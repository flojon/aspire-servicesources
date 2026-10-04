# Gate a cold checkout behind an interactive source pick Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A cold managed `repository` checkout is no longer cloned from `Add()`; with the dashboard up the developer is asked once which undecided cold services to clone and start, the answer is persisted in `.servicesources/selection.json`, unpicked services show a distinct `Skipped` state, and an unanswered prompt gives up after a bounded wait and starts everything (#271).

**Architecture:** `DeferredCheckout.Add` records the service as requested (`MarkRequested`) instead of starting the clone. The existing `BeforeStartEvent` subscriber splits deferred services into decided (saved selection) and undecided; decided and "prompt unavailable" services go through the unchanged `StartCheckout` -> `StartDeferredAsync` flow, undecided ones get a background, never-throwing prompt task bounded by an internal `PromptTimeout` (5 minutes) linked with `ApplicationStopping`. A small package-owned store reads/writes `selection.json`.

**Tech Stack:** C# / .NET (net8.0;net9.0;net10.0), Aspire.Hosting 13.5.2 (`IInteractionService`), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-30-271-cold-checkout-source-pick-design.md` (Draft, human decisions applied). Read it first; "Finding N" and section names below refer to it.

## Global Constraints

- Source in `src/Aspire.Hosting.ServiceSources`, tests in `test/Aspire.Hosting.ServiceSources.Tests`.
- No new public API, no user-facing setting for the wait (`PromptTimeout` is an internal constant, 5 minutes; configurability is #8). Do not build #8 or #70.
- Only a cold managed checkout in run mode with timing `Deferred` is gated (Finding 1); `path`, warm, publish, `Eager`, non-deferring kinds never prompt and never read `selection.json`.
- "As if the prompt were unavailable" means: every undecided service gets `StartCheckout` then the existing `StartDeferredAsync`; nothing is persisted.
- Dialog/state/log text built from service names, URLs, refs goes through the escaping seam (`Raw.Escaped`, `Raw.Join`, `Raw.Literal`/`Raw.Compose`); URLs show scheme/host/path only after `GitUrl.Redact`.
- Time-dependent code takes a `TimeProvider` so tests drive the deadline; no real 5-minute waits in tests.
- Code comments: only the non-obvious WHY, one short sentence, no ticket/PR references.
- Verify legs (from the ticket notes):
  - Build, CI parity: `dotnet restore; dotnet build -c Release --no-restore -warnaserror` (public types need XML docs; run before push).
  - Cheap leg, every task: `dotnet test -f net10.0` (iterate with `--filter`).
  - Expensive leg, once, after the final pre-land rebase and before marking the PR ready: `dotnet test` (net8.0;net9.0;net10.0).
  - CI-only, not run locally: pack + prerelease-bound scan, `aspire-matrix.yml`, `net11-preview.yml`.

## Review Focus

Failure modes the spec implies that no headline test covers, most likely first. Each has a test in the owning task.

1. A picked or saved-`true` service whose `StartDeferredAsync` runs before `StartCheckout` clones synchronously on the calling thread and bypasses `_checkouts` (Task 1, Task 6).
2. Timeout vs host stopping: expiry must start everything, `ApplicationStopping` must start nothing; they differ only by which token fired (Task 6).
3. `Skipped` is overwritten by the `NotStarted` DCP publishes after the handler unless published after awaiting `NotStarted` per resource (Task 3).
4. Shared `CheckoutName` siblings: one picked member clones once, unpicked members Skipped without a second clone (Task 6).
5. A malformed, oversized, newer-version or non-regular `selection.json` must warn, behave as empty and never be overwritten when newer (Task 2, Task 7).
6. Dependents of a service named only through a `ServiceResource` facade, and facade/real duplicates (Task 4).

---

## File Structure

- Create `Sources/SourceSelectionStore.cs`: read/validate/merge-write `.servicesources/selection.json`.
- Create `Sources/SourcePrompt.cs`: builds the dialog (inputs, text, deadline) and maps the answer back.
- Create `Sources/SkippedDependents.cs`: dependents discovery from the model.
- Modify `Sources/LocalCheckoutPrefetch.cs`: add `MarkRequested`; fix `UnusedCheckoutsMessage` remedy text.
- Modify `Sources/DeferredCheckout.cs`: widen the private nested `Deferred` record to `internal` in Task 1 so Tasks 3-6 and their tests can see it; `Add`, the `BeforeStartEvent` subscriber, `Skipped` publishing, prompt task.
- Docs: `docs/sources/repository.md`, `docs/guides/configuration.md` (only if it covers deferral), `CHANGELOG.md`.
- Tests: new `Sources/SourceSelectionStoreTests.cs`, `Sources/SourcePromptTests.cs`, `Sources/SkippedDependentsTests.cs`; extend `Sources/DeferredCheckoutTests.cs`, `Sources/LocalCheckoutPrefetchTests.cs`.

Task order is real: 0 can veto the design; 1 removes the early clone and installs the start seam that every later task hangs behaviour on (it keeps the suite green by starting everything); 2, 4 and 5 are pure units with no dependency on each other (5 consumes the dependents text from 4 as plain strings, so it needs only the `For` signature); 3 needs 1; 6 needs 1-5; 7 needs 2 and 6; 8-10 need 6 and 7.

---

### Task 0: Spike on Aspire 13.5.2

Throwaway code in the scratchpad or an uncommitted test AppHost; only the findings are committed.

**Interfaces:** Produces a "Spike results" section appended to the spec (committed) answering (a)-(e) of the spec's Spike section plus the markdown option names.

- [ ] **Step 1: Write the probe** (each question is an assertion with a recorded answer): a `BeforeStartEvent` handler that starts a background task which reads `IInteractionService.IsAvailable`, then calls `PromptInputsAsync` with one `Boolean` input and a `CancellationTokenSource(TimeSpan.FromSeconds(20))` linked with `ApplicationStopping`.
- [ ] **Step 2: Answer, with the dashboard running** (scratch AppHost): (a) `IsAvailable` with dashboard, with the dashboard disabled/non-interactive, and under `DistributedApplicationTestingBuilder`; (b) the prompt queues and renders once the dashboard connects; (c) closing the dialog returns the cancelled outcome; (d) a custom state text disables the Start command where `NotStarted` does not, and survives later DCP updates to a held-back resource; (e) cancelling the passed token after the deadline removes the dialog from the dashboard and returns the cancelled outcome; plus the names of the markdown options on the message and inputs.
- [ ] **Step 3: Record and gate.** Append the answers to the spec, commit. If (a) or (b) fails, STOP and return to the human (see the spec's Spike section). If (d) shows `Skipped` is not sticky, Task 3 must re-publish on resource change. If (e) fails, apply the spec's fallback in Task 6.
- [ ] **Step 4: Commit** (`Record the #271 spike results`).

### Task 1: Stop cloning from `Add`; install the start seam

**Files:** Modify `Sources/LocalCheckoutPrefetch.cs`, `Sources/DeferredCheckout.cs`. Test `Sources/LocalCheckoutPrefetchTests.cs`, `Sources/DeferredCheckoutTests.cs`.

**Interfaces:**
- Produces `LocalCheckoutPrefetch.MarkRequested(string serviceName, ServiceDefinition definition)`: records `_requested` and `_servicesOnCheckout` only, starts nothing.
- Produces in `DeferredCheckout` a private `StartPicked(IReadOnlyList<Deferred> picked, ...)`: calls `prefetch.StartCheckout` for the whole batch first, then launches `StartDeferredAsync` per service into `_startTasks`.

- [ ] **Step 1: Write the failing tests.** `MarkRequested_StartsNoClone_AndCountsAsRequested` (fake git client records zero clone calls; `UnusedCheckoutsMessage` stays null for that service). `Add_DoesNotStartTheClone` (register a cold deferred service, assert no clone before `BeforeStartEvent`). `Handler_WithNothingGating_StartsEveryColdService` (publish `BeforeStartEvent`; clone starts, service starts exactly as before). `StartPicked_CallsStartCheckoutBeforeAnyStartDeferred` (ordering assertion on the fake: all `StartCheckout` calls precede the first `GetRepoRoot`, covering shared-`CheckoutName` siblings, Review Focus 1). Run: fail.
- [ ] **Step 2: Implement.** Add `MarkRequested`; replace the `StartCheckout` call in `Add` (line ~505) with it and delete the now false "clone starts here" comment; make the `BeforeStartEvent` subscriber call `StartPicked` with every deferred service (the seam; later tasks narrow it). Keep `WithExplicitStart()` registration unchanged.
- [ ] **Step 3: Run** `dotnet test -f net10.0`; the existing deferred-checkout suite must stay green (behaviour is unchanged except the clone now starts at `BeforeStartEvent`).
- [ ] **Step 4: Commit.**

### Task 2: `SourceSelectionStore` (read, validate, merge-write)

**Files:** Create `Sources/SourceSelectionStore.cs`. Test `Sources/SourceSelectionStoreTests.cs` (new).

**Interfaces:**
- Produces `SourceSelectionStore.Read(string toolDirectory)` -> `(Dictionary<string,bool> Decisions, SelectionFileState State)` where `State` is `Missing | Valid | Invalid | Newer`; ordinal-ignore-case keys.
- Produces `SourceSelectionStore.TrySave(string toolDirectory, IReadOnlyDictionary<string,bool> answered)` -> `bool`: `ToolDirectory.Ensure` first, re-read, merge (undeclared entries preserved), write via the `PrepareMarker` helpers; never overwrites `Newer`; a failed save returns false.

- [ ] **Step 1: Write the failing tests**, one per rule in "Persisting the pick": missing file; valid file; `start` must be a strict boolean (`"true"`, `1` -> Invalid); `version` 1 only, `2` -> Newer, `"1"`/`1.5`/missing -> Invalid; duplicate keys last-wins; case-insensitive keys written back in catalog spelling; over 64 KiB + 1 byte -> Invalid; directory or reparse point at the path -> Invalid; unknown names ignored on read and preserved on save; `Newer` never overwritten; `Invalid` overwritten only by a save; save creates the git-ignore via `ToolDirectory.Ensure`; failed write returns false and leaves the old file; two saves merge (last writer wins per key). Run: fail.
- [ ] **Step 2: Implement** with a bounded stream read (64 KiB + 1), `System.Text.Json` document parse with explicit type checks (no coercion).
- [ ] **Step 3: Run** `dotnet test -f net10.0 --filter SourceSelectionStoreTests`, then the full cheap leg.
- [ ] **Step 4: Commit.**

### Task 3: `Skipped` state and the skip path

**Files:** Modify `Sources/DeferredCheckout.cs`. Test `Sources/DeferredCheckoutTests.cs`.

**Interfaces:**
- Produces `PublishSkippedAsync(ResourceNotificationService, Deferred)`: awaits `NotStarted` for every withheld resource (Finding 5), then publishes custom state `Skipped` (info style) on the service and each held-back resource, and logs one Information line in the service's resource log.
- Consumes the Task 1 seam: the subscriber routes each service to start or skip.

- [ ] **Step 1: Write the failing tests.** `Skipped_IsPublishedOnServiceAndHeldBackHelpers_AfterNotStarted` (publish `NotStarted` late; assert `Skipped` is the final state on every resource, Review Focus 3). `Skipped_NeverClonesPreparesOrStarts`. `UnusedCheckoutsMessage_StaysNull_ForASkippedService`. If Task 0(d) found `Skipped` not sticky: `Skipped_SurvivesLaterDcpUpdates`. Run: fail.
- [ ] **Step 2: Implement**, reusing `PublishStateAsync`; make the subscriber able to route a service to skip (not yet reachable from config; unit-tested by calling the internal routing method directly).
- [ ] **Step 3: Run** the cheap leg. **Step 4: Commit.**

Task 3 verifiability note: the routing method is internal and driven directly by its tests; Task 6 is what first connects it to the subscriber, so Task 3 changes no observable AppHost behaviour.

### Task 4: Dependents discovery

**Files:** Create `Sources/SkippedDependents.cs`. Test `Sources/SkippedDependentsTests.cs` (new).

**Interfaces:** Produces `SkippedDependents.For(IEnumerable<IResource> model, IReadOnlyList<Deferred> services)` -> `IReadOnlyDictionary<string, string>` (service name -> the "Waited on by:" text: up to five names plus "and N more").

- [ ] **Step 1: Write the failing tests** for each rule under "Dependents of a skipped service": a `WaitAnnotation` targeting the real resource; targeting a held-back helper; targeting a `ServiceResource` facade whose `ServiceSourceAnnotation` matches (Review Focus 6); facade and real both in the model counted once (keyed by service name, not resource name); helpers of the service itself excluded; transitive; sorted ordinally; capped at five then "and N more"; dependents that are themselves unpicked still listed; displayed by `ServiceSourceAnnotation` service name else resource name. Run: fail.
- [ ] **Step 2: Implement.** **Step 3: Run** the cheap leg. **Step 4: Commit.**

### Task 5: `SourcePrompt` (dialog content and answer mapping)

**Files:** Create `Sources/SourcePrompt.cs`. Test `Sources/SourcePromptTests.cs` (new).

**Interfaces:**
- Produces `SourcePrompt.Build(undecided, dependents, deadline, timeout)` -> title, message, `IReadOnlyList<InteractionInput>` (positional names `s0`...), and `SourcePrompt.Map(result, ...)` -> per-service `Start | Skip | Missing`.
- Produces the deadline strings: dialog message and state text `Awaiting source selection (starts automatically at HH:mm)`, both formatted from the same `PromptTimeout` constant and deadline.

- [ ] **Step 1: Write the failing tests.** One `Boolean` input per undecided service, catalog order, default checked, label = service name, label text "Clone and start"; description shows scheme/host/path only (query/fragment and credentials stripped, length-capped) and the "Waited on by:" line; markdown disabled on message and each input (names from Task 0); service names with markdown/control characters escaped and not truncated; message states the deadline ("by HH:mm") and that closing the dialog does the same; state text states the deadline; `Map` treats a missing, renamed or non-boolean answer as Start with a warning flag. Run: fail.
- [ ] **Step 2: Implement.** **Step 3: Run** the cheap leg. **Step 4: Commit.**

### Task 6: Gate wiring: decided/undecided split, bounded prompt task

**Files:** Modify `Sources/DeferredCheckout.cs`. Test `Sources/DeferredCheckoutTests.cs` with a fake `IInteractionService` and fake `TimeProvider`.

**Interfaces:**
- Produces `internal static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(5)`.
- The subscriber: snapshot -> read selection (Task 2) -> saved `true` start now via `StartPicked`, saved `false` skip now; undecided + `IsAvailable == false` -> start all with one Information line; undecided + available -> one background never-throwing task tracked in `_startTasks`, publishing "Awaiting source selection (...)" after `NotStarted`, awaiting `PromptInputsAsync` with a token from `CancellationTokenSource(PromptTimeout, timeProvider)` linked with `ApplicationStopping`.
- Outcomes per the spec table: accepted (start checked, `PublishSkippedAsync` unchecked), dismissed, timed out (timeout fired and `ApplicationStopping` not set: start all undecided, one Information line), host stopping (nothing starts), throws (logged, start all). Persistence is Task 7.

- [ ] **Step 1: Write the failing tests** (start/skip/state only): accepted subset; dismissed; throws; unavailable; **timed out** (advance the fake clock past `PromptTimeout`: all undecided start, the prompt token is cancelled, Review Focus 2); **host stopping before the deadline** (nothing starts); decided services start without waiting for the prompt; saved all / some / none; shared `CheckoutName` group with one picked (one clone, siblings Skipped, Review Focus 4); `Eager`, publish, `path`, warm never prompt and never read the file; subscriber is a no-op with no deferred service; the state text carries the deadline. Run: fail.
- [ ] **Step 2: Implement**; resolve `IInteractionService` and `TimeProvider` from `@event.Services` (default `TimeProvider.System`). If Task 0(e) failed, apply the spec fallback (late answer ignored, warning).
- [ ] **Step 3: Run** the cheap leg. **Step 4: Commit.**

### Task 7: Persist the answer

**Files:** Modify `Sources/DeferredCheckout.cs`. Test `Sources/DeferredCheckoutTests.cs`.

- [ ] **Step 1: Write the failing tests.** Accepted answer writes both starts and skips in catalog spelling and merges with existing entries; dismissed, timed out, host stopping and throws persist nothing; a `Newer` file is not overwritten, the prompt still runs and a warning says why; an `Invalid` file warns naming the path and is overwritten after an answer; a failed save warns and the run proceeds; one Information line when a saved selection is applied (names the file and how to clear). Run: fail.
- [ ] **Step 2: Implement** via `SourceSelectionStore.TrySave`, called only on the accepted outcome, off the start path so a slow disk never delays a start. **Step 3: Run** the cheap leg. **Step 4: Commit.**

### Task 8: Reporting text

**Files:** Modify `Sources/LocalCheckoutPrefetch.cs`. Test `Sources/LocalCheckoutPrefetchTests.cs`.

- [ ] **Step 1: Write the failing test** (also assert the message still names only `_checkouts` entries with no requested service, i.e. a skipped or unpicked service never appears): the `UnusedCheckoutsMessage` remedy now reads "cloned only when it is added and picked" (change the existing assertion first). Run: fail.
- [ ] **Step 2: Implement.** **Step 3: Run** the cheap leg. **Step 4: Commit.**

### Task 9: Docs

**Files:** `docs/sources/repository.md`, `docs/guides/configuration.md` (only if it covers deferral).

- [ ] **Step 1: Check** whether the repo has a docs link/lint check; run it first if so.
- [ ] **Step 2: Write** the prompt, the 5-minute bounded wait and what it does (starts everything, saves nothing), `Skipped` and that Start is disabled until #70, the warm rule, the `"disabled"` source for permanent off, how to reset (`.servicesources/selection.json`, delete file or entry), the dependents consequence, `SetCheckoutTiming(Eager)` as the way back. No claim of a setting for the wait.
- [ ] **Step 3: Verify** every claim against the Task 6/7 tests; no remaining "waits forever" wording. **Step 4: Commit.**

### Task 10: CHANGELOG `[Unreleased]` Changed entry

**Files:** `CHANGELOG.md`.

- [ ] **Step 1:** Add under `### Changed` (create the heading after `### Breaking`/before `### Added` if the section has none) one bold-lead entry in the register of the #216 entry: what changed (a cold `"repository"` checkout is no longer cloned at `AddService()` in run mode; the dashboard asks which to clone and start, then remembers it in `.servicesources/selection.json`), the unattended behaviour (no answer in 5 minutes, or a dismissed dialog, starts everything as before and saves nothing), what stays the same (warm checkouts, `path`, `aspire publish`, runs with no dashboard), the consequence (a `WaitFor` consumer of a skipped service keeps waiting), and `SetCheckoutTiming(CheckoutTiming.Eager)` as the way back. Add the `[#271]` link reference beside the others.
- [ ] **Step 2:** Re-read against the Keep a Changelog rules at the top of the file (Changed, not Fixed). **Step 3: Commit.**

### Task 11: Verification before landing

- [ ] `dotnet restore; dotnet build -c Release --no-restore -warnaserror` clean.
- [ ] `dotnet test -f net10.0` green (cheap leg).
- [ ] After the final pre-land rebase, before marking ready: `dotnet test` (net8.0;net9.0;net10.0) green.
- [ ] Manual: run a sample AppHost with a cold repository service and the dashboard, once answering, once ignoring the dialog past the deadline (temporarily shorten the constant locally, do not commit that), once dismissing; confirm start/skip/persist match the spec table.
