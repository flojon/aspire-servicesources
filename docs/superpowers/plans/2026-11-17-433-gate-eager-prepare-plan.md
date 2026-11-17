# Gate a changed prepare command on the eager path and across a checkout group (#433) Implementation Plan

> For agentic workers: implement task by task, test first (red, then green), one commit per task.

**Goal:** A changed catalog `prepare` command never runs unprompted. Explicit `SetCheckoutTiming(Eager)` and
non-deferrable kinds fail `AddService`; default-timing group members defer together; a decline is remembered per
(checkout name, command hash) for the process.

**Spec:** `docs/superpowers/specs/2026-11-17-433-gate-eager-prepare-design.md` (Approved). Decisions D1-D5 are
referenced below. Human answers: Q1 accepted (fails everywhere, CI included), Q2 documented marker/checkout deletion
only (no new mechanism or config), Q3 build `_gatedCheckouts`.

**Architecture:** All logic in `src/Aspire.Hosting.ServiceSources/Sources/DeferredCheckout.cs` (policy, sets,
decline memory) plus one call site in `Sources/LocalProjectSource.cs` `Resolve`. No public API change, so
`PublicAPI.Unshipped.txt` stays untouched (confirm: build has no RS0016/RS0017).

**Test placement and traits:** New tests go in the existing classes `test/Aspire.Hosting.ServiceSources.Tests/Prepare/PrepareDeferredTests.cs`
(helpers `WarmCheckoutAsync`, `StepFor`, `FakeRunner`, `Journal`, fake interaction, `ManualTimeProvider`) and
`test/Aspire.Hosting.ServiceSources.Tests/Sources/DeferredCheckoutTests.cs`. Both already carry
`IO`/`Concurrency`/`Timing` class traits, which cover the new tests. If any new test class is created instead, it
gets `[Trait("IO", "true")]` (real marker files, `git`) and `Concurrency`/`Timing` only if it truly races tasks or
waits on wall clock (fake time and `ManualTimeProvider` are not Timing).

**Test-run command per task:** `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~PrepareDeferredTests|FullyQualifiedName~DeferredCheckoutTests"`.

---

## Task 1: Extract `NeedsChangedCommandGate`, add `IsExplicitEager` (pure refactor plus accessor)

**Test first** (`DeferredCheckoutTests`):
- `IsExplicitEager_FalseByDefault_TrueAfterSetTimingEager_FalseAfterLaterDeferred` (builder-level: `DeferredCheckout.For(builder)`, `SetTiming(Eager)`, `SetTiming(Deferred)`; D2 last call wins).
- `NeedsChangedCommandGate_DebrisDirectoryNoGit_FalseAndDoesNotThrow`.
- `NeedsChangedCommandGate_WarmChangedManaged_True`, `..._Cold_False`, `..._UnchangedHash_False`, `..._FirstUseNoMarker_False`, `..._AlwaysMode_False`, `..._PathSource_False` (call the internal static directly; reuse `WarmCheckoutAsync`/`StepFor`).
- Existing `ShouldDefer_*` tests (`PrepareDeferredTests` 1177-1226) must stay green unchanged: they are the refactor's regression net.

**Implement** (`DeferredCheckout.cs`):
- `internal bool IsExplicitEager { get { lock (_gate) return !_enabled; } }`. Does not latch `_resolved`.
- `internal static bool NeedsChangedCommandGate(builder, serviceName, definition, config, PrepareStep? prepareStep, IGitClient? gitClient)`: returns false when run mode is false, when `LocalGitCheckout.IsColdManagedCheckout` (`IsColdManagedCheckout` is only `!Directory.Exists`, so a debris directory with no `.git` does reach `Decide`, which returns not-changed: no marker, null HEAD sha), when step/git client is null or `!IsManagedCheckout(config)`; otherwise `WouldRunBecauseCommandChanged(..., ManagedRepoRoot, managedCheckout: true, gitClient)`.
- `ShouldDefer`: keep the cold-first order (its cold clause still returns true before the gate is called; the gate re-checks run mode and cold harmlessly). Keep the `_resolved` latch, `_enabled`, run-mode and cold clauses in place; replace the trailing warm-and-changed expression with `NeedsChangedCommandGate(...)`.

**Verify:** run-command above green; commit "Extract the changed-command gate predicate (#433)".

## Task 2: Block eager and non-deferrable services (D1)

