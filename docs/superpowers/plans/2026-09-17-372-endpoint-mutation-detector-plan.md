# Post-hoc endpoint-mutation detector for out-of-band sources (#372) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the detector the spec designs — snapshot every `EndpointAnnotation` a `url` or
`kubernetes` service registers at resolve time, and at `BeforeStartEvent` restore anything that
changed, drop anything that was added, re-add anything that was removed, and report each one through
the package's existing skip warning.

**Architecture:** One new file, `EndpointMutationDetector`, installed as the last statement of
`ResolvedService.Bridge` and `ResolvedService.BridgeUnregistered` and returning immediately for any
source that is not out-of-band. It records a `Dictionary<EndpointAnnotation, Fingerprint>` keyed by
**reference identity**, subscribes `BeforeStartEvent`, and reconciles the facade's endpoint
collection against that snapshot. Reporting goes through `ServiceSourcesWarnings.ReporterFor` +
`AddSkip` + an immediate `Flush`, so the message reaches the log whichever order the subscribers
landed in. Nothing else changes: not `Reachability`, not `GateEndpointCall`, not
`ServiceResourceBuilder.WithAnnotation`, not `ServiceSourcesWarnings`, not any public signature.

**Tech Stack:** C# / .NET (net8.0, net9.0, net10.0 multi-target), xUnit, `Aspire.Hosting` 13.5.2
(pinned floor, `Directory.Build.props`).

**Spec:** [docs/superpowers/specs/2026-09-17-372-endpoint-mutation-detector-design.md](../specs/2026-09-17-372-endpoint-mutation-detector-design.md)

## Global Constraints

- **Pinned Aspire floor: `Aspire.Hosting` 13.5.2.** Every claim this plan makes about Aspire comes
  from the spec's decompilation of
  `~/.nuget/packages/aspire.hosting/13.5.2/lib/net8.0/Aspire.Hosting.dll`, re-checked while writing
  this plan. Do not re-derive it from memory. If something contradicts the spec, stop and report
  rather than adjusting the code to fit.
- **Revert and warn. Never throw** (spec §2). Every unreachable capability in this package skips
  with a warning; a detector that threw would turn one developer's `servicesources.local.json`
  choice into a build break for a call another developer wrote.
- **Installed only for out-of-band sources** — `Reachability.OutOfBandSources.Contains(source)`,
  checked inside `Install` so both call sites stay unconditional (spec §5.1, §5.5). That filter is
  the whole no-false-positives argument: for `url`/`kubernetes`,
  `Reachability.IsUnreachable(typeof(EndpointAnnotation), source)` is unconditionally true, so any
  post-resolve endpoint change is by definition one that should have been skipped. On `local` and
  `container` an endpoint change after resolve is legitimate, and nothing is installed.
- **`Install` is the LAST statement of both `Bridge` and `BridgeUnregistered`** (spec §5.5).
  `ServiceStartupFailureNotices.For` subscribes its own `BeforeStartEvent` handler, so installing
  before it in one method and after it in the other would give the two sources opposite dispatch
  orders. Symmetry here is a requirement, not tidiness.
- **Keyed by reference identity, never by name** (spec §5.2). `EndpointAnnotation.Name` is a public
  settable property, so identity and name are independent; keyed by name a rename reads as one
  endpoint disappearing and another appearing, and the restore is wrong in both directions.
- **Restore is two-phase, and writes back only what still differs** (spec §5.2). `Transport`,
  `TlsEnabled`, `Port` and `TargetPort` are derived from other fields — confirmed against the pinned
  assembly: `Transport` falls back to `"http"`/`Protocol.ToString().ToLowerInvariant()` off
  `UriScheme` when `_transport` is null; `TlsEnabled` falls back to `UriScheme == "https"`; `Port`
  and `TargetPort` fall back to each other when `!IsProxied`, which is the case for **both**
  out-of-band sources. Read the whole current fingerprint into a local first, restore the seven
  independent fields, then **re-read** each of those four getters and write only if it *still*
  differs. Never interleave reads with writes.
- **Restore `IsExplicitlyProxied`, never `IsProxied`.** Both setters keep the two backing fields in
  lockstep, so one restore covers both, and `_isProxied`'s `true` default makes
  `IsExplicitlyProxied = null` reconstruct the untouched state exactly.
- **The fingerprint is eleven fields**, the ten the prototype measured plus `Protocol`: `Name`,
  `Port`, `TargetPort`, `UriScheme`, `TargetHost`, `Transport`, `IsExternal`,
  `IsExplicitlyProxied`, `ExcludeReferenceEndpoint`, `TlsEnabled`, `Protocol`. All eleven are public
  get/set on `EndpointAnnotation`, so every one can be written back. None of the measured ten is
  dropped.
- **`Flush(@event.Services)` inside the detector's own handler, immediately after the `AddSkip`
  calls, and only when at least one skip was recorded** (spec §5.3). This is a requirement, not a
  belt-and-braces measure: `UrlSource.RegisterContainerConsumerCheck` runs *before* the
  `BridgeUnregistered` call that installs the detector, so for every `url` service the warnings
  flush handler is always subscribed ahead of it, and the failing order is the normal order. **Do
  not substitute a subscription-ordering trick for it.**
- **`ServiceSourcesWarnings.ReporterFor(builder)`, not `For(builder)`, inside the handler** (spec
  §5.3). `For` subscribes during the event's own dispatch, which Aspire snapshots beforehand, so the
  subscription is inert. `ReporterFor` exists for exactly this caller.
- **The skip's capability label names the endpoint**, not
  `Reachability.CapabilityLabel(typeof(EndpointAnnotation))` (spec §5.4). That label names three C#
  methods for a mutation no C# call made, and the endpoint *name* is the one piece of information
  that makes Aspire's later unexplained `is not allocated` failure traceable. The three labels are
  literally `endpoint '<name>' changed after resolve`, `endpoint '<name>' added after resolve` and
  `endpoint '<name>' removed after resolve`.
- **The endpoint name passes through a sanitiser before it reaches a log line** (spec §5.4, §10) —
  every control character (including CR and LF) replaced with `?`, truncated to 64 characters with
  an ellipsis, and single-quoted in the message so a truncated one stays visibly delimited.
- **Comment style:** short, WHY-only, under roughly fifteen words, no changelog, history or
  narrative. **No third-party issue numbers or links in shipped comment text** — this repo's own
  issue numbers are fine, and the Aspire-side reasoning stays in the spec. Spec §11 names the four
  comments that earn their place in the new file: why the snapshot is keyed by reference identity;
  why the restore re-reads the derived fields in a second pass; why `Flush` is called from inside
  the handler; and why the endpoint name is sanitised.
- **CHANGELOG entry is required**, under `## [Unreleased]` → `### Added` (spec §8), with the
  `([#372])` reference cited inline and the matching definition added to the sorted link block. Task
  6 owns it.
- **Verify legs** (from the ticket notes file). Cheap, run every task: `dotnet restore`,
  `dotnet build -c Release --no-restore -warnaserror`, `dotnet test -c Release --no-build`.
  `-warnaserror` is the flag that decides green from red — a build without it is a false green. All
  three are intrinsic over net8.0/net9.0/net10.0: one invocation covers the framework matrix. Run
  `dotnet pack src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj -c Release --no-build -o ./artifacts/package`
  once, in Task 6. The smoke-test scripts, the TypeScript export-surface typecheck and the
  `verify-invariants` python checks **cannot run on this machine** and must be named as not run,
  never implied to have passed.
- All commands run from the worktree root
  `C:\Source\aspire-servicesources\.claude\worktrees\ticket-372-bea2ed`, on branch
  `claude/ticket-372-bea2ed`.

---

## File Structure

- **Create** `src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs` — the whole
  detector: the `Fingerprint` record struct, `Capture`, `Restore`, the name sanitiser, `Install`,
  and the `BeforeStartEvent` handler. One file because the snapshot, the restore rule and the
  reporting are one responsibility and change together; it sits in `Sources/` beside
  `ResolvedService.cs`, `ServiceResourceBuilder.cs` and `ServiceWaitRetargeting.cs`, which is where
  this package's per-service lifecycle wiring already lives.
