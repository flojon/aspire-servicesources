# Gating a changed `prepare` command on the eager path and across a checkout group (#433)

**Date:** 2026-11-17
**Status:** Approved (section 9 resolved by the human on 2026-11-17)
**Resolves:** #433, the follow-up to #298 / PR #431 (the consent prompt for a changed `prepare` command
only guards services that resolve on the deferred path).
**Out of scope:** #271 (cold-checkout source pick), #128 (interactive configuration).
**Measured against:** reading the repo at `origin/main` `eedf46be`. Nothing here was run; the plan's tests
re-check each claim marked **[verify]**.

## 1. Problem and what the code does today

The #298 gate asks before running a `prepare` command whose hash differs from the one recorded in the
checkout's marker (`PrepareMarker`, `<checkout>/.git/servicesources-prepare.json`, one per managed
checkout, so shared by every service in a checkout group). It lives entirely on the deferred path:

- `DeferredCheckout.ShouldDefer` returns true for a warm managed checkout when
  `CheckoutPreparation.WouldRunBecauseCommandChanged` is true, but only after the `_enabled` check, so
  `SetCheckoutTiming(Eager)` short-circuits it.
- `LocalProjectSource.Resolve` then only registers deferred if the kind allows it
  (`SupportsDeferredKind`, then `ResolveDeferred` non-null). Otherwise it falls through to the eager path:
  `GetRepoRoot`, then `CheckoutPreparation.RunOrReportSkip`, which runs the changed command unprompted and
  writes the new hash to the shared marker.
- `ConfirmChangedCommandAsync` prompts while holding the per-`CheckoutName` lock, so N grouped siblings
  that are declined ask N times, each up to 5 minutes.

Three gaps follow, all reachable from one catalog edit:

1. **Eager bypass.** Explicit `SetCheckoutTiming(Eager)`, or a kind that cannot defer, runs the changed command
   with no consent.
2. **Group bypass.** Services sharing a checkout share one marker. An eager member that runs and records the
   changed command makes the deferred members see "nothing changed", so the prompt disappears for the whole repository.
3. **Repeated prompts.** A decline is forgotten, so each grouped sibling asks again.

`mode: always` never reports a changed command (`ReasonToRun` returns before comparing hashes) and a first use
(no marker) reports `commandChanged = false`; both stay ungated, as documented in `SECURITY.md`.

## 2. Decisions

Human decisions are D1, D2, D4 (as revised on 2026-11-17); the rest is derived.

### D1. A blocked eager service fails the AppHost; it is never skipped

A service is **blocked** when all hold: run mode, a managed checkout that is warm, a prepare step whose mode compares
the command, `WouldRunBecauseCommandChanged` true, and the service cannot take the deferred path because either
timing is explicitly Eager or its kind cannot defer (`SupportsDeferredCheckout` false, or `ResolveDeferred`
returned null).

A blocked service throws `ServiceSourcesConfigurationException` from `AddService` before it runs anything and
before it writes the marker. It does not skip, because a skipped eager service has nowhere to show `Skipped`
(that state belongs to a deferred resource), would still resolve against a checkout lacking what the step produces,
and would let a dependent start against it. Failing matches how a bad `repository` or `project` already behaves.
It is also correct without a dashboard: composition cannot know whether a dashboard will exist after `Build()`,
and the deferred path's "no dashboard, so run unprompted" fallback (documented, kept) needs a deferred resource.

The message names the service, says the catalog's prepare command changed since it last succeeded in this checkout,
shows the redacted command (`CheckoutPreparation.DescribeCommand`), and gives the way out (D3). It is built with
`Raw`/`Name` like the neighbouring messages.

### D2. "Explicit Eager" is `DeferredCheckout._enabled == false`; no new field

`_enabled` defaults to `true` and the only writer is `SetTiming`, so `false` can only come from
`SetCheckoutTiming(Eager)`. The last call wins, so `SetCheckoutTiming(Deferred)` or `UseDeferredCheckout()` after an Eager
clears it, which matches the existing documented override. A new internal `bool IsExplicitEager` exposes it;
default (non-explicit) timing is simply `!IsExplicitEager`. Explicit `Deferred` is indistinguishable from the default and need not be.

Consequence: explicit Eager is builder-wide and set before the first `AddService`, so a group is either all
explicit-Eager or all default timing. Mixed groups arise only from kinds that differ in deferrability.

### D3. Escape hatch: switch the service to deferred timing, or clear the marker

No trust file, approval setting or new API is added. The message offers, in this order:

- **Remove `SetCheckoutTiming(Eager)`** (or call `SetCheckoutTiming(Deferred)`): the dashboard then asks. Only
  meaningful when the kind can defer; the message omits it when the kind cannot.