**Tests first** (`PrepareDeferredTests`, around the existing `ShouldDefer_*`/changed-command section; each asserts the fake runner `Journal` shows no call and the marker file still holds the old hash):
1. `ExplicitEager_WarmChangedCommand_AddServiceThrowsNamingServiceAndTheWayOut`: message contains the service name, the redacted `DescribeCommand` text, the marker path, and the `SetCheckoutTiming(Deferred)` hint.
2. `NonDeferrableKind_SupportsDeferredCheckoutFalse_WarmChangedCommand_Throws` (message omits the timing hint, still names the marker/checkout hatch) and `KindResolveDeferredReturnsNull_WarmChangedCommand_Throws` (no step ran, marker unchanged). First extend `StandInKind` (hardcoded `SupportsDeferredCheckout = true`, `ResolveDeferred` never null) with ctor flags `supportsDeferred = true` and `resolveDeferredReturnsNull = false`.
3. `ExplicitEager_WarmUnchangedCommand_RunsAsToday`, `ExplicitEager_ColdCheckout_NoMarker_RunsAsToday` (no throw).
4. `AlwaysMode_PublishMode_PathSource_ChangedHash_DoNotThrow`.
5. `DefaultTimingDeferrableKind_ChangedCommand_StillDefersAndPrompts` (regression) and `SetCheckoutTimingDeferredAfterEager_ClearsTheBlock`.

**Implement** (`LocalProjectSource.Resolve`): after the deferred branch falls through (`registered is null`) and before the `CheckoutNameLock` acquire / `prefetch.GetRepoRoot` (so no fetch or ref checkout happens for a blocked service), add:

```csharp
if (prepare.Step is not null
    && DeferredCheckout.NeedsChangedCommandGate(builder, serviceName, definition, config, prepare.Step, gitClient))
{
    throw DeferredCheckout.BlockedChangedCommand(serviceName, definition, prepare.Step, builder.AppHostDirectory,
        canDefer: !deferred.IsExplicitEager && !deferredAttempted);
}
```

`deferredAttempted` is a local set true whenever the `ShouldDefer` branch ran and `registered` came back null, or the kind is non-dotnet and `SupportsDeferredKind` was false: any fall-through for a kind-level reason must omit the timing hint (a kind that returns null from `ResolveDeferred` would otherwise be told to remove Eager, which cannot help). `BlockedChangedCommand` (internal static, `DeferredCheckout.cs`) builds the `ServiceSourcesConfigurationException.For(...)` message with `Raw`/`Name` like neighbours: service, "the catalog's prepare command changed since it last succeeded in this checkout", redacted command, then the way out in D3 order (timing hint only when `canDefer`; then delete the marker `PrepareMarker` path or the checkout directory, noting the next start is a first use that runs unprompted). It must run before the marker is written, so nothing is recorded.

**Verify:** run-command green; commit "Fail AddService instead of running a changed prepare command unprompted (#433)".

## Task 3: `_gatedCheckouts` order-independence hardening (D4, Q3)