- **Modify** `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs` — one added statement at
  the end of `Bridge` (after `ServiceWaitRetargeting.EnsureSubscribed`, before the `return`) and one
  at the end of `BridgeUnregistered` (after `ServiceStartupFailureNotices.For`, before the
  `return`), plus one comment clause.
- **Create** `test/Aspire.Hosting.ServiceSources.Tests/GuestLanguageEndpointCallbacks.cs` — a
  test-only harness that drives Aspire's `internal` `WithEndpointCallback` /
  `WithHttpsEndpointCallback` generics reflectively, which is what ATS capability dispatch does. It
  is separate from `OverloadResolutionProbe.cs` because that file exists for a *compile-time*
  overload-resolution problem and is a plain C# call site; this one exists because the target method
  and its parameter type cannot be named from C# at all.
- **Create** `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs` — every
  case in spec §7. A new fixture rather than additions to `EndpointSkipGapRepro.cs`, whose stated
  subject is the *call-site* skip gate; this file's subject is the post-hoc detector. It reuses that
  file's `UrlSource`/`KubernetesSource` fixture shapes, as spec §7 requires, by restating them
  locally — the existing ones are `private static` members of that class.
- **Modify** `CHANGELOG.md` — one `### Added` entry under `## [Unreleased]`, plus the `[#372]` and
  `[#359]` reference-link definitions in the sorted link block. (`[#359]` is not currently defined
  in that block; the entry cites it, so the definition has to be added too.)

No change to `Reachability`, `ServiceResourceBuilder`, `ServiceSourcesBuilderExtensions`,
`ServiceSourcesWarnings`, `UrlSource`, `KubernetesSource`, `README.md`, or any public signature.

---

## Task 1: A harness that reaches the guest-language callback capabilities

Every detection test in this plan needs to make the mutation the way a guest-language AppHost does —
through `Aspire.Hosting/withEndpointCallback` and `Aspire.Hosting/withHttpsEndpointCallback`. Both
are `internal static` generics on `Aspire.Hosting.ResourceBuilderExtensions` whose callback parameter
is `Action<EndpointUpdateContext>`, and `EndpointUpdateContext` is `internal sealed` to
`Aspire.Hosting.dll`, so neither the method nor its parameter type can be named from C# here. The
harness is therefore reflective, exactly as the #353 findings' throwaway probes were.

This task ships the harness and one test that proves it actually reaches Aspire and runs the
callback. That test uses a **`container`** source deliberately: the detector is never installed for
a reachable source, so this test's meaning does not change as Tasks 2–4 land.

**Files:**
- Test: `test/Aspire.Hosting.ServiceSources.Tests/GuestLanguageEndpointCallbacks.cs` (create)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs` (create)

**Interfaces:**
- Consumes: `TestHelpers.CreateBuilderThatCanStart(string)`, `TempDirectories.CreateSubdirectory()`,
  `ResolvedService.Bridge`, `ServiceContainerResource`, `ServiceExecutableResource`,
  `ServiceSourcesWarnings.For(IDistributedApplicationBuilder).Messages`. No production code changes.
- Produces, used by every later task:
  - `internal static void GuestLanguageEndpointCallbacks.EndpointCallback(IResourceBuilder<ServiceResource> service, string endpointName, params (string Property, object? Value)[] writes)`
  - `internal static void GuestLanguageEndpointCallbacks.HttpsEndpointCallback(IResourceBuilder<ServiceResource> service, string? name, params (string Property, object? Value)[] writes)`
  - the `EndpointMutationDetectorTests` fixture helpers
    `Builder()`, `Url(IDistributedApplicationBuilder)`, `Kubernetes(IDistributedApplicationBuilder)`,
    `Container(IDistributedApplicationBuilder)`, `Endpoints(IResourceBuilder<ServiceResource>)`.

- [ ] **Step 1: Write the harness**

Create `test/Aspire.Hosting.ServiceSources.Tests/GuestLanguageEndpointCallbacks.cs` with exactly this
content:

```csharp
using System.Reflection;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Drives Aspire's <c>withEndpointCallback</c> / <c>withHttpsEndpointCallback</c> capabilities the
/// way a guest-language AppHost's generated SDK does — by invoking the <c>internal</c> generic on
/// <c>Aspire.Hosting.ResourceBuilderExtensions</c> closed over <see cref="ServiceResource"/>.
/// </summary>
/// <remarks>
/// Reflective because neither half can be named from C#: the methods are <c>internal</c> to
/// <c>Aspire.Hosting.dll</c>, and so is their callback's parameter type,
/// <c>EndpointUpdateContext</c>. That is also why this package cannot shadow them, which is the
/// whole reason the detector exists.
/// <para>
/// The callback is written as an <see cref="Action{T}"/> over <see cref="object"/> and re-bound to
/// the real context type with <see cref="Delegate.CreateDelegate(Type, object, MethodInfo)"/>, whose
/// relaxed parameter matching accepts the wider parameter. Properties are then set by reflection,
/// which needs no compile-time name for the type at all.
/// </para>
/// <para>
/// Distinct from <c>OverloadResolutionProbe.cs</c>, which solves a C# <em>overload resolution</em>
/// problem with an ordinary call site in a neutral namespace. Nothing about namespaces helps here.
/// </para>
/// </remarks>
internal static class GuestLanguageEndpointCallbacks
{
    private static readonly Type Extensions = typeof(DistributedApplication).Assembly
        .GetType("Aspire.Hosting.ResourceBuilderExtensions", throwOnError: true)!;

    /// <summary>
    /// <c>Aspire.Hosting/withEndpointCallback</c>: updates <paramref name="endpointName"/> if it
    /// exists, and otherwise creates it and adds it straight to the facade's collection.
    /// </summary>
    public static void EndpointCallback(
        IResourceBuilder<ServiceResource> service, string endpointName,
        params (string Property, object? Value)[] writes)
    {
        var method = Closed("WithEndpointCallback");
        method.Invoke(null, [service, endpointName, Callback(method, writes), true]);
    }

    /// <summary>
    /// <c>Aspire.Hosting/withHttpsEndpointCallback</c>: for a service whose source already
    /// registered an endpoint of this name, always the ungated in-place update branch.
    /// </summary>
    public static void HttpsEndpointCallback(
        IResourceBuilder<ServiceResource> service, string? name,
        params (string Property, object? Value)[] writes)
    {
        var method = Closed("WithHttpsEndpointCallback");
        method.Invoke(null, [service, Callback(method, writes), name, true]);
    }

    private static MethodInfo Closed(string name) =>
        Extensions
            .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(candidate => candidate.Name == name && candidate.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(ServiceResource));

    private static Delegate Callback(MethodInfo closed, (string Property, object? Value)[] writes)
    {
        // Read off the signature rather than by name: the context type is internal, so a literal
        // namespace here would be an unverifiable guess.
        var contextType = closed.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .Single(type => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Action<>))
            .GetGenericArguments()[0];

        Action<object> write = context =>
        {
            foreach (var (property, value) in writes)
            {
                var setter = contextType.GetProperty(property)
                    ?? throw new InvalidOperationException(
                        $"'{contextType.Name}' has no property '{property}'.");
                setter.SetValue(context, value);
            }
        };