- **Run the command once yourself and accept it**: delete the marker file (path shown in the message) or the
  checkout directory. With no marker the next start is a first use, which runs unprompted today. This is existing
  behaviour that already lets the developer, who has to take a deliberate filesystem action, accept the command,
  so documenting it adds no weaker path than exists now (it is also the only way out for a kind that cannot defer).

**The first run with no marker is not "changed"** and is not gated (a cold clone is the developer's consent to
the repository; unchanged from #298). That is what makes the marker-clearing hatch work. Not changed here.

### D4. A group defers together only for default-timing, deferrable members

`DeferredCheckout` keeps a per-builder `HashSet<string> _gatedCheckouts` (under `_gate`), filled by `ShouldDefer`
when a member defers for the changed-command reason. A later member with the same `CheckoutName`, default timing
and a deferrable kind is also deferred even if its own step looks unchanged, so the group goes through
`ConfirmChangedCommandAsync` together instead of one member composing eagerly against a checkout that a sibling is
about to bootstrap. Members with explicit Eager or a non-deferrable kind are never pulled in; they are blocked by D1.

A blocked member never reaches `RunOrReportSkip` and never writes the marker, so it cannot hide the change from
deferred siblings (gap 2). Because the AppHost fails at that `AddService`, the sibling question is moot for the
run that fails; the property matters for the run after the developer fixes it by D3 option one, and for the order
`AddService` calls are made in (a blocked member added first must not have recorded anything before a deferred one asks).

### D5. A decline is remembered per (checkout name, command hash) for the process

`DeferredCheckout` keeps `HashSet<(string CheckoutName, string CommandHash)> _declined` under `_gate`
(`MarkDeclined` / `WasDeclined`). `ConfirmChangedCommandAsync` records the key whenever it returns false for a reason
that is the developer's or the timer's answer: explicit Skip, dialog closed, the 5-minute timeout, and a dialog
that could not be shown (fail-closed). It does not record a host shutdown (`stoppingToken` fired), which throws.
The check runs first in `ConfirmChangedCommandAsync`, after the change test and before the interaction lookup, so
a sibling that gets the lock after a decline returns false immediately: gate `DeclinedPrepare`, withheld
resources `Skipped`, and the log says it was declined for the shared checkout. Siblings with a different command
hash still ask. The lock is still held for the first prompt (unchanged); the saving is siblings 2..N.

An approval needs no memory: the approved run writes the marker, and siblings then see "nothing changed".

## 3. Where the changes go

- `Sources/DeferredCheckout.cs`
  - Extract the warm-and-changed clause of `ShouldDefer` into one internal static `NeedsChangedCommandGate(builder,
    serviceName, definition, config, prepareStep, gitClient)` (warm managed checkout, then `WouldRunBecauseCommandChanged`
    against `ManagedRepoRoot`). `ShouldDefer` and the new eager check both call it, so they cannot disagree.
  - `IsExplicitEager`, `_gatedCheckouts`, `_declined` and their methods.
  - `ConfirmChangedCommandAsync`: decline memory (D5).
- `Sources/LocalProjectSource.cs`, `Resolve`, right after the deferred branch falls through and before
  `prefetch.GetRepoRoot`: if run mode, the catalog step exists and `NeedsChangedCommandGate` is true, throw the D1
  error. Before `GetRepoRoot` so a blocked service does not fetch or check out a new ref first, and the evaluation is
  the same one `ShouldDefer` already made at the same point. **[verify]** that `Decide` does not throw for a cold
  or debris directory here; mirror `ShouldDefer`'s order (cold first) so a cold checkout never reaches it.
  `WouldBeDeferredIfAdded` in `LocalCheckoutPrefetch.cs` already calls `ShouldDefer` without a step, so the prefetch is unaffected.
- Not touched: `CheckoutPreparation` decision logic, `PrepareMarker`, `PathSource` (a `path` or `repository.path`
  service runs the developer's own command in the developer's own directory, never the catalog's; publish mode never
  runs the step, so nothing is gated there).
- Docs: `SECURITY.md` (replace the "also keep running a changed command unprompted" and "removes the prompt for
  the whole repository" sentences with the block and the hatch), `docs/sources/repository.md` (the "A changed command
  asks first" section, the `SetCheckoutTiming(Eager)` bullets near the deferred-checkout section, and the sentence
  "after a decline the next one asks again", which becomes the D5 behaviour), XML docs on `CheckoutTiming.Eager` and
  `SetCheckoutTiming`.
- `CHANGELOG.md`: one **Changed** entry (it still compiles, so not Breaking): an AppHost with
  `SetCheckoutTiming(Eager)`, or a kind that cannot defer, now fails at `AddService` instead of running a changed
  prepare command unprompted; says how to continue; mentions the decline memory. Not **Fixed**: the gate shipped in
  0.8.0 only for the deferred path, so this is a behaviour change, and the entry says so. Link ref `[#433]` added at the
  bottom; the open PR #523 also edits `CHANGELOG.md` (trivial overlap).
- **Public API: none.** No new or changed public member, so `PublicAPI.Unshipped.txt` is untouched. `IsExplicitEager`,
  the sets and the helper are `internal`; the exception type already exists. The `[AspireExport]` surface is unchanged.

## 4. Attack surface

- Trust boundary: the catalog is the untrusted-by-default author of the command; the marker hash is the record of
  what the developer already ran or accepted. The change only narrows what executes without consent.
- New thing executed: nothing. New thing trusted: nothing; the hatch relies on filesystem write access to the checkout,
  which already lets that user run anything.
- Fail closed: a blocked service never runs the command or writes the marker; a decline, timeout, dialog failure or
  `IsAvailable == false` handling is unchanged (the last is still "runs unprompted", deliberately, on the deferred
  path only). A remembered decline is per process and per (checkout, hash), so it cannot carry an approval anywhere.
- Residual: the gate compares the command line, not the scripts it invokes (unchanged; pinning `defaultRef` remains the lever).
  `mode: always` and first use stay ungated by design. A repository with a non-deferrable member and no dashboard
  has only the hatch (section 9, Q1).
- Failure mode of the new throw: it stops the whole AppHost for one service. It can fire in CI against a cached
  checkout that has a stale marker. Accepted as the cost of fail-closed.

## 5. Requirements that could be read two ways, settled

- "Changed" means `WouldRunBecauseCommandChanged` exactly as today, so a first use, `always` mode, a moved commit and
  a re-pointed path never block.
- "Blocked" is judged per service at `AddService` time against the shared marker; it is not a group property (D4 only
  adds deferral of default-timing siblings, never blocking).
- The decline memory is keyed on the command hash, not the service name; siblings with their own override still ask.

## 6. Testing (names in `PrepareDeferredTests` / `DeferredCheckoutTests` style)

Existing classes keep their traits; any new class carries `IO` (real marker files and `git`) and, only if it races
real tasks, `Concurrency`. Fake interaction and `ManualTimeProvider` are not `Timing`.

1. Explicit Eager, warm checkout, changed hash: `AddService` throws, message names the service and the hatch; the
   fake runner was never called and the marker still holds the old hash.
2. Same with default timing and a kind whose `SupportsDeferredCheckout` is false, and one whose `ResolveDeferred`
   returns null; the second must also not have run a step.
3. Explicit Eager, warm, hash unchanged, and cold checkout with no marker: runs as today (no throw).
4. `mode: always`, publish mode, `path` source: no throw.
5. Default timing, deferrable kind, changed hash: still deferred and still prompts (regression); `SetCheckoutTiming(Deferred)`
   after Eager clears the block.
6. Group: a non-deferrable member that is blocked leaves the marker hash unchanged, so a deferrable sibling added
   before or after still reports changed and prompts (both orders).
7. Group: a later default-timing sibling whose own step looks unchanged defers when an earlier sibling is gated;
   an explicit-Eager sibling is not deferred.
8. Decline memory: two siblings, first declines, second is `Skipped` without a second prompt (fake interaction shows
   exactly one prompt); timeout and "dialog could not be shown" are remembered too; a sibling with a different hash
   asks; a host shutdown is not remembered; an approval is not remembered and the sibling sees nothing changed.
9. Docs/CHANGELOG are not tested; the checklist in the PR covers them.

## 7. Verify legs

Those recorded in the notes file (restore, `build -c Release -warnaserror`, `test -c Release --no-build`, pack legs).
The CI-only legs are named there as not run locally.

## 8. Scope

One behaviour change in two components, docs and CHANGELOG. Not included: a persistent trust store, an approval
setting, prompting on the eager path, content-hashing the invoked scripts, and any change to the 5-minute timeout or
to holding the checkout lock during the prompt.

## 9. Open Questions (resolved 2026-11-17)

None open. The human answered all three:

- **Q1: accepted.** A non-deferrable member with a changed command fails `AddService` on every start, in CI as
  well as locally, until the marker or checkout is cleared. No environment-based exemption.
- **Q2: documented marker/checkout deletion is the escape hatch.** No approval setting, developer-config field,
  environment variable, schema or `PublicAPI` change.
- **Q3: build `_gatedCheckouts`** as specified in D4, as order-independence hardening for siblings whose own
  step differs (not a new bypass fix).