**Tests first** (`DeferredCheckoutTests`/`PrepareDeferredTests`):
6. `BlockedMember_NeverWritesTheMarker_DeferredSiblingStillPromptsAddedBeforeOrAfter` (two orders; sibling shares `CheckoutName`; blocked member is explicit-Eager or non-deferrable; after the blocked throw the marker hash is unchanged and the sibling's `ShouldDefer` is still true).
7. `GatedSibling_PullsLaterDefaultTimingSiblingWhoseOwnStepLooksUnchanged_IntoDeferral` and `ExplicitEagerSibling_IsNotPulledIn_ItIsBlockedInstead`; also `GatedCheckouts_NotSharedAcrossBuilders`.

**Implement:** `HashSet<string> _gatedCheckouts` under `_gate`. The new clause requires `prepareStep is not null`, so `LocalCheckoutPrefetch.WouldBeDeferredIfAdded` (which calls `ShouldDefer` without a step) is unaffected; add `GatedCheckoutName_WithoutPrepareStep_DoesNotDefer`. In `ShouldDefer`, when the warm-changed clause yields true, add `CheckoutName`; add a clause (after cold, before the changed clause, only when `_enabled`, run mode, managed checkout) returning true if the name is already in the set. Explicit Eager and non-deferrable kinds never reach it (`_enabled` false returns earlier; non-deferrable kinds fall through `registered is null` to Task 2's block or run unchanged-step eagerly as today). Keep a single source of truth: the set is consulted only inside `ShouldDefer`.

**Verify:** run-command green; commit "Defer a checkout group together once one member is gated (#433)".

## Task 4: Remember a decline per (checkout name, command hash) (D5)

**Tests first** (`PrepareDeferredTests`, next to `ChangedCommand_Declined_*`, using the fake interaction that counts prompts):
8. `ChangedCommand_Declined_SecondSiblingIsSkippedWithoutASecondPrompt` (exactly one prompt recorded; sibling `DeclinedPrepare`, resources `Skipped`, log says it was declined for the shared checkout).
   `DialogClosed`, `Timeout` (advance `ManualTimeProvider`) and `PromptThrows` declines are each remembered.
   `SiblingWithDifferentCommandHash_StillAsks`.
   `HostShutdown_IsNotRemembered` (stoppingToken throws, set stays empty).
   `Approval_IsNotRemembered_SiblingSeesNothingChanged` (marker written, no second prompt, sibling prepares nothing).
   `NoDashboard_RunsUnprompted` regression unchanged.

**Implement** (`DeferredCheckout.cs`): `HashSet<(string CheckoutName, string CommandHash)> _declined` under `_gate`, `MarkDeclined`/`WasDeclined`. Make `StartDeferredAsync` and `ConfirmChangedCommandAsync` instance methods (both are `private static` today and `Deferred` holds no `DeferredCheckout`; `StartPicked` is already an instance method, so only the call sites change). In `ConfirmChangedCommandAsync`: after the `WouldRunBecauseCommandChanged` check and before the interaction lookup, `if (WasDeclined(...)) { log; return false; }`; call `MarkDeclined` immediately before the single final `return false` (reached by Skip, close, timeout and dialog failure alike), after the `ThrowIfCancellationRequested` that throws on host stop, so a shutdown is never remembered. The remembered-decline early return happens before `Gate = AwaitingConfirmation`; the caller already sets `DeclinedPrepare` and the Skipped state, and a test asserts the log text for it. The hash is `PrepareStep.CommandHash`, the value the marker comparison uses.

**Verify:** run-command green; commit "Remember a declined prepare command for the rest of the run (#433)".

## Task 5: Docs, XML docs, CHANGELOG

No tests (spec 6.9); the PR checklist covers them. Edits:
- `SECURITY.md` (lines ~51-62): replace the "also keep running a changed command unprompted" and "removes the prompt for the whole repository" sentences with: eager/non-deferrable services fail `AddService`; the escape hatch (switch to deferred timing, or delete the marker / checkout, with the marker path); a first use stays ungated; a decline is remembered per checkout and command for the run.
- `docs/sources/repository.md`: "A changed command asks first" section (~280-306: drop "keep today's unprompted behaviour", drop "after a decline the next one asks again", describe the block and the hatch), the `SetCheckoutTiming(Eager)` mentions at ~94, ~584, ~633, ~641 (note the changed-command failure).
- XML docs on `CheckoutTiming.Eager` and `SetCheckoutTiming` (find the extension; one sentence on the fail-closed behaviour). Comments follow CLAUDE.md style (short, WHY only).
- `CHANGELOG.md` `[Unreleased]` **Changed** entry: AppHost with `SetCheckoutTiming(Eager)`, or a kind that cannot defer, now fails at `AddService` instead of running a changed prepare command unprompted, with how to continue (default timing, or delete the marker); decline remembered across grouped siblings; link `([#433])`. Append `[#433]: https://github.com/flojon/aspire-servicesources/issues/433` after the last `[#4xx]` ref (`[#481]`); `[#298]` sits out of order at ~2207, so do not use it as the anchor. Check `([#433])` in the entry resolves to that ref. Mention the overlap with PR #523 in the PR body, not here.

**Verify:** `grep -n unprompted` over `SECURITY.md`, `docs/sources/repository.md` and the `SetCheckoutTiming` XML docs in `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs` (~279) so no doc still claims eager/non-deferrable services run a changed command unprompted; commit "Document the changed-command gate on the eager path (#433)".

## Task 6: Full verification (once, before PR)

Legs from the notes file: `dotnet restore`; `dotnet build -c Release --no-restore -warnaserror` (also proves no `RS0016`/`RS0017`, i.e. no public API change and `PublicAPI.Unshipped.txt` untouched); `dotnet test -c Release --no-build`; then, once pre-PR, the pack and release-shape pack legs. Run `dotnet test -c Release --no-build --filter "Concurrency=true|Timing=true"` three times for the touched classes to check flakiness. CI-only legs (smoketest scripts, typecheck-typescript, verify-invariants, aspire-matrix, net11-preview) are named as not run locally.

## Self-check against the spec

D1 Task 2; D2 Task 1; D3 Tasks 2 and 5; D4 Task 3; D5 Task 4; tests 1-8 map to Tasks 1-4; docs/CHANGELOG Task 5; public API none, confirmed by Task 6's `-warnaserror` build.