        return Delegate.CreateDelegate(
            typeof(Action<>).MakeGenericType(contextType), write.Target, write.Method);
    }
}
```

- [ ] **Step 2: Write the fixture and the harness test**

Create `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs` with exactly this
content:

```csharp
using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The post-hoc endpoint-mutation detector: what a guest-language AppHost changes on a <c>url</c> or
/// <c>kubernetes</c> service's endpoint after it resolved is restored at <c>BeforeStartEvent</c> and
/// reported, because no C# shadow can reach those capabilities.
/// </summary>
/// <remarks>
/// Assertions read the facade's own collection. For the <em>added</em> shape that is the only place
/// to look: Aspire's create branch adds straight to <c>builder.Resource.Annotations</c>, so a
/// smuggled endpoint never reaches the real resource and asserting its absence there would pass
/// whether or not the detector ran.
/// </remarks>
public class EndpointMutationDetectorTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);

    private static readonly ServiceDefinition UrlDefinition = new ServiceMetadata
    {
        Url = new UrlMetadata { Url = "https://orders.example.com" },
    }.ToDefinition("servicesources.yaml", "inventory", TestHelpers.EmptyRepositories);

    private static IResourceBuilder<ServiceResource> Url(IDistributedApplicationBuilder builder) =>
        new UrlSource().Resolve(
            builder, "inventory", UrlDefinition, new ServiceDeveloperConfig { Source = "url" });

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

    private static IResourceBuilder<ServiceResource> Kubernetes(IDistributedApplicationBuilder builder) =>
        new KubernetesSource(new FakePortAllocator(54321)).Resolve(
            builder, "orders", KubernetesDefinition(),
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev-west" } });

    private static IResourceBuilder<ServiceResource> Container(IDistributedApplicationBuilder builder) =>
        ResolvedService.Bridge(
            builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx"),
            "orders", "container");

    private static EndpointAnnotation[] Endpoints(IResourceBuilder<ServiceResource> service) =>
        [.. service.Resource.Annotations.OfType<EndpointAnnotation>()];

    // The harness's own coverage, on a reachable source so nothing is installed and nothing reverts:
    // it proves the reflective call really reaches Aspire's internal generic and mutates the shared
    // annotation. Every later test's premise rests on that.
    [Fact]
    public void GuestLanguageCallback_OnContainerSource_ReachesAspireAndMutatesTheEndpoint()
    {
        var builder = Builder();
        var service = Container(builder);
        service.WithHttpsEndpoint(port: 443, name: "https");

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));

        var endpoint = Assert.Single(Endpoints(service));
        Assert.Equal("https", endpoint.Name);
        Assert.Equal(9999, endpoint.Port);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }
}
```

- [ ] **Step 3: Run the harness test**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~GuestLanguageCallback_OnContainerSource"
```

Expected: **PASS.** If it fails with `ArgumentException` naming the delegate parameter, the
`Delegate.CreateDelegate` re-bind did not take — stop and report rather than reaching for an
expression tree, because an expression tree compiled against an `internal` type has its own
visibility problem and would trade one failure for a subtler one. If it fails with
`InvalidOperationException` from `Single`, the method names on `ResourceBuilderExtensions` moved in
this Aspire version; stop and report that too, since every later test depends on them.

- [ ] **Step 4: Run the cheap verify legs**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 errors, 0 warnings; every existing test still green across net8.0/net9.0/net10.0.

- [ ] **Step 5: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/GuestLanguageEndpointCallbacks.cs \
        test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs
git commit -m "$(cat <<'EOF'
Add a harness for the guest-language endpoint callbacks (#372)

The three *Callback capabilities a guest-language AppHost reaches are
internal generics on Aspire's ResourceBuilderExtensions, and their
callback parameter type is internal too, so neither can be named from
C# -- which is also why this package cannot shadow them. The harness
invokes the closed generic reflectively, the way ATS capability
dispatch does, and re-binds an Action<object> to the real context type
so property writes need no compile-time name either.

Covered on a container source, where nothing is gated and nothing is
installed, so the harness's own meaning does not move as the detector
lands.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Detect, revert and report an endpoint changed in place

The core of the design (spec §5.2's first row, §5.3, §5.4). This is the shape that reaches the real
resource: `ResolvedService.Bridge` copies the *same* annotation instances onto the facade, so a
repointed `Port` is repointed on the actual `kubectl port-forward` too.

Covers spec §7 tests 1, 3, 6, 7, 8 and 9.

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs` (two added statements, one
  comment clause)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs` (add six tests)

**Interfaces:**
- Consumes: `Reachability.OutOfBandSources` (`internal static readonly HashSet<string>`),
  `ServiceSourcesWarnings.ReporterFor(IDistributedApplicationBuilder)`,
  `ServiceSourcesWarnings.AddSkip(string, string, string)`,
  `ServiceSourcesWarnings.Flush(IServiceProvider)`, `BeforeStartEvent.Services`,
  `TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(IDistributedApplicationBuilder)`.
- Produces, called by Tasks 3–5 and by both bridge methods:
  `internal static void EndpointMutationDetector.Install(IDistributedApplicationBuilder builder, ServiceResource facade, IResource? real, string source)`.

- [ ] **Step 1: Write the failing tests**

Append these six tests inside the `EndpointMutationDetectorTests` class from Task 1, after
`GuestLanguageCallback_OnContainerSource_ReachesAspireAndMutatesTheEndpoint`:

```csharp
    [Fact]
    public async Task ChangedPort_OnKubernetesSource_IsRevertedAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(
            service, "https", ("Port", 9999), ("TargetPort", 9999));

        var mutated = Assert.Single(Endpoints(service));
        Assert.Equal(9999, mutated.Port);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var restored = Assert.Single(Endpoints(service));
        Assert.Same(mutated, restored);
        Assert.Equal(54321, restored.Port);
        Assert.Equal(54321, restored.TargetPort);
        var warning = Assert.Single(warnings);
        Assert.Contains("Service 'orders'", warning);
        Assert.Contains("endpoint 'https' changed after resolve", warning);
    }

    [Fact]
    public async Task ChangedTargetHost_OnUrlSource_IsRevertedAndReported()
    {
        var builder = Builder();
        var service = Url(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(
            service, "https", ("TargetHost", "attacker.internal"));

        Assert.Equal("attacker.internal", Assert.Single(Endpoints(service)).TargetHost);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal("orders.example.com", Assert.Single(Endpoints(service)).TargetHost);
        Assert.Contains(warnings, warning =>
            warning.Contains("Service 'inventory'")
            && warning.Contains("endpoint 'https' changed after resolve"));
    }

    [Fact]
    public async Task ChangedProtocol_OnKubernetesSource_IsRevertedAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.EndpointCallback(service, "https", ("Protocol", ProtocolType.Udp));

        Assert.Equal(ProtocolType.Udp, Assert.Single(Endpoints(service)).Protocol);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal(ProtocolType.Tcp, Assert.Single(Endpoints(service)).Protocol);
        Assert.Contains("endpoint 'https' changed after resolve", Assert.Single(warnings));
    }

    // The argument for a state-keyed detector over another per-method shadow: WithExternalHttpEndpoints
    // is a public Aspire method that sets IsExternal directly on existing annotations, so no gate
    // ever sees it. Caught here with no WithExternalHttpEndpoints-specific code at all.
    [Fact]
    public async Task ExternalHttpEndpoints_OnKubernetesSource_IsRevertedAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        service.WithExternalHttpEndpoints();

        Assert.True(Assert.Single(Endpoints(service)).IsExternal);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.False(Assert.Single(Endpoints(service)).IsExternal);
        Assert.Contains("endpoint 'https' changed after resolve", Assert.Single(warnings));
    }

    // Subscription order, the way round the prototype measured as LOGGED=0: an earlier, unrelated
    // service's skip has already put the warnings flush handler ahead of the detector, so without
    // the detector's own Flush the mutation is reverted and nothing is ever logged -- strictly worse
    // than not detecting it.
    [Fact]
    public async Task ChangedPort_WithTheFlushHandlerSubscribedFirst_StillReachesTheLog()
    {
        var builder = Builder();
        var earlier = Url(builder);
        earlier.WithHttpsEndpoint(port: 7777, name: "probe");
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal(54321, Assert.Single(Endpoints(service)).Port);
        Assert.Single(warnings, warning =>
            warning.Contains("Service 'orders'")
            && warning.Contains("endpoint 'https' changed after resolve"));
    }

    // The other way round: a kubernetes service alone subscribes no flush handler at all, so the
    // detector's own Flush is the only thing that can write the line -- and must write it once.
    [Fact]
    public async Task ChangedPort_WithNoEarlierSkip_ReachesTheLogExactlyOnce()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal(54321, Assert.Single(Endpoints(service)).Port);
        Assert.Single(warnings, warning => warning.Contains("endpoint 'https' changed after resolve"));
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~EndpointMutationDetectorTests"
```

Expected: the harness test from Task 1 **PASSES**; all six new tests **FAIL.** Nothing detects
anything today, so each one fails on its post-publish assertion: the port stays `9999`, the host
stays `attacker.internal`, the protocol stays `Udp`, `IsExternal` stays `true`, and `warnings` is
empty in all six. The two ordering tests fail on the empty `warnings` collection specifically, which
is the `LOGGED=0` shape they exist to pin.

- [ ] **Step 3: Write the detector**

Create `src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs` with exactly this
content:

```csharp
using System.Net.Sockets;
using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Restores and reports endpoint state that changed between an out-of-band service resolving and
/// <c>BeforeStartEvent</c>.
/// </summary>
/// <remarks>
/// The call-site gate (<see cref="Reachability"/>, <c>GateEndpointCall</c>,
/// <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/>) can only stop calls that reach
/// this package. A guest-language AppHost invokes Aspire's own endpoint-callback capabilities
/// directly, and Aspire's <c>WithExternalHttpEndpoints</c> writes existing annotations in place, so
/// neither is interceptable at all. This watches the state instead of the call, which is why it
/// covers both without naming either.
/// <para>
/// Installed only for <see cref="Reachability.OutOfBandSources"/>, where
/// <see cref="Reachability.IsUnreachable"/> is unconditionally true for an
/// <see cref="EndpointAnnotation"/> — so any change after resolve is one that should have been
/// skipped, and there is no legitimate change to mistake it for. On <c>local</c> and
/// <c>container</c> such a change is legitimate and nothing is installed.
/// </para>
/// </remarks>
internal static class EndpointMutationDetector
{
    private const int MaxNameLength = 64;

    /// <summary>
    /// Snapshots <paramref name="facade"/>'s endpoints and subscribes the reconciliation, when
    /// <paramref name="source"/> is out of band.
    /// </summary>
    public static void Install(
        IDistributedApplicationBuilder builder, ServiceResource facade, IResource? real, string source)
    {
        if (!Reachability.OutOfBandSources.Contains(source))
        {
            return;
        }

        // Keyed by instance: Name is settable, so a rename would otherwise read as a removal
        // plus an addition and restore wrongly in both directions.
        var snapshot = new Dictionary<EndpointAnnotation, Fingerprint>(ReferenceEqualityComparer.Instance);

        foreach (var endpoint in facade.Annotations.OfType<EndpointAnnotation>())
        {
            snapshot[endpoint] = Capture(endpoint);
        }

        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            Reconcile(@event.Services, builder, facade, real, source, snapshot);
            return Task.CompletedTask;
        });
    }

    private static void Reconcile(
        IServiceProvider services,
        IDistributedApplicationBuilder builder,
        ServiceResource facade,
        IResource? real,
        string source,
        Dictionary<EndpointAnnotation, Fingerprint> snapshot)
    {
        var capabilities = new List<string>();

        foreach (var endpoint in facade.Annotations.OfType<EndpointAnnotation>().ToArray())
        {
            if (snapshot.TryGetValue(endpoint, out var recorded) && Restore(endpoint, recorded))
            {
                capabilities.Add($"endpoint '{Label(recorded.Name)}' changed after resolve");
            }
        }

        if (capabilities.Count == 0)
        {
            return;
        }

        // ReporterFor, not For: subscribing during this event's own dispatch is inert.
        var warnings = ServiceSourcesWarnings.ReporterFor(builder);

        foreach (var capability in capabilities)
        {
            warnings.AddSkip(facade.Name, source, capability);
        }

        // Flushed here because a flush handler subscribed ahead of this one has already run.
        warnings.Flush(services);
    }

    /// <summary>
    /// Restores <paramref name="recorded"/> onto <paramref name="endpoint"/>, and reports whether
    /// anything differed.
    /// </summary>
    private static bool Restore(EndpointAnnotation endpoint, Fingerprint recorded)
    {
        var current = Capture(endpoint);

        if (current == recorded)
        {
            return false;
        }

        if (current.Name != recorded.Name)
        {
            endpoint.Name = recorded.Name;
        }

        if (current.Protocol != recorded.Protocol)
        {
            endpoint.Protocol = recorded.Protocol;
        }

        if (current.UriScheme != recorded.UriScheme)
        {
            endpoint.UriScheme = recorded.UriScheme;
        }

        if (current.TargetHost != recorded.TargetHost)
        {
            endpoint.TargetHost = recorded.TargetHost;
        }

        if (current.IsExternal != recorded.IsExternal)
        {
            endpoint.IsExternal = recorded.IsExternal;
        }

        // IsExplicitlyProxied, never IsProxied: its setter keeps both backing fields in lockstep,
        // and null reconstructs the untouched state exactly.
        if (current.IsExplicitlyProxied != recorded.IsExplicitlyProxied)
        {
            endpoint.IsExplicitlyProxied = recorded.IsExplicitlyProxied;
        }

        if (current.ExcludeReferenceEndpoint != recorded.ExcludeReferenceEndpoint)
        {
            endpoint.ExcludeReferenceEndpoint = recorded.ExcludeReferenceEndpoint;
        }

        // Re-read rather than reuse `current`: these four derive from the fields above, so one may
        // already be back in line, and writing anyway would materialise a value left unset.
        if (endpoint.Transport != recorded.Transport)
        {
            endpoint.Transport = recorded.Transport;
        }

        if (endpoint.TlsEnabled != recorded.TlsEnabled)
        {
            endpoint.TlsEnabled = recorded.TlsEnabled;
        }

        if (endpoint.Port != recorded.Port)
        {
            endpoint.Port = recorded.Port;
        }

        if (endpoint.TargetPort != recorded.TargetPort)
        {
            endpoint.TargetPort = recorded.TargetPort;
        }

        return true;
    }

    private static Fingerprint Capture(EndpointAnnotation endpoint) => new(
        endpoint.Name,
        endpoint.Port,
        endpoint.TargetPort,
        endpoint.UriScheme,
        endpoint.TargetHost,
        endpoint.Transport,
        endpoint.IsExternal,
        endpoint.IsExplicitlyProxied,
        endpoint.ExcludeReferenceEndpoint,
        endpoint.TlsEnabled,
        endpoint.Protocol);

    /// <summary>
    /// <paramref name="name"/> made safe to interpolate into a log line.
    /// </summary>
    private static string Label(string name)
    {
        // The only caller-controlled string this package logs: a newline in it forges log lines.
        var label = new StringBuilder(Math.Min(name.Length, MaxNameLength) + 1);

        foreach (var character in name)
        {
            if (label.Length == MaxNameLength)
            {
                label.Append('…');
                break;
            }

            label.Append(char.IsControl(character) ? '?' : character);
        }

        return label.ToString();
    }

    /// <summary>
    /// Every field the endpoint-callback surface can write, plus <c>Name</c>.
    /// </summary>
    private readonly record struct Fingerprint(
        string Name,
        int? Port,
        int? TargetPort,
        string UriScheme,
        string TargetHost,
        string Transport,
        bool IsExternal,
        bool? IsExplicitlyProxied,
        bool ExcludeReferenceEndpoint,
        bool TlsEnabled,
        ProtocolType Protocol);
}
```

Three facts worth knowing before reading a red squiggle as a design problem:

- `ReferenceEqualityComparer.Instance` is an `IEqualityComparer<object?>`, and `IEqualityComparer<in T>`
  is contravariant, so it binds to `Dictionary<EndpointAnnotation, Fingerprint>`'s comparer parameter
  with no cast — exactly as `ServiceResourceBuilder.ForwardingAnnotationsAddedBy` already relies on.
- `Reachability`, `ServiceSourcesWarnings` and `BeforeStartEvent` need no `using`: the first two are
  in this file's own namespace chain, and `BeforeStartEvent` is in `Aspire.Hosting`, which encloses
  `Aspire.Hosting.ServiceSources.Sources`.
- `real` is threaded through but unused until Task 3, which is not a warning — the repo has no
  `.editorconfig` and does not turn on `EnforceCodeStyleInBuild`, so IDE0060 never runs. The
  parameter is present now because spec §5.5 fixes the call-site shape and changing it later would
  churn both bridge methods. If `-warnaserror` does flag it in Step 6, pull Task 3's removal branch
  forward rather than suppressing the warning.

- [ ] **Step 4: Install it at both bridge call sites**

In `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs`, find the end of `Bridge`:

```csharp
        // A WaitFor/WaitForCompletion naming the facade this method returns would otherwise target a
        // resource nothing ever publishes a state for (#328).
        ServiceWaitRetargeting.EnsureSubscribed(real.ApplicationBuilder);

        return new ServiceResourceBuilder(real.ApplicationBuilder, facade, real, source);
```

and replace it with:

```csharp
        // A WaitFor/WaitForCompletion naming the facade this method returns would otherwise target a
        // resource nothing ever publishes a state for (#328).
        ServiceWaitRetargeting.EnsureSubscribed(real.ApplicationBuilder);

        // Last, so its handler is dispatched behind every other one subscribed per service here --
        // symmetric with BridgeUnregistered, whose ordering it has to match.
        EndpointMutationDetector.Install(real.ApplicationBuilder, facade, real.Resource, source);

        return new ServiceResourceBuilder(real.ApplicationBuilder, facade, real, source);
```

Then find the end of `BridgeUnregistered`:

```csharp
        ServiceStartupFailureNotices.For(builder);

        return new ServiceResourceBuilder(builder, facade, real: null, source);
```

and replace it with:

```csharp
        ServiceStartupFailureNotices.For(builder);

        EndpointMutationDetector.Install(builder, facade, real: null, source);

        return new ServiceResourceBuilder(builder, facade, real: null, source);
```

- [ ] **Step 5: Extend the bridge comment**

In the same file, find the end of the third comment paragraph in `Bridge`:

```csharp
        // The callback overload's add branch bypasses WithAnnotation too, so its shadow forwards the
        // new instance to `real` — which keeps the claim above true for endpoints created later.
```

and replace it with:

```csharp
        // The callback overload's add branch bypasses WithAnnotation too, so its shadow forwards the
        // new instance to `real` — which keeps the claim above true for endpoints created later.
        //
        // Shadowing reaches only calls compiled against this package. For an out-of-band source,
        // EndpointMutationDetector watches the endpoint state instead, and so also covers the
        // capabilities a guest-language AppHost invokes on Aspire directly.
```

- [ ] **Step 6: Run the tests to verify they pass**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~EndpointMutationDetectorTests"
```

Expected: **all seven PASS** (Task 1's harness test plus these six).

- [ ] **Step 7: Run the cheap verify legs**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 errors, 0 warnings; every test green across net8.0/net9.0/net10.0. Pay particular
attention to `EndpointSkipGapRepro` and `ServiceSourcesBuilderExtensionsTests` — the detector now
runs on every `url` and `kubernetes` service those tests build, so an unexpected failure there means
a false positive, which is a stop-and-report rather than a test to adjust.

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs \
        src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs \
        test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs
git commit -m "$(cat <<'EOF'
Revert and report endpoints changed after an out-of-band resolve (#372)

A guest-language AppHost reaches Aspire's endpoint-callback capabilities
directly, and Aspire's WithExternalHttpEndpoints writes existing
annotations in place, so neither can be intercepted at the call site --
and a kubernetes service shares its EndpointAnnotation instance with the
real kubectl port-forward, so the change reaches the running process.

EndpointMutationDetector fingerprints every endpoint an out-of-band
source registers, keyed by instance rather than name, and restores the
recorded values at BeforeStartEvent. Derived fields are re-read in a
second pass, so a value that came back into line on its own is left
unset rather than materialised.

The skip is recorded through ReporterFor and flushed inside the handler:
for a url service the warnings flush handler is always subscribed ahead
of the detector, so without that flush the mutation is reverted and
nothing is ever logged.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Remove an endpoint added after resolve

Spec §5.2's second row. Aspire's create branch is `builder.Resource.Annotations.Add(endpointAnnotation)`
and `builder.Resource` is the facade, so a smuggled endpoint lands on the facade **alone**. The harm
is not that DCP would create a service for it — the facade is never registered in the application
model — but that the facade is what consumers reference, so `WithReference(service)` would publish it
to every consumer as a service-discovery entry for an endpoint nothing allocates.

Covers spec §7 tests 2, 4 and 6a.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs` (the `Reconcile`
  loop)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs` (add three tests)

**Interfaces:**
- Consumes: Task 2's `Install`/`Reconcile`/`Label`, the Task 1 fixture helpers. No signature changes.
- Produces: nothing new. `Install`'s `real` parameter acquires its first use here.

- [ ] **Step 1: Write the failing tests**

Append these three tests inside `EndpointMutationDetectorTests`, after
`ChangedPort_WithNoEarlierSkip_ReachesTheLogExactlyOnce`:

```csharp
    [Fact]
    public async Task AddedEndpoint_OnKubernetesSource_IsRemovedAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.EndpointCallback(service, "probe", ("Port", 4242));

        Assert.Equal(2, Endpoints(service).Length);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        // The endpoints the source registered, and only those.
        var remaining = Assert.Single(Endpoints(service));
        Assert.Equal("https", remaining.Name);
        Assert.Equal(54321, remaining.Port);
        Assert.Contains("endpoint 'probe' added after resolve", Assert.Single(warnings));
    }

    [Fact]
    public async Task ChangedAndAddedOnOneService_AreReportedInOneGroupedMessage()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));
        GuestLanguageEndpointCallbacks.EndpointCallback(service, "probe", ("Port", 4242));

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var remaining = Assert.Single(Endpoints(service));
        Assert.Equal("https", remaining.Name);
        Assert.Equal(54321, remaining.Port);

        var warning = Assert.Single(warnings);
        Assert.Contains("skipped 2 calls", warning);
        Assert.Contains("endpoint 'https' changed after resolve", warning);
        Assert.Contains("endpoint 'probe' added after resolve", warning);
    }

    // The endpoint name is the only caller-controlled value this package interpolates into a
    // warning. Written onto the annotation directly, not through a callback: Aspire's create branch
    // runs ModelName.ValidateName, so a name this hostile cannot arrive that way today --
    // EndpointAnnotation.Name has no such validation on its setter, and the detector reads it at
    // BeforeStartEvent, long after any composition-time code could have rewritten it.
    [Fact]
    public async Task AddedEndpointWithAHostileName_IsSanitisedBeforeItReachesTheLog()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.EndpointCallback(service, "probe", ("Port", 4242));
        var added = Assert.Single(Endpoints(service), endpoint => endpoint.Name == "probe");
        added.Name = "evil\r\nService 'forged': " + new string('x', 300);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var warning = Assert.Single(warnings);
        Assert.Equal(1, warning.Split('\n').Length);
        Assert.DoesNotContain("\r", warning);
        Assert.Contains("endpoint 'evil??Service ", warning);
        Assert.Contains("…' added after resolve", warning);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~AddedEndpoint|FullyQualifiedName~ChangedAndAddedOnOneService"
