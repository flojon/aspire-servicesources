# Health Checks on a `"kubernetes"` Service: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** `WithHttpHealthCheck` / `WithHealthCheck(key)` on a `"kubernetes"` service applies to the port-forward's real resource instead of being skipped, without a failing user check being misread as a dead tunnel.

**Architecture:** `Reachability.IsUnreachable` swaps its inline `WaitAnnotation` clause for a per-source map of exact annotation types (`"kubernetes"` -> `WaitAnnotation`, `HealthCheckAnnotation`); `DecorationAnnotationTypes` stays a denylist and every other source and type is unchanged. `PortForwardSupervisor` stops reading the resource's aggregate health and reads only the tunnel's own report (`KubernetesSource.HealthCheckKey(serviceName)`), so a user check cannot flip `_ready` or trigger the "no ready pods" warning.

**Tech Stack:** C# / .NET 10, xUnit, Aspire.Hosting 13.x, `Microsoft.CodeAnalysis.PublicApiAnalyzers`.

**Spec:** `docs/superpowers/specs/2026-11-14-399-kubernetes-health-check-reachability-design.md` (status Approved; section 7 holds the human's four decisions, binding).

## Global Constraints

- Exact `Type` identity only: no name matching, no subclass match (a subclass of `HealthCheckAnnotation` is skipped).
- `DecorationAnnotationTypes` stays a denylist; the new allowance is a per-source map keyed by source, so an entry cannot leak to another source.
- `"url"`, `"disabled"` and the unconfigured placeholder: `IsUnreachable(typeof(HealthCheckAnnotation), s)` stays true.
- Only `HealthCheckAnnotation` is added. `ResourceCommandAnnotation`, `EndpointProbeAnnotation`/`ProbeAnnotation`, `ResourceUrlAnnotation` stay gated.
- No public API: `Reachability` is `internal`; `PublicAPI.Unshipped.txt` must not change. No shadow `WithHttpHealthCheck` overload.
- Never relax certificate validation; no `AllowAutoRedirect` change; no startup notice or refusal for `https` (documentation only).
- Code comments: only the non-obvious WHY, under 15 words, no ticket/PR references (global CLAUDE.md).
- Test traits per repo CLAUDE.md: `KubernetesHealthCheckDualWriteTests` carries `[Trait("IO", "true")]` (it builds a real host over a temporary directory); `ReachabilityTests` stays untraited; `PortForwardSupervisorTests` already carries `Concurrency` + `Timing` at class level.
- Verify legs (CI `ci.yml`): `dotnet build -c Release --no-restore -warnaserror`, `dotnet test -c Release --no-build`. Fast subset: `--filter "IO!=true&Timing!=true"`.
- Not run locally (CI only): container/config-layer/local-source smoke tests, typecheck-typescript, verify-invariants. Aspire-facing claims are **[verify]** items in Task 2's tests; nothing is exercised against a live cluster.

## Review Focus

- A subclass of `HealthCheckAnnotation` on `"kubernetes"`: skipped (Task 1 matrix).
- `WithHealthCheck` reusing the built-in `"{service}-port-forward-tcp"` key: Aspire throws its own `DistributedApplicationException`; the tunnel check is not replaced (Task 2).
- A snapshot where only a user check has reported, or the tunnel report has `Status == null`: no readiness and no unhealthy clock (Task 3).
- A healthy tunnel with a failing user check, then kubectl exits: restarted, not `FailedToStart` (Task 3).
- The same call on `"url"`/`"disabled"`/placeholder: still skipped, now labelled `WithHealthCheck/WithHttpHealthCheck` (Tasks 1 and 2).

## File Structure

- Modify `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs`: `Reachability` (allowance map, `IsUnreachable`, `CapabilityLabel`, remarks).
- Modify `src/Aspire.Hosting.ServiceSources/ServiceConfigurationExtensions.cs`: `IsUnreachable<T>` becomes `internal` (agreement test); remarks.
- Modify `src/Aspire.Hosting.ServiceSources/Sources/PortForwardSupervisor.cs`: tunnel-report readiness.
- Modify `test/Aspire.Hosting.ServiceSources.Tests/ReachabilityTests.cs`; create `.../KubernetesHealthCheckDualWriteTests.cs`; modify `.../Sources/PortForwardSupervisorTests.cs`.
- Modify `docs/guides/configuration.md`, `docs/sources/kubernetes.md`, `CHANGELOG.md`.

---

### Task 1: The per-source allowance, the label and the predicate agreement

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs` (`Reachability`, lines ~22-95)
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceConfigurationExtensions.cs` (`IsUnreachable<T>`, ~line 199)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/ReachabilityTests.cs`

**Interfaces:**
- Consumes: existing `Reachability.IsUnreachable(Type, string)`, `Reachability.CapabilityLabel(Type)`, `Reachability.OutOfBandSources`.
- Produces: unchanged signatures; `ServiceConfigurationExtensions.IsUnreachable<T>(string source)` visibility `private` -> `internal` (test-only reach via `InternalsVisibleTo`, no public surface); the spec's "not changed" means behaviour only; `CapabilityLabel(typeof(HealthCheckAnnotation))` returns `"WithHealthCheck/WithHttpHealthCheck"`.

- [ ] **Step 1: Write the failing tests** in `ReachabilityTests`:
  - `Matrix_OfSourceAndAnnotationType_IsUnreachableExactlyWhereExpected` (`[Theory]`/`[MemberData]`): sources `url`, `kubernetes`, `disabled`, `UnconfiguredSource.SourceName` x types `WaitAnnotation`, `HealthCheckAnnotation`, a private `sealed class DerivedHealthCheck : HealthCheckAnnotation` (ctor passes a key), `EnvironmentCallbackAnnotation`, `ResourceCommandAnnotation`, a private unknown `TestAnnotation : IResourceAnnotation`. Expected `false` only for `(kubernetes, WaitAnnotation)` and `(kubernetes, HealthCheckAnnotation)`; `true` for every other cell. Add the two decoration types as `false` for all four sources.
  - `HealthCheckAnnotation_IsLabelledWithTheMethodTheAuthorCalled`: `Assert.Equal("WithHealthCheck/WithHttpHealthCheck", Reachability.CapabilityLabel(typeof(HealthCheckAnnotation)))`.
  - `TheTwoPredicates_AgreeOnWaitFor_ForEverySource`: for the four out-of-band sources plus `"repository"` and `"container"` (so the check is not vacuous for non-gated sources), `Reachability.IsUnreachable(typeof(WaitAnnotation), s) == ServiceConfigurationExtensions.IsUnreachable<IResourceWithWaitSupport>(s)`.
- [ ] **Step 2: Make `IsUnreachable<T>` `internal` first** (no behaviour change) so the class compiles, then run `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~ReachabilityTests"`. Expected: matrix cell `(kubernetes, HealthCheckAnnotation)` and the label test FAIL; the rest PASS.
- [ ] **Step 3: Implement** in `Reachability`: `private static readonly FrozenDictionary<string, FrozenSet<Type>> ReachableBySource` (`StringComparer.Ordinal`; `["kubernetes"] = [typeof(WaitAnnotation), typeof(HealthCheckAnnotation)]`); `IsUnreachable` = out-of-band AND not decoration AND not (`ReachableBySource.TryGetValue(source, out var types)` and `types.Contains(annotationType)`); add the `nameof(HealthCheckAnnotation) => "WithHealthCheck/WithHttpHealthCheck"` case to `CapabilityLabel`. 
- [ ] **Step 4: Rewrite the remarks** (one place each, spec section 4.2 and the rule above the table in section 6): `Reachability.IsUnreachable` summary (kubernetes now has two exceptions), `DecorationAnnotationTypes` remarks (remove `WithHttpHealthCheck` from the list of things that must fail closed on kubernetes), the `ServiceConfigurationExtensions` remarks that say "the one capability that still can" and its `Unwrap<T>`/`IsUnreachable<T>` remarks (it keeps only `IResourceWithWaitSupport`; health checks have no capability interface), and a remark above `ReachableBySource` stating the entry rule: a type joins only if (a) it is data the real resource or Aspire consumes, not applied to the kubectl process, (b) acting on the real resource measures or orders the service behind the tunnel, (c) no invariant elsewhere assumes it unreachable.
- [ ] **Step 5: Run** the same filter, then `dotnet build -c Release -warnaserror`. Expected: PASS; no `RS0016`/`RS0017`.
- [ ] **Step 6: Commit** `Let a health check reach a kubernetes service (#399)` (test + src together).

### Task 2: Dual-write behaviour through the real builder

Depends on Task 1.

**Files:**
- Create: `test/Aspire.Hosting.ServiceSources.Tests/KubernetesHealthCheckDualWriteTests.cs`
- Modify (only if a test exposes a defect): `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs`

**Interfaces:**
- Consumes: `new KubernetesSource(IPortAllocator).Resolve(builder, "orders", ServiceMetadata, ServiceDeveloperConfig)` and the `Url(builder)` helper shape from `CommandDualWriteTests` (copy the helpers, do not share state); `ServiceResourceBuilder.Real`; `ServiceSourcesWarnings.For(builder).Messages`; `KubernetesSource.HealthCheckKey("orders")`.
- Produces: nothing for later tasks.

- [ ] **Step 1: Write the tests.** This task is characterisation: Task 1's seam-level red tests already drove the change, so these are expected to PASS on arrival and a failure means a real defect (fix it in `src`, or report it):
  - `WithHttpHealthCheck_OnKubernetes_AddsOneHealthCheckAnnotationToRealAndFacade_AndRecordsNoSkip`: `service.WithHttpHealthCheck("/healthz")`; assert the real resource has a `HealthCheckAnnotation` whose `Key` starts with `"orders_"`, contains `"/healthz"` and ends with `"_check"`, the same instance is on the facade (`Assert.Same`), and `ServiceSourcesWarnings.For(builder).Messages` is empty.
  - `WithHealthCheck_OnKubernetes_WithUserKey_Applies`: register a check key in `builder.Services.AddHealthChecks()`, call `service.WithHealthCheck("mine")`; annotation on real and facade; no skip.
  - `WithHealthCheck_ReusingTheTunnelKey_ThrowsAndLeavesTheTunnelCheckInPlace`: `Assert.Throws<DistributedApplicationException>(() => service.WithHealthCheck(KubernetesSource.HealthCheckKey("orders")))`; exactly one annotation with that key remains on the real resource. **[verify]**
  - `WithHttpHealthCheck_OnUrl_IsSkipped_NamedByTheMethod`: the single warning contains `"WithHealthCheck/WithHttpHealthCheck"`, the facade has no `HealthCheckAnnotation`.
  - `HealthCheck_OnDisabledAndPlaceholder_IsSkipped`: neither source registers an `EndpointAnnotation`, so `WithHttpHealthCheck` would throw inside Aspire before the gate; use `WithHealthCheck("k")` for both. Resolve the disabled service as `new DisabledSource().Resolve(builder, "billing", <definition>, new ServiceDeveloperConfig { Source = "disabled" })` (see `ServiceConfigurationExtensionsTests.cs` ~line 65) and the placeholder with the static `UnconfiguredSource.Resolve(...)` (arguments as in `UnconfiguredServiceTests`). Assert one warning labelled `WithHealthCheck/WithHttpHealthCheck` and no `HealthCheckAnnotation` on the facade.
  - `ASubclassOfHealthCheckAnnotation_AddedToAKubernetesService_IsSkipped`: `service.WithAnnotation(new DerivedHealthCheck("x"))`; skipped with a warning, absent from the real resource.
  - `AHealthCheckAnnotationAddedDirectlyToTheFacade_OnKubernetes_IsForwardedToTheReal` (the `ForwardingAnnotationsAddedBy` path): `service.Resource.Annotations.Add(new HealthCheckAnnotation("k"))`; present on real; no skip.
  - `WithHttpCommand_OnKubernetes_StillSkips` (scope pin: decision 3): reuse `OverloadProbe.CallWithCommand`; warning names `ResourceCommandAnnotation`; none on real.
  - `TheFacadeBoundEndpointReference_ResolvesToTheAllocatedUrl` **[verify]**: set `AllocatedEndpoint` on the real `EndpointAnnotation` (same instance the facade holds), then `await service.GetEndpoint("https").GetValueAsync()` (or the scheme the helper uses) equals the allocated URL.
- [ ] **Step 2: Run** `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~KubernetesHealthCheckDualWriteTests"`. Expected: all PASS. If a **[verify]** test fails, the spec's claim about Aspire is false: fix the production code only for a real defect, otherwise record the finding in the test's name and the PR body and report it as an open question.
- [ ] **Step 3: Commit** `Pin health-check dual-write per source (#399)`.

### Task 3: The supervisor reads the tunnel's own report

Independent of Tasks 1-2 in code; ordered after them so the behaviour change lands once the allowance exists. Its Step 5 runs the whole test project, which needs Task 2's test file to compile.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/PortForwardSupervisor.cs` (`OnSnapshotAsync`, the `Running` branch, ~lines 235-258; class remarks)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/PortForwardSupervisorTests.cs`

**Interfaces:**
- Consumes: `KubernetesSource.HealthCheckKey(string serviceName)`; the supervisor's existing `serviceName` constructor parameter; `HealthReportSnapshot(string Name, HealthStatus? Status, string? Description, string? ExceptionText)`.
- Produces: `Harness.Publish(string? state, int? exitCode = null, HealthStatus? health = null, HealthStatus? userCheck = null, bool tunnelReport = true)`: `health` is reported under `KubernetesSource.HealthCheckKey("orders")` (not `"tcp"`); `userCheck` adds a second report named `"user"`; `tunnelReport: false` omits the tunnel report.

- [ ] **Step 1: Migrate the harness** so `Publish` names the tunnel report `KubernetesSource.HealthCheckKey("orders")` and add `userCheck`/`tunnelReport` with defaults (null / true), so `BringUp()` and every existing `health:` caller migrate through `Publish` unchanged. Run `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~PortForwardSupervisorTests"`. Expected: all PASS (pure refactor).
- [ ] **Step 2: Write the failing tests**:
  - `ATunnelThatIsHealthy_WithAFailingUserCheck_CountsAsReady`: Starting, Running, then `Publish(Running, health: Healthy, userCheck: Unhealthy)`, then exit code 1 after `IdleAsync`; assert `Control.StartAsync` was called (restart) and `FailToStartAsync` was not.
  - `AFailingUserCheck_DoesNotRaiseTheNoReadyPodsWarning`: tunnel Healthy, user Unhealthy, `AdvanceAsync(UnhealthyWarnAfter + 1s)`, publish again; the logger has no warning.
  - `AnUnhealthyTunnel_StillRaisesTheWarning_EvenWhenAUserCheckIsHealthy`: tunnel Unhealthy, user Healthy; warning logged after 60s.
  - `AUserCheckAloneReporting_IsNeitherReadyNorUnhealthy`: `Publish(Running, health: null, userCheck: Unhealthy, tunnelReport: false)` repeatedly past 60s: no warning; then an exit is `FailToStartAsync` (never ready), no restart.
  - `ATunnelReportWithNoStatusYet_ChangesNothing`: snapshot whose tunnel `HealthReportSnapshot` has `Status == null`: same as the previous case.
  - `StableUptime_FollowsTheTunnelReportOnly`: tunnel Healthy for 30s while the user check stays Unhealthy, template: `ATunnelThatCameBackUp_GetsTheFullAllowanceAgainForTheNextOutage` (`Time.Advance(StableUptime)`); with the user check Unhealthy throughout, after the advance the next exit logs "attempt 1 of 8" with a 1s delay.
- [ ] **Step 3: Run** `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~PortForwardSupervisorTests"`. Expected: the user-check tests FAIL (aggregate is read), the rest PASS.
- [ ] **Step 4: Implement** in `OnSnapshotAsync`: find `tunnel = snapshot.HealthReports.FirstOrDefault(r => r.Name == KubernetesSource.HealthCheckKey(serviceName))`; ready branch when `tunnel is { Status: HealthStatus.Healthy }`; unhealthy branch when `tunnel is { Status: HealthStatus.Unhealthy or HealthStatus.Degraded }`; absent or `Status is null` changes nothing. Keep the existing comment on why a report must be present. Update the class remarks with one sentence: the supervisor reads only the tunnel's own report, so a user's check cannot read as a dead tunnel.
- [ ] **Step 5: Run** the same filter, then the whole project: `dotnet test -c Release`. Expected: PASS.
- [ ] **Step 6: Commit** `Supervise the tunnel by its own health report (#399)`.

### Task 4: Docs and changelog

**Files:**
- Modify: `docs/guides/configuration.md` (the "Native calls are skipped..." paragraph ~lines 216-232), `docs/sources/kubernetes.md` (section "Readiness and a dead tunnel"), `CHANGELOG.md` (`[Unreleased]` / `### Changed`, plus a `[#399]` link ref at the bottom, in numeric order)
- README: `README.md` makes no claim about skipped calls on kubernetes (the only mentions are line 13's source list). Step 1 re-runs the grep; if it still finds none, no README edit.

**Interfaces:** none.

- [ ] **Step 1: Verify the stale claims exist** with `grep -n -i "healthcheck\|health check\|one exception" docs/guides/configuration.md README.md docs/sources/kubernetes.md`. Expected: the two `configuration.md` hits, none in README.
- [ ] **Step 2: configuration.md**: delete the `WithHttpHealthCheck` sentence from the skipped list; replace "The one exception is wait ordering" with two exceptions on `"kubernetes"`: wait ordering and health checks (`WithHttpHealthCheck`, `WithHealthCheck`), each with why (the real resource is the tunnel; the check measures the service behind it). State that a `"url"`, `"disabled"` or unconfigured service still skips both, and that a subclass of `HealthCheckAnnotation` and every other annotation still skips.
- [ ] **Step 3: kubernetes.md**: add a "Health checks" paragraph covering: the check runs on the tunnel, `WaitFor` holds until the built-in TCP check **and** the author's check pass; a failing author check shows in the dashboard health badge and does not make the tunnel restart or `FailedToStart`; a checked-in `WithHttpHealthCheck` now sends real GETs to whichever context `servicesources.local.json` selects, with no credentials; **https limitation** (decision 1: certificate is the pod's, host name `localhost`, default probe will be `Unhealthy` unless trusted, `WaitFor` then holds, validation is not relaxed); **redirect risk** (decision 4: Aspire's default redirect handling applies, a hostile workload could redirect the probe, residual risk accepted); the loopback-port caveat (a foreign process on the port answers the probe).
- [ ] **Step 4: CHANGELOG** `### Changed` entry: "**A health check on a `"kubernetes"` service now applies instead of being skipped** ([#399]). `WithHttpHealthCheck` and `WithHealthCheck` reach the port-forward's resource, so `WaitFor(service)` also waits for the author's check. A failing author check no longer reads as a dead tunnel. `"url"`, `"disabled"` and unconfigured services still skip the call. Remove a `WithHttpHealthCheck` that was only there to be skipped." Match the register of neighbouring entries.
- [ ] **Step 5: Verify the text**: `grep -n "HealthCheck" docs/guides/configuration.md docs/sources/kubernetes.md CHANGELOG.md` shows the new text and no remaining claim that a kubernetes health check is skipped; `grep -n "^\[#399\]" CHANGELOG.md` finds the link ref.
- [ ] **Step 5b: Build and test**: `dotnet build -c Release --no-restore -warnaserror` and `dotnet test -c Release --no-build`. Expected: PASS. Run the Release-shape pack leg once (`dotnet pack ... -p:MinVerVersionOverride=9.9.9`) per the notes file's leg 5 before the PR (Phase 6, not per commit).
- [ ] **Step 6: Commit** `Document health checks on a kubernetes service (#399)`.
