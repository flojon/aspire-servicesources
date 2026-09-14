# Endpoint Skip Gate Shadow Overloads Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Status:** Draft

**Goal:** Close #334 — an `AppHost.WithHttpsEndpoint()`/`WithHttpEndpoint()` call on a
`ServiceResource` whose facade already carries an endpoint of the resolved default name (`"https"`/
`"http"`) currently hits Aspire's in-place-update branch, bypassing
`ServiceResourceBuilder.WithAnnotation` entirely, so the call is neither gated by
`Reachability.IsUnreachable` nor reported in the skip-warning tally.

**Architecture:** Shadow Aspire's `WithHttpEndpoint`/`WithHttpsEndpoint` with two non-generic public
extension overloads on `IResourceBuilder<ServiceResource>`, declared in
`Aspire.Hosting.ServiceSources` (as two more methods on `ServiceSourcesBuilderExtensions`). C#
overload resolution prefers a non-generic exact-receiver-type match over Aspire's generic
`WithHttpEndpoint<T>`/`WithHttpsEndpoint<T>`, so every AppHost call already reaching `AddService`
binds to the shadow automatically, with no new `using` to add. Each shadow gates via
`Reachability.IsUnreachable`/`Reachability.CapabilityLabel` — the same table
`ServiceResourceBuilder.WithAnnotation` already consults — *before* Aspire's own method ever runs,
then delegates to Aspire's real implementation by a fully-qualified static call
(`Aspire.Hosting.ResourceBuilderExtensions.WithHttpEndpoint(...)`) when reachable, never through
extension-method syntax on `builder` (which would recurse into the shadow itself).

**Tech Stack:** C# / .NET (net8.0;net9.0;net10.0), xUnit, Aspire.Hosting 13.5.2 (pinned).

**Spec:** [`docs/superpowers/specs/2026-09-14-334-endpoint-skip-gate-design.md`](../specs/2026-09-14-334-endpoint-skip-gate-design.md)

## Global Constraints

- Shadow methods must be `public static`, non-generic, declared exactly in the
  `Aspire.Hosting.ServiceSources` namespace (spec §3.5 item 1) — an `internal` shadow would compile
  but never bind at an external AppHost's call site.
- Mirror Aspire's exact current public signatures for `WithHttpEndpoint`/`WithHttpsEndpoint`
  (`int? port = null, int? targetPort = null, string? name = null, string? env = null, bool?
  isProxied = null`, `[EndpointName]` on `name`) — no AppHost-visible signature change (spec §3.5
  item 1; verified against the pinned `Aspire.Hosting 13.5.2` decompile, `ResourceBuilderExtensions`
  lines 1581 and 1628).
- Delegate to Aspire's implementation only via the fully-qualified static call
  `Aspire.Hosting.ResourceBuilderExtensions.WithHttpEndpoint`/`WithHttpsEndpoint` — never
  `builder.WithHttpEndpoint(...)` extension syntax, which recurses into the shadow itself (spec
  §3.5).