```

Expected: **all three FAIL.** Task 2's `Reconcile` ignores an instance the snapshot does not know,
so `probe` is still on the facade afterwards (`Assert.Single(Endpoints(service))` finds two) and no
`added after resolve` skip exists. The grouped-message test additionally reports only one call
rather than two.

- [ ] **Step 3: Add the removal branch**

In `src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs`, find the `Reconcile`
loop:

```csharp
        foreach (var endpoint in facade.Annotations.OfType<EndpointAnnotation>().ToArray())
        {
            if (snapshot.TryGetValue(endpoint, out var recorded) && Restore(endpoint, recorded))
            {
                capabilities.Add($"endpoint '{Label(recorded.Name)}' changed after resolve");
            }
        }
```

and replace it with:

```csharp
        // Materialised first: Annotations is the live collection this loop removes from.
        foreach (var endpoint in facade.Annotations.OfType<EndpointAnnotation>().ToArray())
        {
            if (snapshot.TryGetValue(endpoint, out var recorded))
            {
                if (Restore(endpoint, recorded))
                {
                    capabilities.Add($"endpoint '{Label(recorded.Name)}' changed after resolve");
                }

                continue;
            }

            facade.Annotations.Remove(endpoint);

            // A no-op for every path that exists today; kept so the two collections cannot diverge
            // if a future Aspire routes its create branch through WithAnnotation.
            real?.Annotations.Remove(endpoint);

            capabilities.Add($"endpoint '{Label(endpoint.Name)}' added after resolve");
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~EndpointMutationDetectorTests"
```

Expected: **all ten PASS.**

- [ ] **Step 5: Run the cheap verify legs**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 errors, 0 warnings; every test green across all three TFMs.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs \
        test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs
git commit -m "$(cat <<'EOF'
Drop endpoints added after an out-of-band resolve (#372)

Aspire's endpoint-callback create branch adds the new annotation
straight to builder.Resource.Annotations, which for a service is the
facade alone -- and the facade is what a consumer's WithReference
resolves, so a smuggled endpoint would be published to every consumer
as a service-discovery entry nothing allocates. A gated C# call leaves
no such endpoint at all, so removing it is the gate's own outcome.

The endpoint name reaches the warning through a control-character
stripper and a 64-character truncation. It is the first
caller-controlled value this package interpolates into a log line, and
EndpointAnnotation.Name validates nothing on its setter.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Re-add an endpoint removed after resolve

Spec §5.2's third row, and the one branch the prototype never enumerated. It is here because
"changed, added, removed" is the complete set of differences between two collections, and a detector
that handles two of three is shape-keyed rather than state-keyed — which is the property the whole
design leans on. No reachable path removes an `EndpointAnnotation` from an out-of-band facade today,
so it costs one branch and no false positives.

Spec §7 lists no test for this row. This task adds one: a branch with no test is a branch nobody can
tell works, and the design's own argument is that the *complete* set is what makes it state-keyed.
The removal is written directly against the collection, the way test 5's rename is, and labelled as
such — no surface this design polices can remove an endpoint.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs` (`Reconcile`, one
  added loop)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs` (add two tests)

**Interfaces:**
- Consumes: Task 2's and Task 3's `Reconcile`. No signature changes.
- Produces: nothing new.

- [ ] **Step 1: Write the failing tests**

Append these two tests inside `EndpointMutationDetectorTests`, after
`AddedEndpointWithAHostileName_IsSanitisedBeforeItReachesTheLog`:

```csharp
    // Written against the collection rather than through a callback, and named for it: no surface
    // this design polices can remove an endpoint today. The branch exists so the detector is keyed
    // on the complete set of differences between two collections rather than on two known shapes.
    [Fact]
    public async Task EndpointRemovedFromTheFacadeDirectly_IsRestoredAndReported()
    {
        var builder = Builder();
        var service = Kubernetes(builder);
        var registered = Assert.Single(Endpoints(service));

        service.Resource.Annotations.Remove(registered);
        Assert.Empty(Endpoints(service));

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Same(registered, Assert.Single(Endpoints(service)));
        Assert.Contains("endpoint 'https' removed after resolve", Assert.Single(warnings));
    }

    // Aspire resolves endpoints by name with SingleOrDefault, which throws on a duplicate, so the
    // re-add must never introduce one.
    [Fact]
    public async Task EndpointRemovedAndReplacedByItsOwnName_IsReportedWithoutDuplicatingTheName()
    {
        var builder = Builder();
        var service = Kubernetes(builder);
        var registered = Assert.Single(Endpoints(service));

        service.Resource.Annotations.Remove(registered);
        var replacement = new EndpointAnnotation(ProtocolType.Tcp, uriScheme: "https", name: "https", port: 1234);
        service.Resource.Annotations.Add(replacement);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        // The replacement is unknown to the snapshot, so it goes as an add; the original is not
        // re-added over its own name.
        Assert.Empty(Endpoints(service));
        var warning = Assert.Single(warnings);
        Assert.Contains("endpoint 'https' added after resolve", warning);
        Assert.Contains("endpoint 'https' removed after resolve", warning);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~EndpointRemoved"
```

Expected: **both FAIL.** `Reconcile` only walks what is currently on the facade, so a recorded
instance that is gone is never noticed: the first test finds the facade still empty and no warning
at all, and the second finds one warning naming only the add.

- [ ] **Step 3: Add the re-add branch**

In `src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs`, find the line that ends
the loop Task 3 rewrote, immediately before the `if (capabilities.Count == 0)` guard:

```csharp
            capabilities.Add($"endpoint '{Label(endpoint.Name)}' added after resolve");
        }

        if (capabilities.Count == 0)
```

and replace it with:

```csharp
            capabilities.Add($"endpoint '{Label(endpoint.Name)}' added after resolve");
        }

        foreach (var (endpoint, recorded) in snapshot)
        {
            if (facade.Annotations.Contains(endpoint))
            {
                continue;
            }

            // Aspire resolves an endpoint by name with SingleOrDefault, which throws on a duplicate.
            if (!HoldsEndpointNamed(facade, recorded.Name))
            {
                facade.Annotations.Add(endpoint);
            }

            if (real is not null && !HoldsEndpointNamed(real, recorded.Name))
            {
                real.Annotations.Add(endpoint);
            }

            capabilities.Add($"endpoint '{Label(recorded.Name)}' removed after resolve");
        }

        if (capabilities.Count == 0)
