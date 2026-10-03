# Aspire.Hosting.ServiceSources — Gating a cold checkout behind an interactive source pick

**Date:** 2026-09-30
**Status:** Draft (human decisions applied, including the bounded wait; one review round applied)
**Resolves:** GitHub issue #271.
**Does not build:** #8 (offline `configure` CLI), #70 (post-start "Change source" command).

## Motivation

Since #404 a cold managed checkout is deferred by default, but the clone is still started from
`DeferredCheckout.Add` at composition (`prefetch.StartCheckout(...)`), so every declared `repository`
service with no checkout is cloned on first run whether or not the developer wants it. The developer
should be asked once, with the dashboard up, which ones to run. Aspire's model is static, so the pick
only chooses among declared services.

## Findings that constrain the design

1. **Only a cold managed checkout is gated (settles "path kind?").** `ShouldDefer` claims exactly
   `LocalGitCheckout.IsColdManagedCheckout` in run mode with timing `Deferred`. `path` services, warm
   checkouts, publish mode, kinds declining deferral never reach the prompt, and have nothing to
   save. `SetCheckoutTiming(Eager)` makes `ShouldDefer` return false, so `Register` is never reached and "Eager keeps old behaviour,
   no prompt" needs no code.
2. **The pick never stops a warm checkout from starting.** Once cloned (by a prior pick, by hand, or
   by a shared-repository sibling) the next run is eager. Permanent off is the `"disabled"` source.
   Must be documented.