- Reuse `Reachability.IsUnreachable`/`Reachability.CapabilityLabel` unchanged (internal to
  `Aspire.Hosting.ServiceSources.Sources`, reachable via this repo's existing `InternalsVisibleTo`)
  — do not duplicate or re-derive the gating table (spec §3.5 item 2).
- Both shadows need `[AspireExport]`, not `[AspireExportIgnore]` — the latter would silently drop
  `WithHttpsEndpoint`/`WithHttpEndpoint` from the generated TypeScript/Java/JavaScript handle for
  every source (spec §3.6).
- **No CHANGELOG.md entry.** Per `CHANGELOG.md`'s own stated rule ("a bug introduced and fixed
  within the same `[Unreleased]` cycle never shipped... nothing for a consumer to have hit") and
  spec §2: the gap was introduced by #326 (`243c227`, merged 2026-09-14), which is still entirely
  inside `[Unreleased]` and has not been released. This plan adds **no** `### Fixed` entry.
- **No 4th repro case.** `EndpointSkipGapRepro.cs` stays scoped to the three cases the issue
  describes. The with-arguments mutation finding (spec §4) is tracked separately as **#335** — its
  own repro coverage belongs in that issue's branch, not here.
- **The raw `WithEndpoint(builder, endpointName, Action<EndpointAnnotation> callback, ...)` callback
  overloads are out of scope.** They share the same root cause (spec §3.6, last bullet) but are not
  named in #334's acceptance checklist. No task below touches them, and none should be added without
  a human decision to widen scope.
- **The binary-compatibility `bool isProxied` (non-nullable) overload is out of scope for this
  plan**, by explicit decision (see Task 3 note): the spec flags it as a real, narrow, unclosed edge
  (§3.6) but explicitly leaves closing it discretionary — "cheap to add if the Plan phase chooses to,
  not required by the ticket's acceptance criteria." This plan does not shadow it.

---

## File Structure

- **Modify** `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs` — add the two
  shadow methods (`WithHttpEndpoint`, `WithHttpsEndpoint`) plus a small private gating helper they
  share, alongside the existing `AddService`/`UseDeferredCheckout`/`AddLocalKind`/
  `AddServiceCatalog` methods already in this file and namespace.
- **Modify** `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs` — extend the existing
  `Bridge` XML comment (lines 20-25) with the addendum spec §3.5 item 3 calls for.
- **Create** `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs` — the issue's own
  repro, committed essentially verbatim (see Task 1 for the one necessary addition: the third case's
  full code, which the issue only described in prose).
- **Create** `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs` — new
  unit tests exercising both shadow overloads directly (gate-and-skip, delegate-when-reachable,
  `WithHttpEndpoint` parity), kept separate from the repro file so the repro stays an unmodified copy
  of the issue.

## Interfaces produced (for later tasks / reviewers)

- `Aspire.Hosting.ServiceSources.ServiceSourcesBuilderExtensions.WithHttpEndpoint(this
  IResourceBuilder<ServiceResource> builder, int? port = null, int? targetPort = null, string? name
  = null, string? env = null, bool? isProxied = null) : IResourceBuilder<ServiceResource>`
- `Aspire.Hosting.ServiceSources.ServiceSourcesBuilderExtensions.WithHttpsEndpoint(this
  IResourceBuilder<ServiceResource> builder, int? port = null, int? targetPort = null, string? name
  = null, string? env = null, bool? isProxied = null) : IResourceBuilder<ServiceResource>`
- Private helper `GateEndpointCall(IResourceBuilder<ServiceResource> builder) : bool` — `true` means
  the call was skipped and the warning already recorded; reused by both overloads (exact shape
  defined in Task 3).

---

### Task 1: Commit the issue's repro test, `EndpointSkipGapRepro.cs`

**Files:**
- Create: `test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs`

**Interfaces:**
- Consumes: `UrlSource` (`Sources/UrlSource.cs`), `KubernetesSource` (`Sources/KubernetesSource.cs`),
  `ServiceSourcesWarnings.For(IDistributedApplicationBuilder).Messages`, `TestHelpers.CreateBuilder`,
  `TestHelpers.EmptyRepositories`, `TempDirectories.CreateSubdirectory()`.
- Produces: nothing later tasks call directly — this file's assertions are the acceptance evidence.

This is the repro from the issue body, committed as-is (per spec §3.7: the fix makes all three
assertions true, so no inversion). The issue's own text gives full code for the first two cases and
only *describes* the third ("The kubernetes test asserts the real port-forward resource's endpoint
tuples are unchanged across `service.WithHttpsEndpoint()`; it passes.") — its literal source was
never pasted anywhere reachable. **Deviation from "exactly as given," noted rather than hidden:** the
third test below is written from that description, following this repo's own `KubernetesSourceTests`
construction pattern (`Resolve` called directly against a `KubernetesSource`), not copied verbatim
from an issue code block that does not exist. It is written to stay green in *both* the pre-fix and
post-fix state — asserting only that the endpoint's mutable fields and the annotation *instance* are
unchanged, not asserting anything about the warning count (which is 0 before the fix and 1 after, per
Task 3 — asserting either count here would make this file stop being a copy of the issue's own claim
that this case "passes" both before and after).

- [ ] **Step 1: Write the repro file**

```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The repro from issue #334, committed as the issue described it. All three cases are expected to
/// pass once the skip-and-warn gate closes the update-branch gap (design doc
/// docs/superpowers/specs/2026-09-14-334-endpoint-skip-gate-design.md) — before that fix,
/// <see cref="DefaultNamedEndpoint_OnUrlSource_IsSkippedAndReported"/> is the one that fails.
/// </summary>
public class EndpointSkipGapRepro
{
    private static readonly ServiceDefinition UrlDefinition = new ServiceMetadata
    {
        Url = new UrlMetadata { Url = "https://orders.example.com" },
    }.ToDefinition("servicesources.yaml", "inventory", TestHelpers.EmptyRepositories);

    private static IResourceBuilder<ServiceResource> Url(IDistributedApplicationBuilder builder) =>
        new UrlSource().Resolve(
            builder, "inventory", UrlDefinition, new ServiceDeveloperConfig { Source = "url" });

    // FAILS before the fix — the default-named call an AppHost actually writes.
    [Fact]
    public void DefaultNamedEndpoint_OnUrlSource_IsSkippedAndReported()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = Url(builder);

        var before = service.Resource.Annotations.OfType<EndpointAnnotation>()
            .Select(e => $"{e.Name}:{e.UriScheme}").ToArray();

        service.WithHttpsEndpoint();

        var after = service.Resource.Annotations.OfType<EndpointAnnotation>().Count();
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.True(
            warnings.Count == 1 && warnings[0].Contains("WithHttpEndpoint/WithHttpsEndpoint"),
            $"pre-existing=[{string.Join(",", before)}]; endpoints after={after}; "
            + $"warnings={warnings.Count}; text={(warnings.Count > 0 ? warnings[0] : "<none>")}");
    }

    // PASSES before and after — a genuinely new endpoint name goes through WithAnnotation and is
    // gated correctly today already.
    [Fact]
    public void NewEndpoint_OnUrlSource_IsSkippedAndReported()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = Url(builder);

        service.WithHttpsEndpoint(port: 7777, name: "probe");

        var added = service.Resource.Annotations.OfType<EndpointAnnotation>().Count(e => e.Name == "probe");
        var warnings = ServiceSourcesWarnings.For(builder).Messages;

        Assert.True(
            added == 0 && warnings.Count == 1 && warnings[0].Contains("WithHttpEndpoint/WithHttpsEndpoint"),
            $"'probe' added to facade={added}; warnings={warnings.Count}");
    }

    // PASSES before and after — the update branch mutates nothing for a no-argument call, so the
    // kubernetes-sourced port-forward's real endpoint tuple was never at risk here (the *with*-
    // arguments mutation risk is #335, out of this file's scope). Written from the issue's own prose
    // description; no literal code for this case appeared in the issue body — see this task's notes.
    private sealed class FakePortAllocator(int port) : IPortAllocator
    {
        public bool IsAvailable(int candidate) => true;

        public int AllocatePort() => port;

        public IReadOnlyList<int> AllocatePorts(int count) => throw new NotSupportedException();
    }

    private static ServiceDefinition KubernetesDefinition() =>
        new ServiceMetadata
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Kubernetes = new KubernetesMetadata { Service = "orders-svc", Port = 8080, Scheme = "https" },
        }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static ServiceDeveloperConfig KubernetesDevConfig() =>
        new() { Source = "kubernetes", Kubernetes = new() { Context = "dev-west" } };

    [Fact]
    public void DefaultNamedEndpoint_OnKubernetesSource_DoesNotChangeThePortForwardsEndpoint()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var service = new KubernetesSource(new FakePortAllocator(54321))
            .Resolve(builder, "orders", KubernetesDefinition(), KubernetesDevConfig());

        var before = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var beforePort = before.Port;
        var beforeTargetPort = before.TargetPort;
        var beforeScheme = before.UriScheme;

        service.WithHttpsEndpoint();

        var after = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Same(before, after);
        Assert.Equal(beforePort, after.Port);
        Assert.Equal(beforeTargetPort, after.TargetPort);
        Assert.Equal(beforeScheme, after.UriScheme);
    }
}
```

- [ ] **Step 2: Run the repro to confirm the baseline the issue reports**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter FullyQualifiedName~EndpointSkipGapRepro -f net8.0`
Expected: `DefaultNamedEndpoint_OnUrlSource_IsSkippedAndReported` **FAILS**;
`NewEndpoint_OnUrlSource_IsSkippedAndReported` and
`DefaultNamedEndpoint_OnKubernetesSource_DoesNotChangeThePortForwardsEndpoint` **PASS** — matching
the issue's own reported baseline (1 fail, 2 pass) exactly.

- [ ] **Step 3: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/EndpointSkipGapRepro.cs
git commit -m "$(cat <<'EOF'
Add EndpointSkipGapRepro.cs reproducing #334

Default-named WithHttpsEndpoint() on an out-of-band ServiceResource hits
Aspire's in-place-update branch and bypasses the skip-and-warn gate. One
of three cases fails, matching the issue's own reported baseline.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Write failing unit tests for both shadow overloads

**Files:**
- Create: `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs`

**Interfaces:**
- Consumes: the shadow overloads Task 3 will add
  (`Aspire.Hosting.ServiceSources.ServiceSourcesBuilderExtensions.WithHttpEndpoint`/
  `WithHttpsEndpoint`, signature above) — this task's tests call them before they exist in shadowed
  form, so they currently bind to Aspire's own generic `WithHttpEndpoint<T>`/`WithHttpsEndpoint<T>`
  and fail at the assertion, not at compile time.
- Produces: nothing later tasks call directly.

`EndpointSkipGapRepro.cs` only exercises `WithHttpsEndpoint`. The acceptance criteria name both
`WithHttpEndpoint` and `WithHttpsEndpoint`, so this task adds direct unit-level coverage for the
shadow itself — its gate-and-skip path, its delegate-when-reachable path, and parity for
`WithHttpEndpoint` — kept in a new file rather than folded into the repro, so the repro stays an
unmodified copy of the issue (Task 1) while this file is free to test the shadow's own contract.

- [ ] **Step 1: Write the failing tests**

Uses the real internal APIs directly — `Sources.ResolvedService.BridgeUnregistered`/`Bridge` (the
same ones `UrlSource`/`KubernetesSource`/`ContainerSource` call, reachable here via this repo's
existing `InternalsVisibleTo`) rather than hand-building a `ServiceResourceBuilder` and its
`ServiceSourceAnnotation`, so each fixture is wired exactly the way production code wires it — the
same pattern `ServiceResourceBuilderTests.cs` already uses for its container case.

```csharp
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests;

public class ServiceSourcesBuilderExtensionsTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    // Mirrors UrlSource.Resolve's own EndpointAnnotation construction exactly, then bridges it
    // unregistered — the same shape UrlSource itself produces for a "url"-sourced service, so the
    // shadow's gate sees precisely what it sees in production.
    private static IResourceBuilder<ServiceResource> UrlFacadeWithEndpoint(
        IDistributedApplicationBuilder builder, string serviceName, string scheme)
    {
        var facade = new ServiceResource(serviceName);
        facade.Annotations.Add(new EndpointAnnotation(
            ProtocolType.Tcp, uriScheme: scheme, name: scheme, transport: "http", port: 443, targetPort: 443));
        return ResolvedService.BridgeUnregistered(builder, facade, serviceName, "url");
    }

    [Fact]
    public void WithHttpsEndpoint_DefaultNameOnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

        var result = service.WithHttpsEndpoint(port: 9999);

        Assert.Same(service, result);
        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithHttpEndpoint/WithHttpsEndpoint", message);
    }

    [Fact]
    public void WithHttpEndpoint_DefaultNameOnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = UrlFacadeWithEndpoint(builder, "inventory", "http");

        service.WithHttpEndpoint(port: 8888);

        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithHttpEndpoint/WithHttpsEndpoint", message);
    }

    [Fact]
    public void WithHttpsEndpoint_OnReachableSource_AppliesThroughToTheRealEndpoint()
    {
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");
        var service = ResolvedService.Bridge(real, "orders", "container");

        var result = service.WithHttpsEndpoint(port: 9999, name: "probe");

        var endpoint = Assert.Single(result.Resource.Annotations.OfType<EndpointAnnotation>()
            .Where(e => e.Name == "probe"));
        Assert.Equal(9999, endpoint.Port);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail for the expected reason**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter FullyQualifiedName~ServiceSourcesBuilderExtensionsTests -f net8.0`
Expected: the project **builds clean** (every type and method referenced above already exists on
`main`), and exactly two of the three tests **FAIL**:
`WithHttpsEndpoint_DefaultNameOnUrlSource_IsSkippedAndReported` and
`WithHttpEndpoint_DefaultNameOnUrlSource_IsSkippedAndReported` — both currently bind to Aspire's own
generic `WithHttpEndpoint<T>`/`WithHttpsEndpoint<T>`, which mutates `Port` to the passed value (9999 /
8888, not the pre-existing 443) and records no warning, so both assertions fail.
`WithHttpsEndpoint_OnReachableSource_AppliesThroughToTheRealEndpoint` **PASSES** already — a
genuinely new name on a reachable `container` source already goes through the add branch and
`WithAnnotation` correctly today. Do not proceed to Task 3 until this file shows exactly that
two-fail/one-pass state — it is this task's actual red state.

- [ ] **Step 3: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs
git commit -m "$(cat <<'EOF'
Add failing unit tests for the WithHttpEndpoint/WithHttpsEndpoint shadow

Covers the shadow's own contract directly: gate-and-skip on an
out-of-band source's default-named call for both WithHttpEndpoint and
WithHttpsEndpoint, and pass-through on a reachable source.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 3: Implement the shadow overloads with `[AspireExport]`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`

**Interfaces:**
- Consumes: `Reachability.IsUnreachable(Type, string)`, `Reachability.CapabilityLabel(Type)`
  (`Sources/ServiceResourceBuilder.cs`), `ServiceSourceAnnotation` (top-level namespace),
  `ServiceSourcesWarnings.For(IDistributedApplicationBuilder).AddSkip(string, string, string)`,
  `Aspire.Hosting.ResourceBuilderExtensions.WithHttpEndpoint`/`WithHttpsEndpoint` (external, Aspire's
  own).
- Produces: `ServiceSourcesBuilderExtensions.WithHttpEndpoint`/`WithHttpsEndpoint` (signature in the
  File Structure section above), plus the private `GateEndpointCall` helper both call.

Add this using the top of the file (alongside the existing `using Aspire.Hosting.ServiceSources.Sources;`
already there, which brings `Reachability` into scope):

```csharp
using Aspire.Hosting.ApplicationModel;
```

(Already present — no new `using` is actually required: `EndpointAnnotation` and `[EndpointName]`
come from `Aspire.Hosting.ApplicationModel`, already imported at the top of this file; `Reachability`
comes from `Aspire.Hosting.ServiceSources.Sources`, already imported; `ServiceSourceAnnotation` and
`ServiceSourcesWarnings` are declared directly in this file's own namespace,
`Aspire.Hosting.ServiceSources`, so no import is needed for them either.)

- [ ] **Step 1: Add the shared gating helper and both shadow methods**

Add to `ServiceSourcesBuilderExtensions.cs`, as two more public methods on the `ServiceSourcesBuilderExtensions`
class (after `AddServiceCatalog`, before the `private static void RequireCurrentValidateSignature`
section):

```csharp
    /// <summary>
    /// Shadows Aspire's own <c>WithHttpEndpoint&lt;T&gt;</c>. Aspire's generic method, called with a
    /// name that already resolves to an existing <see cref="EndpointAnnotation"/> on the facade —
    /// exactly what happens for the default name (<c>"http"</c>) on a <c>url</c>- or
    /// <c>kubernetes</c>-sourced service, which pre-registers an endpoint under that name — takes an
    /// in-place update branch that never calls
    /// <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/>, so it is neither gated by
    /// <see cref="Reachability"/> nor reported in the skip-warning tally (#334). This non-generic
    /// overload on the concrete <see cref="ServiceResource"/> receiver type is what C# overload
    /// resolution prefers over Aspire's generic one, so every AppHost call already reaching
    /// <see cref="AddService"/> binds here automatically. Mirrors Aspire's current public signature
    /// exactly — no AppHost-visible signature change.
    /// </summary>
    [AspireExport]
    public static IResourceBuilder<ServiceResource> WithHttpEndpoint(
        this IResourceBuilder<ServiceResource> builder,
        int? port = null, int? targetPort = null, [EndpointName] string? name = null, string? env = null,
        bool? isProxied = null)
    {
        if (GateEndpointCall(builder))
        {
            return builder;
        }

        // Fully-qualified static call, never `builder.WithHttpEndpoint(...)` — extension-method
        // syntax on `builder` would resolve back to this very shadow and recurse.
        return Aspire.Hosting.ResourceBuilderExtensions.WithHttpEndpoint(builder, port, targetPort, name, env, isProxied);
    }

    /// <summary>
    /// The <c>https</c> counterpart of <see cref="WithHttpEndpoint"/> — same shadowing, same gate,
    /// same reason (#334). This is the call an AppHost actually writes for the common case: the
    /// default name it resolves to, <c>"https"</c>, is exactly the name <c>UrlSource</c> and
    /// <c>KubernetesSource</c> pre-register.
    /// </summary>
    [AspireExport]
    public static IResourceBuilder<ServiceResource> WithHttpsEndpoint(
        this IResourceBuilder<ServiceResource> builder,
        int? port = null, int? targetPort = null, [EndpointName] string? name = null, string? env = null,
        bool? isProxied = null)
    {
        if (GateEndpointCall(builder))
        {
            return builder;
        }

        return Aspire.Hosting.ResourceBuilderExtensions.WithHttpsEndpoint(builder, port, targetPort, name, env, isProxied);
    }

    /// <summary>
    /// Whether an endpoint call against <paramref name="builder"/> should be skipped — and, if so,
    /// records the skip warning as a side effect, exactly as
    /// <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/> already does for every other
    /// native vocabulary method. Reads the facade's own <see cref="ServiceSourceAnnotation"/> rather
    /// than requiring a <see cref="ServiceResourceBuilder"/> receiver, so it works uniformly whether
    /// <paramref name="builder"/> is the internal wrapper type or (in principle) any other
    /// <see cref="IResourceBuilder{ServiceResource}"/>.
    /// </summary>
    private static bool GateEndpointCall(IResourceBuilder<ServiceResource> builder)
    {
        var source = builder.Resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault()?.Source;
        if (source is null || !Reachability.IsUnreachable(typeof(EndpointAnnotation), source))
        {
            return false;
        }

        ServiceSourcesWarnings.For(builder.ApplicationBuilder)
            .AddSkip(builder.Resource.Name, source, Reachability.CapabilityLabel(typeof(EndpointAnnotation)));
        return true;
    }
```

- [ ] **Step 2: Run Task 1's repro and Task 2's unit tests to verify they now pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~EndpointSkipGapRepro|FullyQualifiedName~ServiceSourcesBuilderExtensionsTests" -f net8.0`
Expected: all six tests (three from Task 1, three from Task 2) **PASS**.

- [ ] **Step 3: Build with `-warnaserror` and confirm it succeeds cleanly**

Run:

```bash
dotnet build -c Release -warnaserror 2>&1 | tee /tmp/334-build-task3.log
grep -c ASPIREEXPORT008 /tmp/334-build-task3.log   # must print 0
```

Expected: build **SUCCEEDS**, zero `ASPIREEXPORT008` matches. Both shadow methods already carry
`[AspireExport]` from Step 1 above (spec §3.6: the un-annotated version is what emits
`ASPIREEXPORT008` in the spec's own probe — this task never writes that un-annotated version in the
first place). Task 4 re-runs this exact check as its own standalone, explicit verification gate
before the PR — this step is the first confirmation, not the only one.

- [ ] **Step 4: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs
git commit -m "$(cat <<'EOF'
Shadow WithHttpEndpoint/WithHttpsEndpoint to close the update-branch skip gap (#334)

Aspire's generic WithHttpEndpoint<T>/WithHttpsEndpoint<T> only calls
ServiceResourceBuilder.WithAnnotation when adding a new endpoint. A call
naming an endpoint that already exists — the default name on a url- or
kubernetes-sourced service — takes an in-place update branch that never
reaches WithAnnotation, so it was neither gated nor reported.

Non-generic overloads on IResourceBuilder<ServiceResource>, which C#
prefers over Aspire's generic ones, gate via the same Reachability table
before ever calling into Aspire's own implementation.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 4: Dedicated verification gate — `[AspireExport]` / `ASPIREEXPORT008` is load-bearing here

**Files:** none — verification only, no code change.

**Interfaces:** none.

Spec §3.6 calls this out as **not optional** for this ticket specifically, unlike most ATS-unrelated
bug fixes: this change's mechanism (a second extension method shadowing an exported type) is exactly
the shape `ASPIREEXPORT008` exists to catch, and there are now two candidate
`WithHttpsEndpoint`/`WithHttpEndpoint` bindings for ATS's own TypeScript codegen to reconcile. Task 3
already applies `[AspireExport]` to both shadow methods and confirms a clean build as part of its own
Step 3; this task is the standalone, explicit verification gate the spec insists on — run
independently, on the tip of the branch as it stands after Task 3's commit, so a reviewer (or a later
round) can rerun exactly this and trust the result without re-deriving it from Task 3's narrative.

- [ ] **Step 1: Confirm both shadow methods carry `[AspireExport]` in the committed source**

```bash
grep -B1 "public static IResourceBuilder<ServiceResource> WithHttpEndpoint(" \
    src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs
grep -B1 "public static IResourceBuilder<ServiceResource> WithHttpsEndpoint(" \
    src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs
```

Expected: both `grep` calls print a line reading `[AspireExport]` immediately before the method
signature.

- [ ] **Step 2: Rebuild with `-warnaserror` and confirm zero `ASPIREEXPORT008` diagnostics**

Run: `dotnet build -c Release -warnaserror`
Expected: **build SUCCEEDS**, with no `ASPIREEXPORT008` (or any other) diagnostic naming
`WithHttpEndpoint` or `WithHttpsEndpoint`. This is the explicit, non-optional confirmation spec §3.6
calls for — do not mark this task complete on a build that merely "looks clean"; grep the build
output for `ASPIREEXPORT008` and confirm zero matches:

```bash
dotnet build -c Release -warnaserror 2>&1 | tee /tmp/334-build.log
grep -c ASPIREEXPORT008 /tmp/334-build.log   # must print 0
```

- [ ] **Step 3: Rerun the full test suite across all three target frameworks**

Run: `dotnet test -c Release --logger "trx;LogFilePrefix=results" --results-directory
./artifacts/test-results` (this is the intrinsic multi-target invocation CI itself uses —
`ticket-notes.md`'s leg 1 — it runs net8.0/net9.0/net10.0 together, not one framework at a time)
Expected: **all tests PASS**, including every test from Task 1 and Task 2.

- [ ] **Step 4: Nothing to commit**

This task makes no code change — Steps 1-3 confirm what Task 3 already committed. Proceed directly
to Task 5.

---

### Task 5: Extend `ResolvedService.Bridge`'s comment with the second-interception-point addendum

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs:20-25`

**Interfaces:**
- Consumes: nothing (documentation only).
- Produces: nothing (documentation only).

Acceptance checklist item 5 and spec §3.5 item 3 both call for revisiting this comment. Per the
spec's own framing, the addendum documents *why a second interception point now exists* alongside
`WithAnnotation`, not a permanent gap (which is how an earlier draft of this document would have
framed it).

- [ ] **Step 1: Extend the comment**

In `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs`, change:

```csharp
        // Copies the SAME instances `real` already carries — a container/kubernetes source's own
        // EndpointAnnotation, a "local" project's launch-profile-derived endpoints and environment,
        // whatever a deferred registration added — so GetServiceEndpoint/GetEndpoint and a second
        // WithHttpEndpoint() call see them on the facade exactly as they sat on `real` before the
        // facade existed. Sharing the instance rather than its value is what keeps a later
        // mutation-in-place (WithEndpoint's update branch) visible on both collections.
```

to:

```csharp
        // Copies the SAME instances `real` already carries — a container/kubernetes source's own
        // EndpointAnnotation, a "local" project's launch-profile-derived endpoints and environment,
        // whatever a deferred registration added — so GetServiceEndpoint/GetEndpoint and a second
        // WithHttpEndpoint() call see them on the facade exactly as they sat on `real` before the
        // facade existed. Sharing the instance rather than its value is what keeps a later
        // mutation-in-place (WithEndpoint's update branch) visible on both collections.
        //
        // That same update branch is also why WithHttpEndpoint/WithHttpsEndpoint are shadowed
        // (ServiceSourcesBuilderExtensions) rather than gated solely through WithAnnotation: naming
        // an endpoint that already exists here — the default name on a "url"/"kubernetes" service,
        // which is exactly the name this method pre-registers — takes Aspire's in-place-update
        // branch, which never calls WithAnnotation at all (#334). The shadow is a second
        // interception point reading the identical Reachability table, not a workaround duplicating
        // its logic.
```

- [ ] **Step 2: Rebuild to confirm the comment-only change is clean**

Run: `dotnet build -c Release -warnaserror`
Expected: build succeeds (a comment change cannot introduce a diagnostic, but this confirms nothing
adjacent broke).

- [ ] **Step 3: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs
git commit -m "$(cat <<'EOF'
Document the endpoint shadow as a second WithAnnotation-bypass interception point

Extends the existing Bridge comment about instance-sharing to explain
why WithHttpEndpoint/WithHttpsEndpoint also needed a shadow: the same
update branch the comment already described bypasses WithAnnotation
entirely for a default-named call (#334).

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Full pre-PR verification sweep

**Files:** none (verification only).

**Interfaces:** none.

Runs every verify leg from `ticket-notes.md`, including the expensive ones that are deliberately not
run per-task above. No code changes in this task.

- [ ] **Step 1: Cheap legs, already green from Task 4 — rerun once more for a clean final state**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --logger "trx;LogFilePrefix=results" --results-directory ./artifacts/test-results
```

Expected: both succeed, zero `ASPIREEXPORT008` (or any other) diagnostics, all tests pass across
net8.0/net9.0/net10.0.

- [ ] **Step 2: `smoketest-container` (expensive, run once before PR)**

Run: `./scripts/smoketest-container-source.sh`
Expected: passes. Not expected to touch this change's code path (`local`/`url`/`kubernetes` endpoint
gating), but `container` also goes through `ServiceResourceBuilder`, so it is not skipped.

- [ ] **Step 3: `smoketest-config-layers` (run once before PR)**

Run: `./scripts/smoketest-config-layers.sh`
Expected: passes.

- [ ] **Step 4: `smoketest-local-source` (expensive, run once before PR)**

Run: `./scripts/smoketest-local-source.sh`
Expected: passes.

- [ ] **Step 5: `smoketest-kubernetes-source.sh` (not CI-wired, but relevant to this change — run as
  an extra probe)**

Run: `./scripts/smoketest-kubernetes-source.sh`
Expected: passes — acceptance criterion 2 explicitly calls out kubernetes port-forward endpoint
tuples, and this script is the closest thing to an end-to-end kubernetes-source check this repo has,
even though it is not a CI-enforced leg.

- [ ] **Step 6: `typecheck-typescript` — relevant here, run before marking the PR ready**

This leg is **not a routine "if feasible" leg for this ticket** — spec §3.6 names it explicitly as
load-bearing: this change adds new `[AspireExport]`-annotated public API surface (the two shadow
methods), and there are now two candidate `WithHttpsEndpoint`/`WithHttpEndpoint` bindings in scope
for the TypeScript codegen catalog to reconcile (Aspire's own and this package's shadow) — exactly
the shape `ASPIREEXPORT008` and this leg exist to catch together. If the pinned Aspire CLI
(`aspire.cli`, `13.5.3`) and Node 22 are available locally, run it; otherwise this leg is named
explicitly here as **not run locally, relying on CI** — do not silently skip mentioning it.

```bash
# If feasible locally (requires the pinned aspire.cli dotnet tool and npx tsc):
# follow ci.yml's typecheck-typescript job steps against samples/DemoAppHostTypeScript (or
# equivalent) and confirm no new TypeScript compile error.
```

- [ ] **Step 7: `verify-invariants` — not applicable to this change**

This change touches no repo-invariant file (`Directory.Packages.props`, `global.json`, the Dependabot
ignore list, the Aspire-version-pairing files) — only `.cs` source and test files. State explicitly:
**this leg is not applicable to this change** and is not run locally; it will still run in CI as a
matter of course and is expected to pass trivially since nothing it checks was touched.

- [ ] **Step 8: Confirm the acceptance checklist is fully satisfied**

Cross-check against `ticket-notes.md`'s acceptance checklist:

1. Default-named endpoint call reported in the skip-warning tally — **closed** by Task 3
   (`GateEndpointCall`), proven by Task 1's `DefaultNamedEndpoint_OnUrlSource_IsSkippedAndReported`
   and Task 2's equivalent unit tests for both `WithHttpEndpoint`/`WithHttpsEndpoint`.
2. No functional regression (resolved URL/env correct; kubernetes port-forward tuples unmutated) —
   verified by Task 1's kubernetes case and Task 2's reachable-source case.
3. `EndpointSkipGapRepro.cs` committed — done in Task 1.
4. Fix shape chosen — the shadow-overload design (spec §3.5), implemented in Task 3-4.
5. `ResolvedService.Bridge`'s comment revisited — done in Task 5.

No CHANGELOG.md entry (Global Constraints). No 4th repro case (#335 is separate). The binary-compat
`bool isProxied` overload and the raw `WithEndpoint` callback overloads remain unshadowed, by the
explicit scope decisions in Global Constraints — confirm neither was accidentally touched by any
task's diff.