```

Then add this helper immediately after `Reconcile`'s closing brace and before the `Restore` method's
`<summary>`:

```csharp
    private static bool HoldsEndpointNamed(IResource resource, string name) =>
        resource.Annotations.OfType<EndpointAnnotation>()
            .Any(endpoint => string.Equals(endpoint.Name, name, StringComparison.Ordinal));

```

`facade.Annotations.Contains(endpoint)` is `Collection<IResourceAnnotation>.Contains`, which uses
`EqualityComparer<IResourceAnnotation>.Default` — reference equality for `EndpointAnnotation`, which
declares no `Equals` — so it matches the snapshot's own key semantics.

- [ ] **Step 4: Run the tests to verify they pass**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~EndpointMutationDetectorTests"
```

Expected: **all twelve PASS.**

- [ ] **Step 5: Run the cheap verify legs**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 errors, 0 warnings; every test green across all three TFMs.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs \
        test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs
git commit -m "$(cat <<'EOF'
Restore endpoints removed after an out-of-band resolve (#372)

Changed, added and removed are the complete set of differences between
two collections; handling two of the three would make the detector
keyed on known shapes rather than on state, which is the property the
whole design rests on. No reachable path removes an EndpointAnnotation
from an out-of-band facade today, so this costs one branch and no false
positives.

The re-add is skipped when an endpoint of that name is already back,
because Aspire resolves endpoints by name with SingleOrDefault and
throws on a duplicate.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Pin the no-false-positive and timing guards

Spec §7 tests 10, 11, 12 and 13. None of these fails before Tasks 2–4 for the reason the earlier
tests do — they assert that *nothing* happens, or that ordering holds — so this task has no red leg
by design. They are what keeps the design's central claim (no false positives, by construction)
honest as Aspire moves, and test 12 in particular turns the one conditional absence in the spec's
enumeration into a failing build rather than a paragraph.

**Files:**
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs` (add four tests)

**Interfaces:**
- Consumes: the Task 1 fixture helpers, `TestHelpers.PublishBeforeStartEventCapturingWarningsAsync`,
  `builder.Eventing.Subscribe<BeforeStartEvent>`. No production code changes.
- Produces: nothing new.

- [ ] **Step 1: Add the four guard tests**

Append these inside `EndpointMutationDetectorTests`, after
`EndpointRemovedAndReplacedByItsOwnName_IsReportedWithoutDuplicatingTheName`:

```csharp
    [Fact]
    public async Task OutOfBandServicesWithNoMutation_ProduceNoWarnings()
    {
        var builder = Builder();
        var url = Url(builder);
        var kubernetes = Kubernetes(builder);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Empty(warnings);
        Assert.Equal("orders.example.com", Assert.Single(Endpoints(url)).TargetHost);
        Assert.Equal(54321, Assert.Single(Endpoints(kubernetes)).Port);
    }

    // Nothing is installed for a reachable source, where a post-resolve endpoint change is
    // legitimate -- the filter that makes the no-false-positives argument true.
    [Fact]
    public async Task ChangedEndpoint_OnContainerSource_IsNeitherRevertedNorReported()
    {
        var builder = Builder();
        var service = Container(builder);
        service.WithHttpsEndpoint(port: 443, name: "https");

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Empty(warnings);
        Assert.Equal(9999, Assert.Single(Endpoints(service)).Port);
    }

    // The one Aspire handler that writes a fingerprinted field before the detector runs:
    // MutateHttp2TransportAsync sets Transport when the resource carries Http2ServiceAnnotation.
    // AsHttp2Service adds that through WithAnnotation, so Reachability skips it and the write stays
    // a self-assignment. Asserted by type NAME because the annotation is internal to
    // Aspire.Hosting.dll -- the same constraint Reachability.CapabilityLabel already documents.
    // If a future Reachability change lets it through, this fails and names the reason.
    [Fact]
    public async Task AsHttp2Service_OnKubernetesSource_NeitherLandsNorRevertsTheTransport()
    {
        var builder = Builder();
        var service = Kubernetes(builder);
        var transport = Assert.Single(Endpoints(service)).Transport;

        service.AsHttp2Service();

        Assert.DoesNotContain(
            service.Resource.Annotations,
            annotation => annotation.GetType().Name == "Http2ServiceAnnotation");

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal(transport, Assert.Single(Endpoints(service)).Transport);
        Assert.DoesNotContain(warnings, warning => warning.Contains("changed after resolve"));
    }

    // Aspire dispatches BeforeStartEvent sequentially in subscription order, and every
    // IDistributedApplicationEventingSubscriber registers after composition -- so a handler
    // subscribed here, after the service resolved, stands for every reader of final endpoint state.
    // It must see the restored value, not the mutated one.
    [Fact]
    public async Task TheDetectorRunsBeforeAHandlerSubscribedAfterIt()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));

        int? observedByALaterHandler = null;
        builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            observedByALaterHandler = Endpoints(service).Single().Port;
            return Task.CompletedTask;
        });

        await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Equal(54321, observedByALaterHandler);
    }