3. **"Requested" is two things.** `StartCheckout` starts the clone and records `_requested` /
   `_servicesOnCheckout`; `_requested` is read by `UnusedCheckouts()` (the `UnusedCheckoutsMessage`, the failed-unused notices and
   `ReportSpeculativeWork`), which names `_checkouts` entries with no requested service. The speculative set already excludes cold managed
   checkouts in run mode (#76, #217), so an unpicked service has no `_checkouts` entry; it must still
   be recorded requested in case a shared-repository sibling starts the clone.
4. **`BeforeStartEvent` is awaited by host startup before any resource is created.** An awaited prompt
   inside the handler would hold back every resource in the AppHost. The prompt is therefore awaited by
   a background task started from the handler (like `StartAll`), not by the handler itself; this is the
   one reading of the ticket's "awaited prompt" that does not block unrelated resources.
5. **DCP publishes `NotStarted` for withheld resources after the handler.** Any state published here
   must first await that state per resource (as `StartDeferredAsync` already does).
6. **`IInteractionService` (13.5.2):** `IsAvailable`; `PromptInputsAsync(title, message,
   IReadOnlyList<InteractionInput>, options, ct)` -> `InteractionResult<InteractionInputCollection>`.
   `InputType` has no multi-select: "checkbox per service" is one `Boolean` input each. Readiness at
   `BeforeStartEvent` is unverified (Spike).
7. **Managed URLs can carry credentials:** dialog and logs use `GitUrl.Redact`.
8. **`.servicesources/` (`ToolDirectory`) is package-owned, self-git-ignored**, and already holds
   machine-written state.

## Design

### Flow

`Add` stops calling `StartCheckout`; it calls a new `prefetch.MarkRequested(serviceName, definition)`
that records `_requested` and `_servicesOnCheckout` only. Registration (`WithExplicitStart()`, held-back
helpers) is unchanged. The `BeforeStartEvent` subscriber:

1. Snapshots deferred services.
2. Reads the saved selection; splits into decided / undecided.
3. Saved decisions are honoured whether or not a prompt can be shown. Undecided and
   `IsAvailable == false`: every undecided service is treated as picked, one Information line.
   Undecided and available: one background, non-awaited, never-throwing task (tracked in `_startTasks`)
   shows the prompt, which is bounded by `PromptTimeout` (see The prompt). Decided services start immediately and independently of the prompt; the prompt's own batch starts when it is answered. `StartCheckout` must have been called for every picked or saved-`true` service before its `StartDeferredAsync` runs: otherwise `GetRepoRoot` finds no `_checkouts` entry and clones synchronously on the calling thread, bypassing `_checkouts` (order matters for shared-`CheckoutName` siblings).
4. Picked: `StartCheckout` for the whole batch before any blocks (picked clones still overlap each
   other; overlap with composition is lost, inherent to gating), then the existing `StartDeferredAsync`.
5. Unpicked: published **Skipped** after its withheld resources reach `NotStarted`; never cloned,
   prepared, or started.

### The prompt

One dialog, "Choose services to check out", only undecided services, catalog order, one `Boolean`
input each, default checked (accepting unchanged equals pre-#271 behaviour), label = service name,
description = redacted URL and ref, plus the dependents line below. Positional input names (`s0`...) mapped back, since service names
are free text. Text is plain (markdown disabled on the inputs), every interpolated value goes through the
package's escaping seam, and the URL through `GitUrl.Redact`. The prompt opens immediately; separately, each undecided service publishes custom state "Awaiting source selection" once its withheld resources reach `NotStarted` (Finding 5), replaced by Skipped or "Checking out" on the answer, or by "Checking out" on expiry. The checkbox reads "Clone and start"; unchecked means only "do not clone for me this run" (see the warm rule). The dialog message and title are plain text too: `EnableMessageMarkdown` and each input's `EnableDescriptionMarkdown` are set false explicitly (names verified in the Spike). Values shown are scheme, host and path of the URL only (no query or fragment; `GitUrl.Redact` alone does not strip query-string tokens), ref and URL length-capped, all through `Raw.Escaped`; service names are not shown through `Name`, which truncates. The prompt is **bounded**: an internal constant `PromptTimeout` of 5 minutes (no user-facing setting; a configurable value belongs to #8). Its token is a `CancellationTokenSource(PromptTimeout)` linked with `ApplicationStopping`, so both end the wait; which one fired is told by `ApplicationStopping.IsCancellationRequested`. The deadline is computed once when the prompt opens (from `TimeProvider`, so tests control it) and stated in two places: the dialog message ("Answer within 5 minutes (by HH:mm); after that all of these start this run and nothing is saved. Closing this dialog does the same") and the custom state text, "Awaiting source selection (starts automatically at HH:mm)" (local time; the 5 minutes is formatted from the constant, not typed twice). On expiry the dialog is closed/abandoned by cancelling the prompt token (Spike (e) confirms the dashboard removes it). A missing, renamed or non-boolean answer for a service means "start" with a warning.

| Outcome | Result |
|---|---|
| Accepted | Checked start, unchecked Skipped; both persisted. |
| Dismissed/cancelled | Every undecided service starts this run; nothing persisted. Stated in the dialog text ("Closing this dialog starts all of them this run"); chosen because it equals pre-#271 behaviour. |
| Timed out (`PromptTimeout`, no answer) | Same as dismissed: the dialog is closed, every undecided service starts this run, nothing persisted; one Information line says the wait expired. Covers unattended runs (CI with a dashboard, containers, `DistributedApplicationTestingBuilder`). |
| Host stopping (before the deadline) | Nothing starts, nothing persisted. |
| Prompt throws | Logged; treated as "cannot be shown": undecided start. |

### Unpicked state: distinct `Skipped`

Publish custom state text `Skipped` (info style) on the service and every held-back resource, the
pattern of `PublishCheckingOutAsync`. `NotStarted` leaves the dashboard Start command enabled on a
resource registered against a project that is not on disk, so it fails confusingly and reads as a
stall; a custom state text disables it and reads as a decision. Cost: not startable in-session until
#70. A consumer that `WaitFor`s a skipped service keeps waiting (decided; see Dependents).

### Dependents of a skipped service (decided)

The wait is left as is and documented: it is the developer's own declared ordering, and dropping it
(as `"disabled"` does) would start the consumer against a dependency that is not coming. To make the
consequence visible the dialog lists, per service, what waits on it: "Waited on by: billing-api, web".

Discovery, at `BeforeStartEvent` from `@event.Model.Resources`: a resource is a dependent of service S
when one of its `WaitAnnotation`s targets S's real resource, one of S's held-back helpers, or a
`ServiceResource` facade whose `ServiceSourceAnnotation` matches S's real resource (both shapes must be
handled, and a facade and its real resource both appear in the model, so de-duplication keys on the service name carried by `ServiceSourceAnnotation`, not on the resource name: `ServiceWaitRetargeting` rewrites facade waits to the real resource in its own
`BeforeStartEvent` subscriber, and order between subscribers is not relied on). Helpers of S itself are
not dependents. A dependent is shown by the service name its `ServiceSourceAnnotation` carries, otherwise by resource name. Dependents
are followed transitively (a consumer of a consumer also stalls), listed once, sorted ordinally, capped
at five names then "and N more". Dependents that are themselves unpicked are still listed. The line is
computed before the dialog opens, so it reflects the model at that moment only.

### Persisting the pick

`.servicesources/selection.json`, package-written:

```json
{ "version": 1, "services": { "orders": { "start": true }, "billing": { "start": false } } }
```

Keyed by the catalog's canonical service-name spelling. `start` is an object property so later fields
are not a format break.

Why not `servicesources.local.json`: hand-authored (comments, ordering), lowest layer of the
configuration chain (higher layers shadow it), not at a path the package owns; rewriting clobbers hand
edits. `"source": "disabled"` removes the resource at composition (needs restart, discards the declared
source).

- **A saved decision skips the prompt for that service and is honoured even when the prompt could not
  be shown** (non-interactive, dashboard disabled, CI). The prompt appears only when some cold service
  has no entry (new service, cleared entry, first run); with no entry and no prompt possible, the
  service starts.
- Saved `true` runs the normal flow; saved `false` is Skipped without cloning.
- Trust and parsing: the file is local, git-ignored, and read as data only. It selects among services the AppHost
  already declares; it never supplies a URL, path, ref, command or any value that reaches a process or
  the file system. Unknown names are ignored. It is read through a bounded stream (at most 64 KiB + 1 byte; over the limit is
  invalid), opened only at the fixed path, and a non-regular file (directory, reparse point) is invalid.
  `version` must be integer 1; greater is "newer" (never overwritten); anything else is invalid. `start`
  must be a strict JSON boolean (no coercion). Keys match ordinal-ignore-case (as developer config does)
  and are written in catalog spelling; duplicate keys are last-wins (no detection, which would not be
  portable across net8/9/10).
- After an accepted dialog, answered services are merged into the file. Entries for undeclared names
  are preserved.
- Write: `ToolDirectory.Ensure` first (so the file is git-ignored), then the `PrepareMarker` write/read
  helpers (unique scratch name in the same directory, rename with Windows-race retries, shared-delete
  reads), re-reading just before merging; racing AppHosts last-writer-wins. A failed save is a warning
  and the run proceeds.
- Saved `false` means only "do not clone for me"; if the checkout is already warm (prior pick, by hand,
  or a shared-repository sibling) the service is eager and never reaches the gate, so the entry is not
  consulted. Entries for undeclared names are preserved until the 64 KiB cap, which is accepted.
- Invalid file: warning naming the path, treated as empty, overwritten only after a successful answer.
  A newer `version` is never overwritten (prompt still runs, result unsaved, warning says why).
- Reset = delete the file or one entry; documented. No new public API.
- Read at `BeforeStartEvent`, so composition, `servicesources.local.json` validation and the
  `AspireExport` surface are untouched. Publish, inspect and Eager never consult it; the subscriber is a no-op when no service was deferred.

### Shared repositories (#291)

Pick is per service, checkout per `CheckoutName`: one picked member clones once; unpicked members are
Skipped for this run only and start on a later run via the warm path (Finding 2).

### Reporting and docs

Information line when a saved selection is applied (file, how to clear); one per Skipped service in its
resource log; `UnusedCheckoutsMessage` remedy becomes "cloned only when it is added and picked";
docs/sources/repository.md (prompt, Skipped, reset, warm rule), docs/guides/configuration.md if it
covers deferral; `CHANGELOG.md` Changed entry in the register of the #216 entry, naming
`SetCheckoutTiming(Eager)` as the way back (decided: no other escape hatch now; a selection override
belongs to #8).

Decisions recorded from review: persistence stays in `.servicesources/selection.json`; the early
`StartCheckout` in `Add()` is removed for all services, including those with a saved `start: true`
(composition overlap is given up); the distinct `Skipped` state stays.

### Alternatives

| | Keep NotStarted | Distinct Skipped (recommended) |
|---|---|---|
| Start on a service with no checkout | enabled, fails | disabled |
| Reads as a decision | no | yes |
| Code | none | one publish helper |

## Spike (first task)

On 13.5.2, from a background task launched by a `BeforeStartEvent` handler, establish: (a) `IsAvailable`
true with the dashboard, false when disabled / non-interactive, and its value under
`DistributedApplicationTestingBuilder`; (b) `PromptInputsAsync` queues and
renders once the dashboard connects; (c) cancel returns the cancelled outcome; (d) a custom state text disables the dashboard Start command
where `NotStarted` does not, and Skipped survives later DCP updates to the held-back resources (if not,
re-publish on change). Names of the markdown options. (e) Cancelling the token passed to `PromptInputsAsync` after the deadline removes the dialog from the dashboard and returns the cancelled outcome. If (e) fails (dialog stays open), the expiry still starts the services and the dialog is left until answered, with a late answer ignored and a warning; record it and tell the human. If (a) or (b) fail, return
to the human: awaiting from `AfterResourcesCreatedEvent` deadlocks consumers that `WaitFor` a deferred
service (see the comment on `EnsureSubscribed`).

## Testing

Dependents: facade-shaped and real-shaped waits, transitive, capped. Fake `IInteractionService` and fake `TimeProvider`: accepted subset, dismissed, throws, unavailable, timed out (all undecided start, nothing persisted, token cancelled, deadline text present in state and message, host stopping before the deadline starts nothing); saved decisions all / some /
none; malformed and newer-version files; shared group with one picked; Eager, publish, `path`, warm never
prompt; Skipped on service and held-back helper; no clone for skipped; `UnusedCheckoutsMessage` stays
null. `dotnet test -f net10.0` per round; full matrix once before landing.

## Open Questions

None. (The unattended-prompt question is resolved by the bounded `PromptTimeout`; it also resolves the earlier concern that `Eager` was the only escape hatch for unattended runs.)