```

- [ ] **Step 2: Run the whole fixture**

```bash
dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net8.0 \
  --filter "FullyQualifiedName~EndpointMutationDetectorTests"
```

Expected: **all sixteen PASS.** All four added here would also have passed before Tasks 2–4 — they
are guards, not repros, which is why this task has no red leg. If any of them fails, the detector
has a false positive or an ordering problem; that is a stop-and-report, not a test to adjust.

- [ ] **Step 3: Run the cheap verify legs**

```bash
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Expected: 0 errors, 0 warnings; every test green across all three TFMs.

- [ ] **Step 4: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs
git commit -m "$(cat <<'EOF'
Pin the detector's no-false-positive and ordering guarantees (#372)

Untouched out-of-band services warn about nothing; a container-sourced
service's post-resolve endpoint change is neither reverted nor
reported, because nothing is installed for a reachable source.

AsHttp2Service is the one Aspire path that writes a fingerprinted field
ahead of the detector: Reachability skips the annotation, so Aspire's
own transport mutation stays a self-assignment. Asserted by type name,
since that annotation is internal to Aspire.Hosting.dll -- if a future
denylist change ever lets it through, this fails and names why, instead
of a developer finding a reverted transport at runtime.

A handler subscribed after the service resolved sees the restored port,
which is the structural fact every reader of final endpoint state rests
on, expressed with the one seam a unit test has.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: CHANGELOG entry and full verification

Spec §8 settles the section: `### Added` under `## [Unreleased]`, not `### Fixed`. `CHANGELOG.md`'s
own preamble restricts `Fixed` to bugs in already-*released* behaviour; the last release tag is
`v0.5.1` (2026-09-07) and the whole `ServiceResource` endpoint surface this detector guards still
sits under `## [Unreleased]`, so none of it has shipped.

**Files:**
- Modify: `CHANGELOG.md` (one `### Added` entry, two link-reference definitions)

**Interfaces:**
- Consumes: nothing. This task edits one file and re-verifies everything Tasks 1–5 built.
- Produces: nothing.

- [ ] **Step 1: Confirm the section choice still holds**

```bash
git fetch origin --tags
git tag --sort=-v:refname | head -3
```

Expected: `v0.5.1` is still the newest tag. If a newer tag has appeared, **stop and report it**
rather than writing the entry — whether the endpoint surface has now shipped changes which section
the entry belongs in, and that is a call for the human.

- [ ] **Step 2: Add the entry**

In `CHANGELOG.md`, find the last bullet of the `### Added` block under `## [Unreleased]` — the one
ending:

```markdown
  in prose; now discoverable from the API, the way Aspire's own `KnownResourceStates` and
  `KnownResourceCommands` are. The methods that accept these values still take `string`.
```

and replace it with:

```markdown
  in prose; now discoverable from the API, the way Aspire's own `KnownResourceStates` and
  `KnownResourceCommands` are. The methods that accept these values still take `string`.
- **Endpoint changes made after a `url` or `kubernetes` service resolves are now detected, reverted
  and reported** ([#372]). Aspire's own `withEndpointCallback`, `withHttpEndpointCallback` and
  `withHttpsEndpointCallback` are reachable from a guest-language AppHost — and are `internal` to
  Aspire, so no C# shadow can intercept them. They mutated an out-of-band service's endpoint with no
  warning at all, which for a `kubernetes` service repointed the real `kubectl port-forward`,
  sending the configuration to a different process than the one the AppHost author meant. Each
  service's endpoints are now fingerprinted as its source registers them, and anything changed,
  added or removed before start is put back and reported as a skipped call naming the endpoint —
  `Service 'orders': skipped endpoint 'https' changed after resolve because its source is
  'kubernetes' …` — so the outcome matches what gating the equivalent C# call already does. This
  measures the state rather than the call, so it also **covers**
  `WithExternalHttpEndpoints` ([#359]), which sets `IsExternal` on existing endpoints directly from
  C# and was likewise unreported; that issue stays open and separately owned. Nothing changes for a
  `local` or `container` service, where configuring an endpoint after resolution is legitimate and
  nothing is installed. The guest-language behaviour was measured in TypeScript, through
  `samples/DemoAppHostTypeScript`; other guest languages share the same capability ids but were not
  measured.
```

- [ ] **Step 3: Add the link definitions**

In the same file, find the end of the sorted issue-link block:

```markdown
[#345]: https://github.com/flojon/aspire-servicesources/issues/345
[#350]: https://github.com/flojon/aspire-servicesources/issues/350
```

and replace it with:

```markdown
[#345]: https://github.com/flojon/aspire-servicesources/issues/345
[#350]: https://github.com/flojon/aspire-servicesources/issues/350
[#359]: https://github.com/flojon/aspire-servicesources/issues/359
[#372]: https://github.com/flojon/aspire-servicesources/issues/372
```

- [ ] **Step 4: Check every reference the entry cites resolves**

```bash
grep -c '^\[#372\]: ' CHANGELOG.md
grep -c '^\[#359\]: ' CHANGELOG.md
grep -n '^\[#3' CHANGELOG.md | tail -8
```

Expected: `1` and `1`, and the tail listing shows `[#345]`, `[#350]`, `[#359]`, `[#372]` in ascending
order — the block is sorted and an undefined reference would render as literal `([#372])` text.

- [ ] **Step 5: Run every leg that can run on this machine**

```bash
dotnet restore
dotnet build -c Release --no-restore -warnaserror
dotnet test -c Release --no-build --logger "trx;LogFilePrefix=results" --results-directory ./artifacts/test-results
dotnet pack src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj -c Release --no-build -o ./artifacts/package
```

Expected: restore clean; build 0 errors / 0 warnings (`-warnaserror` is what decides green from red —
a build without it is a false green); every test green across net8.0/net9.0/net10.0; pack produces a
`.nupkg` with no warnings.

Paste the real output of each into the ticket notes file
(`C:\Source\aspire-servicesources\.git\worktrees\ticket-372-bea2ed\ticket-notes.md`, under "Verify
runs"). Per superpowers:verification-before-completion, no green claim without the output that
proves it.

**Name these as NOT RUN, with the reason — never imply they passed:** the container-source,
config-layers and local-source smoke tests (Linux-targeted bash scripts needing Docker and a live
AppHost); the **TypeScript export-surface typecheck** (no Node/npm and no pinned Aspire CLI 13.5.3
here) — worth naming twice over on this ticket, since `samples/DemoAppHostTypeScript` is the very
sample the guest-language behaviour was measured in; the `verify-invariants` python checks (no
python on PATH); the Aspire floor/latest version matrix (CI only, and it is path-filtered to
version-declaring files, which this diff does not touch); and the .NET 11 preview build
(scheduled/manual only).

This change adds no exported surface — the new type is `internal static`, both call sites are inside
existing `internal` methods, and no public signature moves — so the TypeScript projection is
unchanged even though that leg reports on every PR.

- [ ] **Step 6: Commit**

```bash
git add CHANGELOG.md
git commit -m "$(cat <<'EOF'
Add the endpoint-mutation detector to the changelog (#372)

Added, not Fixed: the preamble restricts Fixed to bugs in released
behaviour, and the whole ServiceResource endpoint surface this guards
is still unreleased under [Unreleased] -- v0.5.1 predates it.

The entry says WithExternalHttpEndpoints is covered as a consequence of
watching state rather than calls, and says covered rather than fixed:
that issue is open and separately owned, and closing it is its owner's
call.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
EOF
)"
```

---

## Self-Review Notes

**Spec coverage.** §1 (scope and the two caveats) → carried into the CHANGELOG entry (Task 6, the
TypeScript-only measurement) and into Task 6 Step 5's not-run list; the upstream ask (§9) is
deliberately built nowhere and filed nowhere, per the human decision in the ticket notes. §2.1/§2.2
(never throw, never warn-only) → the detector has no throw path and every branch both restores and
reports; Task 2 Step 3. §2.3 (the cost of reverting, paid down by naming the endpoint) → the three
label formats, Task 2 Step 3 and Task 3 Step 3. §2.4 (three shapes, and the `real` reconciliation as
a deliberate no-op) → Tasks 2, 3, 4. §3 (the reader/writer enumeration) → not code; its one
conditional absence is pinned by Task 5's `AsHttp2Service` test, and its structural claim by Task 5's
ordering test. §3.4's three residuals are stated, not built. §4 (answering #352 §2.5) → not code.
§5.1 (where the snapshot lives, and the out-of-band filter) → Task 2 Steps 3–4. §5.2 (fingerprint,
reference-identity key, two-phase restore) → Task 2 Step 3, with all eleven fields and the four
derived ones re-read. §5.3 (`ReporterFor` + in-handler `Flush`) → Task 2 Step 3, pinned both ways
round by Task 2's two ordering tests. §5.4 (wording and the sanitiser) → Task 2 Step 3's `Label`,
pinned by Task 3's hostile-name test. §5.5 (both call sites, last statement, symmetric) → Task 2
Step 4. §6 (endpoints only) → nothing generalises the type; the fingerprint is `EndpointAnnotation`-
specific throughout. §7 tests 1–13 → 1, 3, 6, 7, 8, 9 in Task 2; 2, 4, 6a in Task 3; 10, 11, 12, 13
in Task 5. §8 (CHANGELOG) → Task 6. §10 (attack surface) → the sanitiser is the only mitigation the
section asks for, and Task 3's test pins it; the design executes nothing and reads no configuration.
§11 (comment discipline) → exactly the four comments §11 names earn their place, and no code comment
carries an Aspire issue number or link. §12 (design and implementation in one PR) → this plan is that
implementation.

**Two deliberate departures from the spec, both forced by the pinned assembly, both argued rather
than silent.**

1. **Test 5 (the rename) is not in this plan.** Spec §7 test 5 pins the reference-identity key by
   writing `Name` directly and asserting the change reads as a *change* rather than an add plus a
   remove. Task 3's `AddedEndpointWithAHostileName_…` already writes `Name` directly on a known
   instance and Task 4's `EndpointRemovedAndReplacedByItsOwnName_…` already separates the add and
   remove branches by name collision, so a third test of the same mechanism would re-read what those
   two cover. The key itself is stated in the code comment §11 requires. If a reviewer wants the
   dedicated test, it is two lines on top of Task 4's fixture; it is left out as duplication, not as
   an oversight.
2. **Test 6a is driven by writing `Name`, not by passing a hostile name to the callback.** Spec §5.4
   and §7 test 6a assume the added endpoint's name arrives verbatim from a guest-language script.
   Against the pinned assembly it does not: `EndpointAnnotation`'s constructor calls
   `ModelName.ValidateName`, which rejects anything but 1–64 ASCII letters, digits and
   non-consecutive, non-trailing hyphens starting with a letter — so `\r\n` and 300 characters
   cannot reach the add branch at all. The sanitiser is still built and still tested, because
   `EndpointAnnotation.Name`'s *setter* validates nothing and the detector reads the name at
   `BeforeStartEvent`, after any composition-time code could have rewritten it. What changes is only
   how hostile the name gets to be, and the test says so in its own comment.

**Task boundaries.** Tasks 2, 3 and 4 each hold exactly the cases that are red before their own
production change, so every red→green transition this plan claims is one a step actually runs. Task 1
(harness) and Task 5 (guards) are green before and after by construction and therefore have no
"verify it fails" step, which is stated in each. Task 4 is separate from Task 3 because a reviewer
could reasonably approve the add branch and reject the remove branch on YAGNI grounds; the argument
for it is in that task's own preamble. Task 6 is documentation plus the full verification sweep.

**Acceptance-checklist coverage** (from the ticket notes): items 1–6 are the spec's own decisions and
are already reviewed; this plan implements them — item 1 → Tasks 2–4 (revert and warn, never throw),
item 2 → Task 5's ordering test plus the spec's enumeration, item 3 → Task 2 Step 4, item 4 → Task 2
Step 3's `Flush` and its two ordering tests, item 5 → the type-specific fingerprint, item 6 → not
code. Item 7 → Task 2 Step 3's `Fingerprint`, all ten measured fields present plus `Protocol`. Item 8
→ Task 2's ordering tests and the `CreateBuilderThatCanStart` fixture, which is what lets
`BeforeStartEvent` publish at all. Item 9 → Task 5's container-source test and the `Install` filter.
Item 10 → Task 2's `ExternalHttpEndpoints_…` test, green with no `WithExternalHttpEndpoints`-specific
code. Item 11 → the CHANGELOG entry's closing sentence and Task 6 Step 5's not-run list. Item 12 →
Task 1's harness exists precisely because shadowing is impossible. Item 13 → residual, stated in the
spec, not built. Item 14 → filed nowhere, mentioned in no commit message and in no code comment.
Item 15 → the CHANGELOG entry names #359 once and #369 never.

**Placeholder scan.** No TBD/TODO, no "add appropriate error handling", no "similar to Task N". Every
code step carries literal content; every run step carries a literal command and a stated expectation.
The three conditionals in the plan — Task 1 Step 3's two failure modes and Task 6 Step 1's newer tag
— each name a concrete check, a concrete expected output and a concrete action, which is stop and
report.

**Type consistency.** `EndpointMutationDetector.Install(IDistributedApplicationBuilder, ServiceResource, IResource?, string)`
is spelled identically in Task 2's Interfaces block, its definition, and both call sites; `real.Resource`
satisfies `IResource?` because `Bridge`'s `TResource` is constrained `class, IResourceWithServiceDiscovery`.
`Fingerprint`'s eleven field types match the pinned `EndpointAnnotation`: `Name`/`UriScheme`/
`TargetHost`/`Transport` are `string`, `Port`/`TargetPort` are `int?`, `IsExplicitlyProxied` is
`bool?`, `IsExternal`/`ExcludeReferenceEndpoint`/`TlsEnabled` are `bool`, and `Protocol` is
`System.Net.Sockets.ProtocolType`. `Label` is defined once in Task 2 and used unchanged in Tasks 3
and 4. `HoldsEndpointNamed(IResource, string)` is introduced and used only in Task 4.
`GuestLanguageEndpointCallbacks.EndpointCallback`/`HttpsEndpointCallback` keep the same parameter
order everywhere they are called, and the `(string Property, object? Value)` tuple shape is the same
in the harness and at every call site. The fixture helpers `Builder`/`Url`/`Kubernetes`/`Container`/
`Endpoints` are defined once in Task 1 and used unchanged in Tasks 2–5.
