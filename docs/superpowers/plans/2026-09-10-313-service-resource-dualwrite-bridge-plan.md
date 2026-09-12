# `AddService` returns `ServiceResource` — the dual-write bridge — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** `AddService()` returns `IResourceBuilder<ServiceResource>` for all four sources
(`container`, `kubernetes`, `local`, `url`), replacing `IResourceBuilder<IResourceWithServiceDiscovery>`,
via a dual-write bridge that never registers `ServiceResource` itself with DCP. `Configure<T>` and the
ten `WithService*`/`WaitForService*` shims are retired; `As<T>()` is renamed to `Unwrap<T>()`, not
retired — same capability, same throw-not-skip semantics (see Task 8).

**Architecture:** A new sealed `ServiceResource : Resource` implements exactly five capability
interfaces (`IResourceWithServiceDiscovery`, `IResourceWithEnvironment`, `IResourceWithArgs`,
`IResourceWithEndpoints`, `IResourceWithWaitSupport`). Each source builds its real resource exactly as
today, then wraps it via `ResolvedService.Bridge`, which constructs a new `ServiceResource` facade,
copies every annotation the real resource already carries onto the facade (by reference, not by
value), and returns an `IResourceBuilder<ServiceResource>` backed by `ServiceResourceBuilder` — a
hand-written `IResourceBuilder<ServiceResource>` whose `WithAnnotation` override writes every new
annotation to both the facade and the real resource's own collections. `"url"` has no real resource,
so `ResolvedService.BridgeUnregistered` wraps the facade alone.

**Tech Stack:** C# / .NET (net8.0/9.0/10.0 multi-target), Aspire.Hosting 13.5.2 (pinned floor), xUnit.

**Spec:** `docs/superpowers/specs/2026-09-10-313-service-resource-dualwrite-bridge-design.md`
(as amended by this plan — see "Deviations from the spec's literal text" below for two bugs this
plan's self-review found and fixed in the design before implementing it).

## Global Constraints

- Multi-target `net8.0`/`net9.0`/`net10.0` — every new/changed file must compile on all three; no
  target-specific APIs.
- Aspire.Hosting pinned floor `13.5.2` (`Directory.Build.props:74`) — every Aspire API used
  (`IResourceBuilder<T>.WithAnnotation`, `ResourceAnnotationMutationBehavior`, `WaitAnnotation`,
  `EndpointAnnotation`, `ResourceNotificationService.WaitForDependenciesAsync`, …) must exist there;
  the spec's own decompile citations already confirm this for everything this plan touches.
- `dotnet build -c Release --no-restore -warnaserror` is the real gate — a warning is a build
  failure. Every new file must be warning-clean, including nullability and XML-doc-comment
  completeness where the codebase's existing convention requires it (see any touched file's existing
  doc-comment density as the bar).
- `[AspireExport]`-carrying methods must stay non-generic and must not collide on generated capability
  id (`CatalogExportsTests.NoTwoExportedMethodsInTheAssembly_ShareAGeneratedCapabilityId` guards this
  already — Task 10 updates its companion `ExportedIds_MatchTheKnownSurface` list).
- `CHANGELOG.md`'s `[Unreleased]` section is the release-note target; this ticket's `AddService`
  return-type change is a **Breaking** entry per the repo's own pre-1.0 convention (Task 11).
- Every new/changed `internal` type stays `internal`; only `ServiceResource` itself is `public` (it is
  the new export surface). `ServiceResourceBuilder` and `Reachability` are `internal`.

---

## Deviations from the spec's literal text (found and fixed during this plan's self-review)

The spec is otherwise sound and this plan implements it as written, **except** for three bugs found
while turning its `WithAnnotation` code sample, its `Bridge` description, and (after the human's
superseding rename decision) its `Unwrap<T>()` treatment into a task an engineer can execute without
re-discovering them:

1. **The spec's `WithAnnotation` sample gates the reachability check on `real is not null`, which
   silently breaks the `"url"` skip-with-warning behaviour.** `if (real is not null &&
   Reachability.IsUnreachable(...))` skips the whole branch — warning included — whenever `real` is
   `null`, which is exactly the `"url"` case. That means every annotation an AppHost author adds to a
   `"url"`-sourced service would be applied to the facade **silently, with no warning at all** —
   contradicting acceptance-checklist item 8 ("Configure's skip-with-warning behavior … preserved")
   and contradicting the spec's own §2.3 prose two paragraphs later ("writing it to the facade alone
   and warning is correct, not a gap"). The fix (Task 2) drops the `real is not null &&` guard:
   `Reachability.IsUnreachable(typeof(TAnnotation), source)` alone already returns `true` for every
   annotation type when `source == "url"`, regardless of whether `real` is populated, so the check
   needs no `real`-nullness gate at all. `real?.WithAnnotation(...)` stays defensively null-conditional
   below it, but in practice is only ever reached with `real` non-null once the skip-check is fixed
   (since `"url"` is always unreachable and every other source always has a `real`).
2. **`ResolvedService.Bridge` must copy every annotation `real` already carries onto the new facade,
   not just the shared `ServiceSourceAnnotation` the spec's §2.2 explicitly names.** A container or
   kubernetes source calls `.WithEndpoint(...)` (and, for kubernetes, `.WithArgs(...)`) on the real
   builder **before** wrapping it; a `"local"` `dotnet` service's endpoints and environment come from
   `AddProject`'s own launch-profile processing, which also runs before the facade exists. If `Bridge`
   only added `ServiceSourceAnnotation` to both objects (as the spec's §2.2 numbered list literally
   says), the facade would carry **no endpoint annotations at all** for every source except a second,
   later `WithHttpEndpoint()` call — breaking `GetServiceEndpoint()`/`GetEndpoint()` for every existing
   service, and breaking the "same instance, not a copy" invariant §3's own residual-risk table calls
   load-bearing for `WithEndpoint`'s update-in-place branch (a second `WithHttpEndpoint()` call would
   find no existing annotation on the facade and add a **second, divergent** one instead of mutating
   the original in place). The fix (Task 3): `Bridge` copies every annotation reference from
   `real.Resource.Annotations` into `facade.Annotations` before adding the shared
   `ServiceSourceAnnotation`. This is the same category of gap as the `ServiceSourceAnnotation`
   dual-write fix the spec's own self-review already found (§2.2's worked example) — one more instance
   of "the facade needs what `real` already had, not only what it gets next."

Both are called out again at their task below with the corrected code, and Task 3's tests assert the
fix directly (`GetServiceEndpoint` finding a container's endpoint through the facade; a `"url"`
service still warning on every `WithEnvironment` call).

A third bug, found while working out how the human's superseding rename decision (see the amended
spec's Open Question 1) actually has to be implemented, not from the spec's own code:

3. **`Unwrap<T>()` (the rename of today's `As<T>()`) cannot keep checking `service.Resource is T` —
   `service.Resource` is always the `ServiceResource` facade under this design, never the real,
   source-specific object.** Before this ticket, `AddService`'s caller held a builder over the real
   resource directly, so `As<T>()`'s body (`if (service.Resource is T typed) return
   service.ApplicationBuilder.CreateResourceBuilder(typed);`) worked because `service.Resource` *was*
   e.g. the `ContainerResource`/`JavaScriptAppResource` in question. After Tasks 1–7, every source
   returns a facade wrapping the real object (§2); `service.Resource` is unconditionally the
   `ServiceResource` facade, which is sealed and implements only the five capability interfaces —
   `service.Resource is ContainerResource` is now `false` for every source, always. Left as a pure
   rename, `Unwrap<T>()` would throw for every call, including the `container`/`local` cases the
   human decision's own justification for keeping this capability depends on — silently defeating the
   entire point of renaming rather than retiring it. The fix (Task 2 gains one property; Task 8's
   `Unwrap<T>()` is reimplemented, not just renamed): `ServiceResourceBuilder` exposes its private
   `real` field as `internal IResourceBuilder<IResource>? Real => real;`, and `Unwrap<T>()` checks
   `wrapper.Real?.Resource is T` after casting `service` to `Sources.ServiceResourceBuilder` (same
   assembly, so the `internal` member is reachable) — reaching through the wrapper to the object it
   dual-writes to, rather than the facade the wrapper itself exposes as `.Resource`. `"url"`'s `real`
   is always `null`, but that path is unaffected: `IsUnreachable<T>` is already unconditionally `true`
   for `"url"`, so the existing throw fires before this check is ever reached, exactly as it does
   today.

There is a fourth, smaller finding not from the spec's code but from reading `UrlConsumerWaitTests.cs`
directly: it has an existing test, `UrlSourcedService_HasNoLifetime`, asserting
`Assert.IsAssignableFrom<IResourceWithoutLifetime>(inventory.Resource)` — and three other tests that
call `ResourceNotificationService.WaitForDependenciesAsync` **without** first publishing
`BeforeStartEvent`. Since `ServiceResource` deliberately does not implement
`IResourceWithoutLifetime` (spec §1 — doing so unconditionally would suppress `WaitFor` on
`container`/`kubernetes`/`local` too, since they now share the same facade type), those three tests
would hang until their own cancellation budget fires under the new design, because nothing strips
their `WaitAnnotation` before the wait is evaluated. In a real `aspire run` this is fine —
`DropWaitsOnUrlServices` (subscribed to `BeforeStartEvent`, which always fires before DCP starts
resources) already strips the annotation — but these specific unit tests bypass that pipeline on
purpose, to isolate the wait mechanism. Task 5 fixes this by publishing `BeforeStartEvent` first,
matching the ordering a real run always has.

---

## Task 1: `ServiceResource` — the class itself

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/ServiceResource.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/ServiceResourceTests.cs`

**Interfaces:**
- Produces: `public sealed class ServiceResource(string name) : Resource(name),
  IResourceWithServiceDiscovery, IResourceWithEnvironment, IResourceWithArgs,
  IResourceWithEndpoints, IResourceWithWaitSupport` — every later task constructs this type directly
  (`new ServiceResource(serviceName)`).

- [ ] **Step 1: Write the failing test**

```csharp
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Pins the exact shape PR #319 measured as "shape D" — the one the TypeScript codegen wall does
/// not apply to, and the one the ticket's acceptance item 3 names. A regression here (an interface
/// added or dropped) changes generated TypeScript without any test elsewhere catching it, since
/// nothing else asserts ServiceResource's own interface list.
/// </summary>
public class ServiceResourceTests
{
    [Fact]
    public void ServiceResource_DeclaresExactlyTheFiveCapabilityInterfaces()
    {
        var type = typeof(ServiceResource);

        Assert.True(type.IsSealed);
        Assert.True(typeof(Resource).IsAssignableFrom(type));

        var interfaces = type.GetInterfaces();
        Assert.Contains(typeof(IResourceWithServiceDiscovery), interfaces);
        Assert.Contains(typeof(IResourceWithEnvironment), interfaces);
        Assert.Contains(typeof(IResourceWithArgs), interfaces);
        Assert.Contains(typeof(IResourceWithEndpoints), interfaces);
        Assert.Contains(typeof(IResourceWithWaitSupport), interfaces);

        // Deliberately excluded (spec §1): declaring these unconditionally on a type every source
        // shares would suppress WaitFor/DCP-execution-model behaviour for sources that need it.
        Assert.DoesNotContain(typeof(IResourceWithoutLifetime), interfaces);
        Assert.DoesNotContain(typeof(IComputeResource), interfaces);
        Assert.DoesNotContain(typeof(IResourceWithProbes), interfaces);
    }

    [Fact]
    public void ServiceResource_ConstructsWithTheGivenName()
    {
        var resource = new ServiceResource("orders");

        Assert.Equal("orders", resource.Name);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceResourceTests"`
Expected: FAIL to compile — `ServiceResource` does not exist yet.

- [ ] **Step 3: Write the class**

```csharp
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// What <c>AddService()</c> returns for every source. Never the object DCP registers and starts —
/// see <c>Sources.ResolvedService.Bridge</c> — so its own declared shape stays the same across
/// <c>"container"</c>, <c>"kubernetes"</c>, <c>"local"</c> and <c>"url"</c>, while each dual-writes
/// configuration to the real, source-specific resource behind it.
/// </summary>
/// <remarks>
/// Deliberately does not implement:
/// <list type="bullet">
///   <item><description>
///   <see cref="IResourceWithoutLifetime"/> — giving every instance this marker unconditionally
///   would also suppress <c>WaitFor</c> on <c>container</c>/<c>kubernetes</c>/<c>local</c>-sourced
///   services, which must keep working. The <c>"url"</c> source's "no lifetime" behaviour is
///   preserved a different way — <c>Sources.UrlSource.DropWaitsOnUrlServices</c> strips a consumer's
///   <see cref="WaitAnnotation"/> at <c>BeforeStartEvent</c>, before Aspire's own wait machinery ever
///   evaluates one.
///   </description></item>
///   <item><description>
///   Any container-only vocabulary (<c>WithImage</c>, <c>WithBindMount</c>, <c>WithVolume</c>,
///   <c>WithDockerfile</c>, <c>WithLifetime</c>, …), <see cref="IComputeResource"/> and
///   <see cref="IResourceWithProbes"/> — DCP-execution-model concerns tied to being the literal
///   registered resource, which this type never is.
///   </description></item>
/// </list>
/// See docs/superpowers/specs/2026-09-10-313-service-resource-dualwrite-bridge-design.md §1.
/// </remarks>
public sealed class ServiceResource(string name) : Resource(name),
    IResourceWithServiceDiscovery,
    IResourceWithEnvironment,
    IResourceWithArgs,
    IResourceWithEndpoints,
    IResourceWithWaitSupport;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceResourceTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceResource.cs test/Aspire.Hosting.ServiceSources.Tests/ServiceResourceTests.cs
git commit -m "Add the ServiceResource facade class (#313)"
```

---

## Task 2: `Reachability` and `ServiceResourceBuilder` — the dual-write wrapper

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs` (contains both
  `Reachability` and `ServiceResourceBuilder`)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/ServiceResourceBuilderTests.cs`

**Interfaces:**
- Consumes: `ServiceResource` (Task 1).
- Produces: `internal static class Reachability` with `IsUnreachable(Type, string)` and
  `CapabilityLabel(Type)`; `internal sealed class ServiceResourceBuilder(IDistributedApplicationBuilder
  applicationBuilder, ServiceResource facade, IResourceBuilder<IResource>? real, string source) :
  IResourceBuilder<ServiceResource>`, including an internal `Real` property exposing `real` — Task
  3's `ResolvedService.Bridge`/`BridgeUnregistered` construct this directly, and Task 8's
  `Unwrap<T>()` reads `Real` to reach the underlying resource behind the facade.

- [ ] **Step 1: Write the failing tests**

```csharp
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class ServiceResourceBuilderTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    private static (ServiceResource Facade, IResourceBuilder<ServiceResources.ServiceContainerResource> Real,
        ServiceResourceBuilder Wrapper) ContainerCase(IDistributedApplicationBuilder builder)
    {
        var facade = new ServiceResource("orders");
        var real = builder.AddResource(new ServiceSources.ServiceContainerResource("orders")).WithImage("nginx");
        var wrapper = new ServiceSources.ServiceResourceBuilder(builder, facade, real, "container");
        return (facade, real, wrapper);
    }

    [Fact]
    public void Real_ExposesTheRealBuilderPassedToTheConstructor()
    {
        var builder = Builder();
        var (_, real, wrapper) = ContainerCase(builder);

        // Task 8's Unwrap<T> depends on this — it is the only way to reach the real, source-specific
        // resource, since Resource above is always the facade.
        Assert.Same(real, wrapper.Real);
    }

    [Fact]
    public void WithAnnotation_Append_AddsTheSameInstanceToBothCollections()
    {
        var builder = Builder();
        var (facade, real, wrapper) = ContainerCase(builder);
        var annotation = new EnvironmentAnnotation("A", "B");

        wrapper.WithAnnotation(annotation);

        Assert.Same(annotation, Assert.Single(facade.Annotations.OfType<EnvironmentAnnotation>()));
        Assert.Same(annotation, Assert.Single(real.Resource.Annotations.OfType<EnvironmentAnnotation>()));
    }

    [Fact]
    public void WithAnnotation_Replace_RemovesTheFacadesExistingOneFirst()
    {
        var builder = Builder();
        var (facade, real, wrapper) = ContainerCase(builder);

        wrapper.WithAnnotation(new EnvironmentAnnotation("A", "1"));
        wrapper.WithAnnotation(new EnvironmentAnnotation("A", "2"), ResourceAnnotationMutationBehavior.Replace);

        Assert.Equal("2", Assert.Single(facade.Annotations.OfType<EnvironmentAnnotation>()).Value);
    }

    [Fact]
    public void WithAnnotation_UrlSource_SkipsEveryAnnotationAndWarns()
    {
        var builder = Builder();
        var facade = new ServiceResource("inventory");
        var wrapper = new ServiceSources.ServiceResourceBuilder(builder, facade, real: null, "url");

        wrapper.WithAnnotation(new EnvironmentAnnotation("A", "B"));

        Assert.Empty(facade.Annotations.OfType<EnvironmentAnnotation>());
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("inventory", message);
        Assert.Contains("'url'", message);
    }

    [Fact]
    public void WithAnnotation_KubernetesSource_SkipsEnvironmentButAppliesWait()
    {
        var builder = Builder();
        var facade = new ServiceResource("orders");
        var real = builder.AddResource(
                new ServiceSources.ServiceExecutableResource("orders", "kubectl", builder.AppHostDirectory))
            .WithArgs("port-forward");
        var wrapper = new ServiceSources.ServiceResourceBuilder(builder, facade, real, "kubernetes");

        wrapper.WithAnnotation(new EnvironmentAnnotation("A", "B"));
        Assert.Empty(facade.Annotations.OfType<EnvironmentAnnotation>());

        var wait = new WaitAnnotation(real.Resource, WaitType.WaitUntilHealthy);
        wrapper.WithAnnotation(wait);
        Assert.Same(wait, Assert.Single(facade.Annotations.OfType<WaitAnnotation>()));
        Assert.Same(wait, Assert.Single(real.Resource.Annotations.OfType<WaitAnnotation>()));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceResourceBuilderTests"`
Expected: FAIL to compile — `Reachability`/`ServiceResourceBuilder` do not exist yet.

- [ ] **Step 3: Write `Reachability` and `ServiceResourceBuilder`**

```csharp
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Re-keys today's <c>ServiceConfigurationExtensions.IsUnreachable&lt;T&gt;</c> table by the
/// concrete annotation type native vocabulary adds, instead of a capability type parameter —
/// <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/> has no single generic capability
/// parameter to key on any more, since every native method (<c>WithEnvironment</c>, <c>WaitFor</c>,
/// …) calls <c>WithAnnotation</c> directly rather than through one dispatcher.
/// </summary>
internal static class Reachability
{
    private static readonly HashSet<string> OutOfBandSources = new(StringComparer.Ordinal) { "url", "kubernetes" };

    /// <summary>
    /// Whether an annotation of <paramref name="annotationType"/> cannot reach the service behind
    /// <paramref name="source"/> — an exact re-expression of
    /// <c>ServiceConfigurationExtensions.IsUnreachable&lt;T&gt;</c> (design §5): everything is
    /// unreachable for <c>"url"</c>; only <see cref="WaitAnnotation"/> survives for
    /// <c>"kubernetes"</c>, whose real resource is a genuine <c>kubectl port-forward</c> process
    /// worth ordering against.
    /// </summary>
    public static bool IsUnreachable(Type annotationType, string source) =>
        OutOfBandSources.Contains(source)
        && !(string.Equals(source, "kubernetes", StringComparison.Ordinal) && annotationType == typeof(WaitAnnotation));

    /// <summary>
    /// The capability name a skip warning should show for <paramref name="annotationType"/> — the
    /// native method an AppHost author actually called, not the annotation's own (often
    /// callback-suffixed) type name.
    /// </summary>
    public static string CapabilityLabel(Type annotationType) => annotationType.Name switch
    {
        nameof(EnvironmentAnnotation) or nameof(EnvironmentCallbackAnnotation) => "WithEnvironment",
        nameof(CommandLineArgsCallbackAnnotation) => "WithArgs",
        nameof(EndpointAnnotation) => "WithHttpEndpoint/WithHttpsEndpoint",
        nameof(WaitAnnotation) => "WaitFor/WaitForCompletion",
        _ => annotationType.Name,
    };
}

/// <summary>
/// The <see cref="IResourceBuilder{T}"/> an AppHost author actually holds after <c>AddService()</c>.
/// Every native vocabulary method this design buys back (<c>WithEnvironment</c>,
/// <c>WithReference</c>, <c>WithArgs</c>, <c>WithHttpEndpoint</c>/<c>WithHttpsEndpoint</c>,
/// <c>WaitFor</c>, <c>WaitForCompletion</c>) is an Aspire extension method that — for a new
/// annotation — ends in <c>builder.WithAnnotation(...)</c>, so this one override point intercepts
/// all of them (design §2, §3).
/// </summary>
internal sealed class ServiceResourceBuilder(
    IDistributedApplicationBuilder applicationBuilder,
    ServiceResource facade,
    IResourceBuilder<IResource>? real,
    string source)
    : IResourceBuilder<ServiceResource>
{
    public IDistributedApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;

    public ServiceResource Resource { get; } = facade;

    // Exposed so ServiceConfigurationExtensions.Unwrap<T> (Task 8) can reach the real,
    // source-specific resource behind the facade — `Resource` above is always the facade itself,
    // never `real`, so Unwrap<T> cannot recover a kind-specific type through `Resource` alone.
    internal IResourceBuilder<IResource>? Real => real;

    public IResourceBuilder<ServiceResource> WithAnnotation<TAnnotation>(
        TAnnotation annotation, ResourceAnnotationMutationBehavior behavior = ResourceAnnotationMutationBehavior.Append)
        where TAnnotation : IResourceAnnotation
    {
        // Unconditional on `real` being non-null: IsUnreachable(_, "url") is already true for every
        // annotation type regardless of whether a real resource exists, so gating this on
        // `real is not null` (as an earlier draft of this design did) would silently apply every
        // annotation to the facade alone for "url", with no warning at all — the opposite of
        // Configure<T>'s preserved skip-with-warning behaviour.
        if (Reachability.IsUnreachable(typeof(TAnnotation), source))
        {
            ServiceSourcesWarnings.For(ApplicationBuilder)
                .AddSkip(Resource.Name, source, Reachability.CapabilityLabel(typeof(TAnnotation)));
            return this;
        }

        // Mirrors Aspire's own DistributedApplicationResourceBuilder<T>.WithAnnotation exactly:
        // Replace removes any existing TAnnotation before adding, Append does not.
        if (behavior == ResourceAnnotationMutationBehavior.Replace
            && Resource.Annotations.OfType<TAnnotation>().SingleOrDefault() is { } existing)
        {
            Resource.Annotations.Remove(existing);
        }

        // The SAME instance goes into both collections — never a copy. This is what keeps a
        // second WithHttpEndpoint() call's mutation-in-place visible through both objects, and what
        // makes an EndpointAnnotation's AllocatedEndpoint — set by DCP against the real, registered
        // object during an actual run — readable through the facade afterwards.
        Resource.Annotations.Add(annotation);

        // real.WithAnnotation (not a direct Annotations.Add) so the real resource's own
        // Replace-or-Append bookkeeping runs too, against its own possible pre-existing TAnnotation.
        real?.WithAnnotation(annotation, behavior);

        return this;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceResourceBuilderTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/ServiceResourceBuilder.cs test/Aspire.Hosting.ServiceSources.Tests/Sources/ServiceResourceBuilderTests.cs
git commit -m "Add Reachability and the ServiceResourceBuilder dual-write wrapper (#313)"
```

---

## Task 3: `ResolvedService.Bridge`/`BridgeUnregistered`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs` (replaces `Tag` entirely)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/ResolvedServiceTests.cs` (new)

**Interfaces:**
- Consumes: `ServiceResource` (Task 1), `ServiceResourceBuilder` (Task 2).
- Produces: `internal static IResourceBuilder<ServiceResource> Bridge<TResource>(IResourceBuilder<TResource>
  real, string serviceName, string source) where TResource : class, IResourceWithServiceDiscovery` and
  `internal static IResourceBuilder<ServiceResource> BridgeUnregistered(IDistributedApplicationBuilder
  builder, ServiceResource facade, string serviceName, string source)` — Task 4 (container/kubernetes),
  Task 5 (url), and Task 6 (local, including deferred) each call one of these instead of `Tag`.

- [ ] **Step 1: Write the failing tests**

```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class ResolvedServiceTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    [Fact]
    public void Bridge_ReturnsAFacadeDistinctFromTheRealResource()
    {
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");

        var bridged = ResolvedService.Bridge(real, "orders", "container");

        Assert.IsType<ServiceResource>(bridged.Resource);
        Assert.NotSame(real.Resource, bridged.Resource);
        Assert.Equal("orders", bridged.Resource.Name);
    }

    [Fact]
    public void Bridge_CopiesAnnotationsAlreadyOnTheRealResourceOntoTheFacade()
    {
        // The gap this plan's self-review found: an endpoint added before Bridge is called (exactly
        // what ContainerSource/KubernetesSource/AddProject do) must still be visible through the
        // facade's own Annotations, or GetServiceEndpoint()/GetEndpoint() find nothing.
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders"))
            .WithImage("nginx")
            .WithEndpoint(targetPort: 8080, scheme: "http", name: "http");
        var existingEndpoint = Assert.Single(real.Resource.Annotations.OfType<EndpointAnnotation>());

        var bridged = ResolvedService.Bridge(real, "orders", "container");

        Assert.Same(existingEndpoint, Assert.Single(bridged.Resource.Annotations.OfType<EndpointAnnotation>()));
    }

    [Fact]
    public void Bridge_AddsTheSameServiceSourceAnnotationInstanceToBothCollections()
    {
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");

        var bridged = ResolvedService.Bridge(real, "orders", "container");

        var onFacade = Assert.Single(bridged.Resource.Annotations.OfType<ServiceSourceAnnotation>());
        var onReal = Assert.Single(real.Resource.Annotations.OfType<ServiceSourceAnnotation>());
        Assert.Same(onFacade, onReal);
        Assert.Equal("orders", onFacade.ServiceName);
        Assert.Equal("container", onFacade.Source);
    }

    [Fact]
    public void Bridge_NewAnnotationThroughTheReturnedBuilder_DualWrites()
    {
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");

        var bridged = ResolvedService.Bridge(real, "orders", "container")
            .WithAnnotation(new EnvironmentAnnotation("A", "B"));

        Assert.NotEmpty(bridged.Resource.Annotations.OfType<EnvironmentAnnotation>());
        Assert.NotEmpty(real.Resource.Annotations.OfType<EnvironmentAnnotation>());
    }

    [Fact]
    public void BridgeUnregistered_TagsOnlyTheFacade_NoRealResourceInvolved()
    {
        var builder = Builder();
        var facade = new ServiceResource("inventory");

        var bridged = ResolvedService.BridgeUnregistered(builder, facade, "inventory", "url");

        var annotation = Assert.Single(bridged.Resource.Annotations.OfType<ServiceSourceAnnotation>());
        Assert.Equal("inventory", annotation.ServiceName);
        Assert.Equal("url", annotation.Source);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ResolvedServiceTests"`
Expected: FAIL to compile — `Bridge`/`BridgeUnregistered` do not exist yet (`Tag` is still the only
member).

- [ ] **Step 3: Rewrite `ResolvedService.cs`**

```csharp
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Sources;

internal static class ResolvedService
{
    /// <summary>
    /// Wraps <paramref name="real"/> — the resource Aspire actually runs — behind a new
    /// <see cref="ServiceResource"/> facade that dual-writes future configuration to both, and
    /// returns a builder over the facade. Used by every source except <c>"url"</c>, which has no
    /// real resource to wrap — see <see cref="BridgeUnregistered"/>.
    /// </summary>
    public static IResourceBuilder<ServiceResource> Bridge<TResource>(
        IResourceBuilder<TResource> real, string serviceName, string source)
        // `class` is what lets IResourceBuilder<T>'s covariance pass `real` through unchanged below.
        where TResource : class, IResourceWithServiceDiscovery
    {
        var facade = new ServiceResource(serviceName);

        // Copies the SAME instances `real` already carries — a container/kubernetes source's own
        // EndpointAnnotation, a "local" project's launch-profile-derived endpoints and environment,
        // whatever a deferred registration added — so GetServiceEndpoint/GetEndpoint and a second
        // WithHttpEndpoint() call see them on the facade exactly as they sat on `real` before the
        // facade existed. Sharing the instance rather than its value is what keeps a later
        // mutation-in-place (WithEndpoint's update branch) visible on both collections.
        foreach (var annotation in real.Resource.Annotations)
        {
            facade.Annotations.Add(annotation);
        }

        var sourceAnnotation = new ServiceSourceAnnotation(serviceName, source);
        facade.Annotations.Add(sourceAnnotation);
        real.WithAnnotation(sourceAnnotation);

        // Subscribed from here, exactly as Tag did: this is the set the report is about — the
        // resources a source produced, and no others.
        ServiceStartupFailureNotices.For(real.ApplicationBuilder);

        return new ServiceResourceBuilder(real.ApplicationBuilder, facade, real, source);
    }

    /// <summary>
    /// Tags <paramref name="facade"/> alone — no real resource exists to dual-write to. The
    /// <c>"url"</c> source's only case: <see cref="ServiceResourceBuilder"/>'s <c>real</c> is
    /// <see langword="null"/>, so every future annotation is unreachable and skipped with a warning
    /// (design §2.3, §5).
    /// </summary>
    public static IResourceBuilder<ServiceResource> BridgeUnregistered(
        IDistributedApplicationBuilder builder, ServiceResource facade, string serviceName, string source)
    {
        facade.Annotations.Add(new ServiceSourceAnnotation(serviceName, source));

        ServiceStartupFailureNotices.For(builder);

        return new ServiceResourceBuilder(builder, facade, real: null, source);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ResolvedServiceTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/ResolvedService.cs test/Aspire.Hosting.ServiceSources.Tests/Sources/ResolvedServiceTests.cs
git commit -m "Replace ResolvedService.Tag with the Bridge/BridgeUnregistered dual-write pair (#313)"
```

---

## Task 4: `IServiceSource.Resolve` returns `ServiceResource` — `container`/`kubernetes`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/IServiceSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ContainerSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/KubernetesSource.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/ServiceEndpointTests.cs` (helper return types only)

**Interfaces:**
- Consumes: `ResolvedService.Bridge` (Task 3).
- Produces: `IServiceSource.Resolve(...)` now returns `IResourceBuilder<ServiceResource>` — Task 6
  (`LocalProjectSource`, the other `IServiceSource` implementation) and Task 7 (`AddService`, the sole
  caller of `IServiceSource.Resolve` through the `Sources` dictionary) both depend on this.

- [ ] **Step 1: Confirm the existing tests that already cover this behaviour**

`test/Aspire.Hosting.ServiceSources.Tests/Sources/ContainerSourceTests.cs` and
`.../KubernetesSourceTests.cs` already call `.Resolve(...)` with an implicit `var`, and assert on
`service.Resource.Annotations.OfType<EndpointAnnotation>()` — this keeps compiling and keeps passing
once `Bridge` copies pre-existing annotations onto the facade (Task 3). No new test is needed for
`ContainerSource`/`KubernetesSource` themselves; run these two files as the regression check:

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ContainerSourceTests|FullyQualifiedName~KubernetesSourceTests"`
Expected (before this task's code changes, against the OLD `IServiceSource.Resolve` signature):
PASS — this run is the baseline the task must not regress. (`ServiceEndpointTests.ContainerService`/
`KubernetesService` in Step 4 below is the one place an explicit type annotation needs to change.)

- [ ] **Step 2: Change `IServiceSource.Resolve`'s return type**

In `src/Aspire.Hosting.ServiceSources/IServiceSource.cs`, change:

```csharp
    IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
```

to:

```csharp
    IResourceBuilder<ServiceResource> Resolve(
```

(Add `using Aspire.Hosting.ServiceSources;` if the file's namespace requires it — it is already in
`Aspire.Hosting.ServiceSources`, so no new `using` is needed.)

- [ ] **Step 3: Update `ContainerSource.Resolve` and `KubernetesSource.Resolve`**

In `ContainerSource.cs`, change the signature and the final line:

```csharp
    public IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null)
    {
        ...
        return ResolvedService.Bridge(containerBuilder, serviceName, "container");
    }
```

In `KubernetesSource.cs`, the same shape:

```csharp
    public IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null)
    {
        ...
        return ResolvedService.Bridge(executableBuilder, serviceName, "kubernetes");
    }
```

Everything else in both files (the body building `containerBuilder`/`executableBuilder`) is
unchanged — only the declared return type and the final `Tag` → `Bridge` call change.

- [ ] **Step 4: Update `ServiceEndpointTests.cs`'s two explicit-type helpers**

In `test/Aspire.Hosting.ServiceSources.Tests/ServiceEndpointTests.cs`, change:

```csharp
    private static IResourceBuilder<IResourceWithServiceDiscovery> ContainerService(
```

and

```csharp
    private static IResourceBuilder<IResourceWithServiceDiscovery> KubernetesService(
```

to `IResourceBuilder<ServiceResource>` in both places. No other line in this file changes — the
`.GetServiceEndpoint()`/`.GetEndpoint(...)` calls the tests make bind unchanged via
`IResourceBuilder<T>`'s covariance to `IResourceWithServiceDiscovery`.

- [ ] **Step 5: Run tests to verify everything still passes**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ContainerSourceTests|FullyQualifiedName~KubernetesSourceTests|FullyQualifiedName~ServiceEndpointTests"`
Expected: PASS — same test count as Step 1's baseline; this is the "annotation copy-forward from Task
3 actually works for container/kubernetes" proof.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/IServiceSource.cs src/Aspire.Hosting.ServiceSources/Sources/ContainerSource.cs src/Aspire.Hosting.ServiceSources/Sources/KubernetesSource.cs test/Aspire.Hosting.ServiceSources.Tests/ServiceEndpointTests.cs
git commit -m "IServiceSource.Resolve returns IResourceBuilder<ServiceResource> for container/kubernetes (#313)"
```

---

## Task 5: `UrlSource` — `ServiceResource` replaces `ServiceUrlResource`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ServiceResources.cs` (remove `ServiceUrlResource`)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/UrlSource.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Sources/UrlSourceTests.cs` (any explicit
  `ServiceUrlResource` reference — confirm via grep in Step 1)
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/UrlConsumerWaitTests.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/BackingServices/BackingServiceWaitTests.cs` — **not
  cosmetic**: this file has a real test, `UrlSourcedService_CarriesTheMarkerAndItsWaitResolves`, that
  asserts `Assert.IsAssignableFrom<IResourceWithoutLifetime>(inventory.Resource)` and then calls
  `WaitForDependenciesAsync` without publishing `BeforeStartEvent` first — the exact same pattern as
  the three `UrlConsumerWaitTests.cs` tests this task already fixes, found by reading this file in
  full rather than trusting its "stale doc-comment" appearance in a grep. See Step 6 below.

**Interfaces:**
- Consumes: `ResolvedService.BridgeUnregistered` (Task 3).
- Produces: `UrlSource.Resolve` now returns `IResourceBuilder<ServiceResource>`.

- [ ] **Step 1: Confirm nothing else references `ServiceUrlResource` before removing it**

Run: `grep -rn "ServiceUrlResource" src/ test/`
Expected: only `ServiceResources.cs` (the class itself), `UrlSource.cs`, and the doc-comment mention
in `BackingServiceWaitTests.cs`. If anything else appears, add it to this task's file list before
proceeding.

- [ ] **Step 2: Write/adjust the failing tests**

Two of `UrlConsumerWaitTests.cs`'s existing tests assert a mechanism this task removes. Replace:

```csharp
[Fact]
public void UrlSourcedService_HasNoLifetime()
{
    var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));
    var inventory = builder.AddService("inventory");
    Assert.IsAssignableFrom<IResourceWithoutLifetime>(inventory.Resource);
}

[Fact]
public void ContainerSourcedService_StillHasALifetime()
{
    var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("container"));
    var inventory = builder.AddService("inventory");
    Assert.IsNotAssignableFrom<IResourceWithoutLifetime>(inventory.Resource);
}
```

with:

```csharp
/// <summary>
/// ServiceResource is shared by every source, so it cannot carry IResourceWithoutLifetime
/// unconditionally — doing so would also suppress WaitFor on container/kubernetes/local services,
/// which must keep working. The "url" source's no-lifetime behaviour instead comes from
/// DropWaitsOnUrlServices stripping the WaitAnnotation at BeforeStartEvent, before Aspire's wait
/// machinery ever evaluates one — see the three tests below, all of which now publish
/// BeforeStartEvent before waiting.
/// </summary>
[Fact]
public void UrlSourcedService_DoesNotDeclareIResourceWithoutLifetime()
{
    var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

    var inventory = builder.AddService("inventory");

    Assert.IsNotAssignableFrom<IResourceWithoutLifetime>(inventory.Resource);
}
```

(`ContainerSourcedService_StillHasALifetime` is deleted outright — both sources now share the same
facade type, so there is no longer a type-level contrast for it to prove.)

For the three tests that call `WaitForDependenciesAsync` without first publishing
`BeforeStartEvent`, insert the publish immediately before building `notifications`:

```csharp
[Fact]
public async Task UrlSourcedService_WaitedOnByAConsumer_ResolvesRatherThanHanging()
{
    var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

    var inventory = builder.AddService("inventory");
    var worker = Consumer(builder, "worker").WaitFor(inventory);

    // BeforeStartEvent is what strips the WaitAnnotation now (DropWaitsOnUrlServices) — in a real
    // run it always fires before DCP starts anything and before WaitForDependenciesAsync is ever
    // called; this test publishes it explicitly to reproduce that ordering.
    await TestHelpers.PublishBeforeStartEventAsync(builder);

    var notifications = builder.Services.BuildServiceProvider()
        .GetRequiredService<ResourceNotificationService>();

    using var cts = new CancellationTokenSource(ResolvesWithin);

    await notifications.WaitForDependenciesAsync(worker.Resource, cts.Token);
}
```

Apply the identical one-line insertion (`await TestHelpers.PublishBeforeStartEventAsync(builder);`
before building `notifications`) to `UrlSourcedService_WaitedOnForCompletion_ResolvesRatherThanHanging`
and `UrlSourcedService_ReferencedByAConnectionString_ResolvesRatherThanHanging`.

- [ ] **Step 3: Run the changed tests to verify they fail against today's code**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~UrlConsumerWaitTests"`
Expected: `UrlSourcedService_DoesNotDeclareIResourceWithoutLifetime` FAILs today (the current
`ServiceUrlResource` **does** implement `IResourceWithoutLifetime`) — this is the expected red before
Step 4's implementation change. The three `BeforeStartEvent`-inserted tests still PASS today (the
insertion is a no-op against the still-present `IResourceWithoutLifetime` marker) — confirms the
insertion doesn't itself break anything ahead of the real change.

- [ ] **Step 4: Remove `ServiceUrlResource` from `ServiceResources.cs`**

Delete the `ServiceUrlResource` class and its doc comment entirely (lines documenting
`IResourceWithoutLifetime`'s role move to `ServiceResource.cs`'s own remarks, already written in
Task 1, and to `UrlSource.cs`'s `DropWaitsOnUrlServices` doc comment below). `ServiceContainerResource`
and `ServiceExecutableResource` are unaffected — leave them exactly as they are.

- [ ] **Step 5: Rewrite `UrlSource.Resolve` and its two annotation-matching helpers**

```csharp
    public IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null)
    {
        var uri = ResolveUrl(serviceName, definition, config);

        var facade = new ServiceResource(serviceName);
        var endpoint = new EndpointAnnotation(
            ProtocolType.Tcp, uriScheme: uri.Scheme, name: uri.Scheme, transport: "http", port: uri.Port, targetPort: uri.Port)
        {
            TargetHost = uri.Host,
            IsProxied = false,
        };
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(
            endpoint, uri.Host, uri.Port, EndpointBindingMode.SingleAddress, targetPortExpression: null);
        facade.Annotations.Add(endpoint);

        RegisterContainerConsumerCheck(builder);

        return ResolvedService.BridgeUnregistered(builder, facade, serviceName, "url");
    }
```

`DropWaitsOnUrlServices`'s match changes from a type check to a `ServiceSourceAnnotation` check, since
every source now shares the `ServiceResource` type:

```csharp
    private static void DropWaitsOnUrlServices(
        DistributedApplicationModel model, ServiceSourcesWarnings warnings)
    {
        foreach (var resource in model.Resources)
        {
            var waitsOnUrlServices = resource.Annotations
                .OfType<WaitAnnotation>()
                .Where(wait => IsUrlSourcedService(wait.Resource))
                .ToArray();
            ...
```

and `ConsumedUrlService` the same way:

```csharp
    private static ServiceResource? ConsumedUrlService(ContainerResource consumer)
    {
        foreach (var annotation in consumer.Annotations)
        {
            IResource? consumed = annotation switch
            {
                EndpointReferenceAnnotation endpointReference => endpointReference.Resource,
                ResourceRelationshipAnnotation relationship
                    when string.Equals(relationship.Type, ReferenceRelationship, StringComparison.Ordinal)
                    => relationship.Resource,
                _ => null,
            };

            if (consumed is ServiceResource candidate && IsUrlSourcedService(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="resource"/> is the "url"-sourced facade — the type check alone no
    /// longer distinguishes it, since every source now shares <see cref="ServiceResource"/>.
    /// </summary>
    private static bool IsUrlSourcedService(IResource resource) =>
        resource is ServiceResource
        && resource.Annotations.OfType<ServiceSourceAnnotation>().Any(a => a.Source == "url");
```

(Both call sites of `resource is ServiceUrlResource` — in `DropWaitsOnUrlServices`'s `.Where(...)`
and `ConsumedUrlService`'s pattern match — now go through the shared `IsUrlSourcedService` helper,
so the check is written once rather than twice.)

- [ ] **Step 6: Fix `BackingServiceWaitTests.cs`'s doc comment and its real test**

The class-level doc comment (line ~13) referencing `<see cref="Sources.ServiceUrlResource"/>, which
declares the marker deliberately (#170)` becomes prose that no longer claims `ServiceResource`
declares `IResourceWithoutLifetime` — e.g. "unlike a `container`/`local`-sourced service, whose real
resource keeps a lifetime" plus a note that the `"url"` contrast now comes from
`UrlSource.DropWaitsOnUrlServices` rather than a type marker.

`UrlSourcedService_CarriesTheMarkerAndItsWaitResolves` (its doc comment claims "A `"url"`-sourced
service does carry the marker") is rewritten the same way Task 5's `UrlConsumerWaitTests.cs` fixes
were: drop the `IsAssignableFrom<IResourceWithoutLifetime>` assertion, and publish `BeforeStartEvent`
before calling `WaitForDependenciesAsync`:

```csharp
/// <summary>
/// A "url"-sourced service's wait resolves with nothing running at all — not because the facade
/// carries IResourceWithoutLifetime (it doesn't; ServiceResource is shared by every source), but
/// because UrlSource.DropWaitsOnUrlServices strips the WaitAnnotation at BeforeStartEvent, before
/// Aspire's wait machinery ever evaluates one.
/// </summary>
[Fact]
public async Task UrlSourcedService_WaitResolvesOnceBeforeStartEventHasRun()
{
    var dir = TempDirectories.CreateSubdirectory().FullName;
    File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
        services:
          inventory:
            url:
              url: https://orders.example.com
        """);
    File.WriteAllText(
        Path.Combine(dir, "servicesources.local.json"),
        """{ "services": { "inventory": { "source": "url" } } }""");

    var builder = TestHelpers.CreateBuilderThatCanStart(dir);

    var inventory = builder.AddService("inventory");
    var worker = builder
        .AddExecutable("worker", "dotnet", TempDirectories.CreateSubdirectory().FullName)
        .WaitFor(inventory);

    Assert.IsNotAssignableFrom<IResourceWithoutLifetime>(inventory.Resource);

    await TestHelpers.PublishBeforeStartEventAsync(builder);

    var notifications = builder.Services.BuildServiceProvider()
        .GetRequiredService<ResourceNotificationService>();

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

    await notifications.WaitForDependenciesAsync(worker.Resource, cts.Token);
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~UrlConsumerWaitTests|FullyQualifiedName~UrlSourceTests|FullyQualifiedName~ContainerConsumerTests|FullyQualifiedName~BackingServiceWaitTests"`
Expected: PASS. (`ContainerConsumerTests` is included because `RegisterContainerConsumerCheck`'s
pre-flight is exercised there via a container consuming a url-sourced service.)

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/ServiceResources.cs src/Aspire.Hosting.ServiceSources/Sources/UrlSource.cs test/Aspire.Hosting.ServiceSources.Tests/UrlConsumerWaitTests.cs test/Aspire.Hosting.ServiceSources.Tests/BackingServices/BackingServiceWaitTests.cs
git commit -m "UrlSource builds a bare ServiceResource facade instead of ServiceUrlResource (#313)"
```

---

## Task 6: `LocalProjectSource` and `DeferredCheckout` — the `"local"` source (dotnet and kind)

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/DeferredCheckout.cs`

**Interfaces:**
- Consumes: `ResolvedService.Bridge` (Task 3), `IServiceSource.Resolve`'s new return type (Task 4).
- Produces: `LocalProjectSource.Resolve`, `DeferredCheckout.Register`, `DeferredCheckout.RegisterKind`
  all return `IResourceBuilder<ServiceResource>` (the latter nullable).

- [ ] **Step 1: Identify the regression check — existing tests already cover this path**

`LocalProjectSourceTests.cs`, `DeferredKindCheckoutTests.cs`, and the `Prepare/*.cs` tests already
exercise `LocalProjectSource.Resolve`/`DeferredCheckout.Register`/`RegisterKind` end-to-end and assert
on the returned builder's `.Resource`/`.Resource.Annotations`. Their `ILocalResourceKind.Resolve` fake
implementations keep returning `IResourceBuilder<IResourceWithServiceDiscovery>` unchanged (that
interface's signature is untouched by this ticket — only `IServiceSource.Resolve`, one level up,
changes). Run the full set as the baseline before touching code:

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LocalProjectSourceTests|FullyQualifiedName~DeferredKindCheckoutTests|FullyQualifiedName~PrepareDeferredTests|FullyQualifiedName~PrepareEagerPathTests"`
Expected: PASS (this is the "don't regress" baseline captured before Step 2's edits).

- [ ] **Step 2: Change `LocalProjectSource.Resolve`'s signature and both `Tag` call sites**

```csharp
    public IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null)
    {
        ...
        // dotnet-kind eager path (line ~223 today):
        return ResolvedService.Bridge(builder.AddProject(serviceName, projectPath), serviceName, "local");
        ...
    }
```

and `InvokeKindHandler` (the non-dotnet eager path, line ~414 today):

```csharp
    private static IResourceBuilder<ServiceResource> InvokeKindHandler(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition, string repoRoot,
        ILocalResourceKind handler)
    {
        IResourceBuilder<IResourceWithServiceDiscovery>? resourceBuilder;
        // ... body unchanged; resourceBuilder is still the kind's own return type ...

        return ResolvedService.Bridge(resourceBuilder, serviceName, "local");
    }
```

`resourceBuilder`'s own declared type stays `IResourceBuilder<IResourceWithServiceDiscovery>?` — that
is `ILocalResourceKind.Resolve`'s unaffected return type — only the method's own return type and its
final `Bridge` call change. `ResolvedService.Bridge<TResource>`'s `TResource` is inferred as
`IResourceWithServiceDiscovery` here, which satisfies its `where TResource : class,
IResourceWithServiceDiscovery` constraint.

The `registered` local variable in the middle of `Resolve` (the deferred-path branch) needs no
change: its type already comes from `deferred.Register(...)`/`deferred.RegisterKind(...)`, both
updated in Step 3 below to the same new return type, so the ternary and the `if (registered is not
null) return registered;` guard keep compiling unchanged.

- [ ] **Step 3: Change `DeferredCheckout.Register` and `RegisterKind`**

```csharp
    public IResourceBuilder<ServiceResource> Register(
        IDistributedApplicationBuilder builder,
        string serviceName,
        ServiceDefinition definition,
        ServiceDeveloperConfig config,
        RepositoryDeveloperConfig? repositoryConfig,
        LocalCheckoutPrefetch prefetch,
        IGitClient gitClient,
        PrepareStep? prepareStep,
        IPrepareCommandRunner prepareRunner)
    {
        ...
        return ResolvedService.Bridge(resourceBuilder, serviceName, "local");
    }
```

```csharp
    public IResourceBuilder<ServiceResource>? RegisterKind(
        IDistributedApplicationBuilder builder,
        string serviceName,
        ServiceDefinition definition,
        ServiceDeveloperConfig config,
        RepositoryDeveloperConfig? repositoryConfig,
        LocalCheckoutPrefetch prefetch,
        IGitClient gitClient,
        PrepareStep? prepareStep,
        IPrepareCommandRunner prepareRunner,
        Func<string, DeferredLocalResource?> resolveDeferred)
    {
        ...
        return ResolvedService.Bridge(registration.Service, serviceName, "local");
    }
```

Only the two methods' declared return types and their final `Tag` → `Bridge` calls change; every
line in between (repo-root resolution, `ProjectResource`/`DeferredProjectMetadata` construction,
withheld-resource bookkeeping) is untouched. `DeferredLocalResource.Service`'s own type
(`IResourceBuilder<IResourceWithServiceDiscovery>`) is unaffected — it is the kind's own return, one
level below where this ticket's change applies.

- [ ] **Step 4: Run tests to verify they still pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LocalProjectSourceTests|FullyQualifiedName~DeferredKindCheckoutTests|FullyQualifiedName~PrepareDeferredTests|FullyQualifiedName~PrepareEagerPathTests|FullyQualifiedName~ResolvedServiceTests"`
Expected: PASS — same test count as Step 1's baseline; this is the "annotation copy-forward from Task
3 works for AddProject's own launch-profile-derived annotations too" proof (a `"local"` `dotnet`
service's endpoints come from `AddProject`, entirely before `Bridge` is ever called).

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs src/Aspire.Hosting.ServiceSources/Sources/DeferredCheckout.cs
git commit -m "LocalProjectSource and DeferredCheckout return IResourceBuilder<ServiceResource> (#313)"
```

---

## Task 7: `AddService` returns `IResourceBuilder<ServiceResource>`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`

**Interfaces:**
- Consumes: `IServiceSource.Resolve`'s new return type (Tasks 4 and 6 — every entry in the `Sources`
  dictionary now returns `IResourceBuilder<ServiceResource>`).
- Produces: `[AspireExport] public static IResourceBuilder<ServiceResource> AddService(this
  IDistributedApplicationBuilder builder, [ResourceName] string name)` — every later task (8, 9, 10)
  and every existing call site across the repo depend on this.

- [ ] **Step 1: Identify the regression check**

This is a pure return-type change with no new logic — `AddService`'s body (`return
source.Resolve(...)`) is unchanged, since `source.Resolve` already returns the new type after Tasks 4
and 6. The check is that the whole solution still builds and the widest-reaching existing test file
still passes:

Run: `dotnet build -c Release --no-restore -warnaserror`
Expected (before this task's one-line change): still fails to build, because `AddService`'s declared
return type (`IResourceBuilder<IResourceWithServiceDiscovery>`) no longer matches what
`source.Resolve(...)` returns (`IResourceBuilder<ServiceResource>`) — confirming this task is the one
that closes that gap. (`IResourceBuilder<ServiceResource>` does not implicitly convert to
`IResourceBuilder<IResourceWithServiceDiscovery>` merely by being assigned to a differently-declared
return type without the method itself changing its signature — the compiler error names
`ServiceSourcesBuilderExtensions.cs`'s `AddService` method.)

- [ ] **Step 2: Change `AddService`'s signature and doc comment**

```csharp
    [AspireExport]
    public static IResourceBuilder<ServiceResource> AddService(
        this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        ...
        return source.Resolve(builder, name, definition, developerConfig, repositoryConfig);
    }
```

The method body is unchanged. Its doc comment (`<returns>`/`<remarks>`) currently explains why the
bare `IResourceBuilder<IResourceWithServiceDiscovery>` return type is "load bearing" for TypeScript
codegen — that reasoning is now wrong (PR #319 settled that a concrete class does not hit the codegen
wall) and must be replaced, not merely left stale:

```csharp
    /// <returns>
    /// An <see cref="IResourceBuilder{T}"/> over a <see cref="ServiceResource"/> facade — never the
    /// resource DCP actually runs, which is a <see cref="ProjectResource"/> for <c>"local"</c>, a
    /// container or executable resource for <c>"container"</c> and <c>"kubernetes"</c>, or whatever
    /// an <see cref="ILocalResourceKind"/> returns. Configuration applied through the returned
    /// builder — <c>WithEnvironment</c>, <c>WithReference</c>, <c>WithArgs</c>,
    /// <c>WithHttpEndpoint</c>/<c>WithHttpsEndpoint</c>, <c>WaitFor</c>/<c>WaitForCompletion</c> —
    /// dual-writes to the real resource behind the facade; see
    /// docs/superpowers/specs/2026-09-10-313-service-resource-dualwrite-bridge-design.md.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Which configuration applies depends on the resolved source: <c>"url"</c> and
    /// <c>"kubernetes"</c> run out of band — one is a fixed remote URL, the other a
    /// <c>kubectl port-forward</c> in front of something already running — so most configuration is
    /// skipped with a warning rather than applied. Wait ordering survives for <c>"kubernetes"</c>,
    /// whose port-forward is a real local process to order against; <c>"url"</c> registers no
    /// resource at all, so nothing applies to it.
    /// </para>
    /// </remarks>
```

(Delete the paragraph beginning "The bare `IResourceBuilder<IResourceWithServiceDiscovery>` return
type is load bearing…" entirely — it describes a constraint this design no longer has.)

- [ ] **Step 3: Build and run the full unit-test project**

Run: `dotnet build -c Release --no-restore -warnaserror`
Expected: PASS (this is the point at which the whole solution compiles again — every earlier task's
type changes now line up end to end).

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --no-build`
Expected: the suite now runs (rather than failing to build); some tests still fail — the ones Tasks 8
and 9 fix (anything still calling `Configure<T>`/the ten `WithService*` shims). Record which fail here
as the input to Tasks 8–10; do not treat this as a regression, since those call sites are deleted by
the tasks that follow, not preserved.

- [ ] **Step 4: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs
git commit -m "AddService returns IResourceBuilder<ServiceResource> (#313)"
```

---

## Task 8: Retire `Configure<T>`; rename `As<T>()` to `Unwrap<T>()`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceConfigurationExtensions.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/ServiceConfigurationExtensionsTests.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/BackingServices/BackingServiceConsumerTests.cs`
- Modify: `samples/DemoAppHost/Program.cs`
- Modify: `samples/DemoAppHostCodeCatalog/Program.cs`
- Modify: `README.md` — the only other place in this repo with live `.As<T>()` call sites (found by
  grepping the whole repo, `grep -rn '\.As<' --include='*.cs' --include='*.ts' --include='*.mts' .`;
  no TypeScript-side call site exists anywhere, confirming §6/§9 of the spec: ATS does not project a
  generic method — `Configure<T>`/`As<T>`/`Unwrap<T>` all carry `[AspireExportIgnore]` — so this
  rename is purely C#-side, no guest-language sample or generated handle is affected). Step 5 below
  is scoped narrowly to the three literal `.As<T>()`/`As<T>()` mentions — a mechanical rename to
  `Unwrap<T>()`. **Not fixed here, and flagged as a separate, pre-existing gap this plan does not
  otherwise track anywhere:** those three mentions sit inside two much larger README sections
  (the "Configuring the resolved resource" walkthrough and its "From a guest-language AppHost"
  continuation) that document `Configure<T>` and the ten `WithService*`/`WaitForService*` shims in
  depth — both retired by this ticket (Tasks 8, 9) — and neither is updated by any task in this plan.
  Those sections need a real rewrite once Tasks 8–9 land; it is out of scope for this task's narrow
  rename and is called out again in this plan's self-review below rather than attempted here.
  (`CHANGELOG.md` also mentions `As<T>()`, in entries recording *past* releases — a historical record,
  deliberately left untouched; only Task 11's new `[Unreleased]` entry describes the current change,
  and it already needs the identical wording fix — see Task 11.)

**Interfaces:**
- Consumes: native vocabulary now available directly on `IResourceBuilder<ServiceResource>` via
  Aspire's own extension methods (`WithEnvironment`, `WithReference`, `WithArgs`, `WaitFor`,
  `WaitForCompletion`), routed through `ServiceResourceBuilder.WithAnnotation` (Task 2);
  `ServiceResourceBuilder.Real` (Task 2) so the renamed method can reach the real resource.
- Produces: `ServiceConfigurationExtensions` retains the capability under a new name, `Unwrap<T>()`
  — same receiver type (`IResourceBuilder<IResourceWithServiceDiscovery>`, still binds to
  `IResourceBuilder<ServiceResource>` via covariance) and the same throw-not-skip semantics as
  today's `As<T>()`, but reimplemented (not just renamed — see Step 1) to reach through the
  `ServiceResourceBuilder` wrapper to the real, source-specific resource, since `service.Resource` is
  always the `ServiceResource` facade now.

- [ ] **Step 1: Delete `Configure<T>`; rename `As<T>()` to `Unwrap<T>()` and fix it to reach the real
  resource through the facade, not `service.Resource`**

Remove the entire `Configure<T>` method (including its `<example>`/`<exception>` doc comment and its
`[AspireExportIgnore]` attribute). Rename `As<T>()` to `Unwrap<T>()` — same receiver type,
`[AspireExportIgnore]` reason, and throw-vs-skip semantics (the human decision superseding the
earlier resolution of Open Question 1 keeps the capability but moves it off Aspire's own `As*`
prefix, which means "reinterpret this resource for publish, same builder type" — never a downcast to
a different type — see the spec's amended "Open Questions" §1). Leave `OutOfBandSources`,
`IsUnreachable<T>`, and `Explain<T>` exactly as they are, only updating their doc comments'
cross-references from `<see cref="As{T}"/>` to `<see cref="Unwrap{T}"/>`.

**`Unwrap<T>()`'s body must change, not just its name** (the third deviation from the spec's literal
text, found in this plan's self-review — see "Deviations from the spec's literal text" above).
`As<T>()`'s old body checked `service.Resource is T typed` — correct only because, before this
ticket, `service.Resource` *was* the real, source-specific object (`ContainerResource`,
`ProjectResource`, a kind's own resource type). After Tasks 1–7, `service.Resource` is always the
`ServiceResource` facade (sealed, implementing only the five capability interfaces) —
`service.Resource is ContainerResource` is now unconditionally `false`, for every source, because the
facade is never a `ContainerResource`/`ProjectResource`/kind type. Left as a pure rename,
`Unwrap<T>()` would throw for every call, including the very `container`/`local` cases the human
decision's justification for keeping this capability depends on. The fix reaches through the wrapper
instead:

```csharp
public static IResourceBuilder<T> Unwrap<T>(this IResourceBuilder<IResourceWithServiceDiscovery> service)
    where T : IResource
{
    var annotation = service.Resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault();

    // Checked before the cast, not after — unchanged from As<T>(): a kubernetes-sourced service is a
    // real ExecutableResource wrapping `kubectl port-forward`, so it accepts configuration that
    // would silently reach kubectl, never the service behind it.
    if (annotation is not null && IsUnreachable<T>(annotation.Source))
    {
        throw new ServiceSourcesConfigurationException(Explain<T>(service.Resource, annotation));
    }

    // service.Resource is always the ServiceResource facade now, never the real, source-specific
    // object real.Resource is — so the cast goes through the ServiceResourceBuilder wrapper's own
    // Real property (Task 2), not through service.Resource itself.
    if (service is Sources.ServiceResourceBuilder wrapper && wrapper.Real?.Resource is T typed)
    {
        return service.ApplicationBuilder.CreateResourceBuilder(typed);
    }

    throw new ServiceSourcesConfigurationException(Explain<T>(service.Resource, annotation));
}
```

`"url"`'s `real` is always `null`, but that path never reaches the `wrapper.Real?.Resource is T`
check: `IsUnreachable<T>` is already unconditionally `true` for `"url"`, so the first throw fires
before it, unchanged from today's behaviour.

Update the class-level doc comment, which currently describes "Both methods," to describe only
`Unwrap<T>()`. Every other doc-comment cross-reference to `As<T>`/`As{T}` in this file (the class
summary, `IsUnreachable<T>`'s remarks, `OutOfBandSources`' summary) becomes `Unwrap<T>`/`Unwrap{T}`.

- [ ] **Step 2: Rewrite `ServiceConfigurationExtensionsTests.cs`**

Every `Configure<TCapability>(r => r.Method(...))` call becomes a direct `.Method(...)` call — the
same assertions, now exercising native vocabulary through `ServiceResourceBuilder` instead of through
the deleted dispatcher. Replace the file's `Configure`-based tests with:

```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Covers issue #53: the AppHost applying its own configuration to a resolved service, now through
/// native vocabulary on ServiceResource's own interfaces rather than Configure&lt;T&gt;. Each test
/// drives a real source so the resource under test is the one an AppHost would actually get.
/// </summary>
public class ServiceConfigurationExtensionsTests
{
    private static readonly ServiceDefinition ContainerDefinition = new ServiceMetadata
    {
        Container = new ContainerMetadata { Image = "nginxdemos/hello", Port = 8080 },
    }.ToDefinition("servicesources.yaml", "payments", TestHelpers.EmptyRepositories);

    private static readonly ServiceDefinition KubernetesDefinition = new ServiceMetadata
    {
        Kubernetes = new KubernetesMetadata { Service = "orders", Port = 8080 },
    }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static readonly ServiceDefinition UrlDefinition = new ServiceMetadata
    {
        Url = new UrlMetadata { Url = "https://orders.example.com" },
    }.ToDefinition("servicesources.yaml", "inventory", TestHelpers.EmptyRepositories);

    private sealed class FixedPortAllocator : IPortAllocator
    {
        public bool IsAvailable(int port) => true;
        public int AllocatePort() => 51234;
        public IReadOnlyList<int> AllocatePorts(int count) => throw new NotSupportedException();
    }

    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    private static IResourceBuilder<ServiceResource> AddContainerService(IDistributedApplicationBuilder builder) =>
        new ContainerSource().Resolve(builder, "payments", ContainerDefinition, new ServiceDeveloperConfig { Source = "container" });

    private static IResourceBuilder<ServiceResource> AddUrlService(IDistributedApplicationBuilder builder) =>
        new UrlSource().Resolve(builder, "inventory", UrlDefinition, new ServiceDeveloperConfig { Source = "url" });

    [Fact]
    public void WithEnvironment_AppliesToTheRealResource()
    {
        var builder = Builder();

        var service = AddContainerService(builder).WithEnvironment("DBUSERNAME", "postgres");

        Assert.NotEmpty(service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
    }

    [Fact]
    public void NativeCalls_ReturnTheSameBuilder_SoCapabilitiesCanBeChained()
    {
        var builder = Builder();
        var service = AddContainerService(builder);

        var returned = service.WithEnvironment("A", "B").WithArgs("--verbose");

        Assert.Same(service.Resource, returned.Resource);
    }

    [Fact]
    public void WaitFor_AppliesToTheRealResource()
    {
        var builder = Builder();
        var dependency = builder.AddResource(new ServiceContainerResource("redis")).WithImage("redis");

        var service = AddContainerService(builder).WaitFor(dependency);

        Assert.NotEmpty(service.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public void Unwrap_ReturnsATypedBuilderForTheUnderlyingResource()
    {
        var builder = Builder();

        var typed = AddContainerService(builder).Unwrap<ContainerResource>();

        Assert.Equal("payments", typed.Resource.Name);
    }

    [Fact]
    public void Unwrap_MismatchedType_ThrowsNamingTheService()
    {
        var builder = Builder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => AddContainerService(builder).Unwrap<ProjectResource>());

        Assert.Contains("payments", ex.Message);
        Assert.Contains("container", ex.Message);
    }

    [Fact]
    public void WithEnvironment_OnUrlSource_SkipsWithoutThrowing_SoSourceSwitchingKeepsWorking()
    {
        var builder = Builder();
        var callbackRan = false;

        var service = AddUrlService(builder).WithEnvironment("A", _ =>
        {
            callbackRan = true;
            return "B";
        });

        Assert.False(callbackRan);
        Assert.NotNull(service);
    }

    [Fact]
    public void WithEnvironment_OnUrlSource_ReportsTheSkip()
    {
        var builder = Builder();

        AddUrlService(builder).WithEnvironment("A", "B");

        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("inventory", message);
        Assert.Contains("'url'", message);
        Assert.Contains("servicesources.local.json", message);
    }

    [Fact]
    public void ManyCallsOnOneUrlService_ReportOneAggregatedSkip()
    {
        var builder = Builder();
        var service = AddUrlService(builder);

        for (var i = 0; i < 25; i++)
        {
            service.WithEnvironment("A", "B");
        }

        service.WaitFor(builder.AddResource(new ServiceContainerResource("migrations")).WithImage("migrate"));

        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("26 calls", message);
        Assert.Contains("WithEnvironment ×25", message);
        Assert.Contains("WaitFor/WaitForCompletion", message);
    }

    [Fact]
    public void TwoDifferentUrlServices_ReportSkipsSeparately()
    {
        var builder = Builder();

        AddUrlService(builder).WithEnvironment("A", "B");
        new UrlSource()
            .Resolve(builder, "billing", UrlDefinition, new ServiceDeveloperConfig { Source = "url" })
            .WithEnvironment("A", "B");

        Assert.Equal(2, ServiceSourcesWarnings.For(builder).Messages.Count);
    }

    [Fact]
    public void WithEnvironment_OnKubernetesSource_SkipsRatherThanConfiguringThePortForward()
    {
        var builder = Builder();
        var service = new KubernetesSource(new FixedPortAllocator()).Resolve(
            builder, "orders", KubernetesDefinition,
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev" } });

        service.WithEnvironment("A", "B");

        Assert.Empty(service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
        Assert.Contains("port-forward", Assert.Single(ServiceSourcesWarnings.For(builder).Messages));
    }

    [Fact]
    public void WaitFor_OnKubernetesSource_StillApplies_BecauseOrderingThePortForwardIsCorrect()
    {
        var builder = Builder();
        var migrations = builder.AddResource(new ServiceContainerResource("migrations")).WithImage("migrate");
        var service = new KubernetesSource(new FixedPortAllocator()).Resolve(
            builder, "orders", KubernetesDefinition,
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev" } });

        service.WaitForCompletion(migrations);

        Assert.NotEmpty(service.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void WaitFor_OnUrlSource_StillSkips_BecauseNothingIsRegisteredToOrder()
    {
        var builder = Builder();
        var migrations = builder.AddResource(new ServiceContainerResource("migrations")).WithImage("migrate");

        var service = AddUrlService(builder).WaitForCompletion(migrations);

        Assert.Empty(service.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public async Task SkippedConfiguration_IsLoggedAtStartup()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              inventory:
                url:
                  url: https://orders.example.com
            """);
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" } } }""");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);

        builder.AddService("inventory").WithEnvironment("A", "B");

        var ex = await Record.ExceptionAsync(() => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Null(ex);
        Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void Unwrap_OnUrlSource_StillThrows_BecauseItMustReturnABuilder()
    {
        var builder = Builder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => AddUrlService(builder).Unwrap<IResourceWithEnvironment>());

        Assert.Contains("inventory", ex.Message);
        Assert.Contains("'url'", ex.Message);
    }
}
```

- [ ] **Step 3: Update the two remaining `.Configure<IResourceWithEnvironment>` call sites**

In `test/Aspire.Hosting.ServiceSources.Tests/BackingServices/BackingServiceConsumerTests.cs`, all four
occurrences of

```csharp
orders.Configure<IResourceWithEnvironment>(service => service.WithReference(db));
```

become:

```csharp
orders.WithReference(db);
```

In `samples/DemoAppHost/Program.cs` and `samples/DemoAppHostCodeCatalog/Program.cs`, the one
occurrence in each of

```csharp
.Configure<IResourceWithEnvironment>(r => r.WithEnvironment("DEMO_INJECTED_BY_APPHOST", "true"));
```

becomes:

```csharp
.WithEnvironment("DEMO_INJECTED_BY_APPHOST", "true");
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceConfigurationExtensionsTests|FullyQualifiedName~BackingServiceConsumerTests"`
Expected: PASS.

Run: `dotnet build samples/DemoAppHost -c Release --no-restore -warnaserror` and the same for
`samples/DemoAppHostCodeCatalog`
Expected: PASS (both sample AppHosts compile with native vocabulary in place of `Configure<T>`).

- [ ] **Step 5: Rename the three `.As<T>()` mentions in `README.md`**

Mechanical only — do not touch the surrounding `Configure<T>`/shim prose (see this task's file-list
note above):

```csharp
// line ~1132: "reach it from the AppHost with `As<JavaAppExecutableResource>()`" → "... with
// `Unwrap<JavaAppExecutableResource>()`"
// line ~1138: "reachable from the AppHost with `As<JavaAppExecutableResource>()`" → "... with
// `Unwrap<JavaAppExecutableResource>()`"
```
```csharp
builder.AddService("catalog")
    .Unwrap<JavaAppExecutableResource>()          // was .As<JavaAppExecutableResource>()
    .WithMavenBuild()
    .WithJvmArgs(["-Xmx512m"])
    .WithOtelAgent("/path/to/opentelemetry-javaagent.jar");
```
and, further down:
```csharp
backend.Unwrap<JavaScriptAppResource>().WithRunScript("dev");   // was backend.As<...>()
```

Leave line ~1148's `Use Configure<T>(...) instead for anything that should survive...` and the whole
"Configure is skipped for..."/"As<T>() throws for those sources..." paragraphs (lines ~1749-1790)
untouched here — they document `Configure<T>`'s retired behaviour too and need the larger rewrite
this task's file-list note already flags as separate, out-of-scope work.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceConfigurationExtensions.cs test/Aspire.Hosting.ServiceSources.Tests/ServiceConfigurationExtensionsTests.cs test/Aspire.Hosting.ServiceSources.Tests/BackingServices/BackingServiceConsumerTests.cs samples/DemoAppHost/Program.cs samples/DemoAppHostCodeCatalog/Program.cs README.md
git commit -m "Retire Configure<T>; rename As<T> to Unwrap<T> (#313)"
```

---

## Task 9: Retire the ten `WithService*`/`WaitForService*` shims

**Files:**
- Delete: `src/Aspire.Hosting.ServiceSources/ServiceConfigurationExports.cs`
- Delete: `test/Aspire.Hosting.ServiceSources.Tests/ServiceConfigurationExportsTests.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/UrlConsumerWaitTests.cs` (three
  `.Configure<IResourceWithEnvironment>` call sites remaining — see Step 2)
- Modify: `samples/DemoAppHostTypeScript/apphost.mts`
- Modify: `samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts`

**Interfaces:**
- Consumes: Aspire's own `[AspireExport]`-carrying extension methods (`withEnvironment`,
  `withReference`, `withArgs`, `waitFor`, `waitForCompletion`, `withHttpEndpoint`,
  `withHttpsEndpoint`) already project onto any handle whose declared shape includes the matching
  capability interface — verified for `ServiceResource`'s shape by PR #319 (spec §4, §6).

- [ ] **Step 1: Delete the shim file and its test file**

```bash
git rm src/Aspire.Hosting.ServiceSources/ServiceConfigurationExports.cs test/Aspire.Hosting.ServiceSources.Tests/ServiceConfigurationExportsTests.cs
```

- [ ] **Step 2: Fix the three remaining `.Configure<IResourceWithEnvironment>` call sites in `UrlConsumerWaitTests.cs`**

These three (lines 309, 340, 383 in the pre-plan file) are unrelated to the ten shims but were missed
by Task 8's file list because they live in a file this task also touches for a different reason
(§Task 5 already updated this file's `IResourceWithoutLifetime`/`BeforeStartEvent` tests — this step
is the same file's remaining `Configure<T>` cleanup, folded in here since `Configure<T>` no longer
exists after Task 8):

```csharp
.Configure<IResourceWithEnvironment>(r => r.WithEnvironment("A", "B"));
```

becomes:

```csharp
.WithEnvironment("A", "B");
```

at all three call sites (two on a returned `IResourceBuilder<ServiceResource>` directly, one chained
onto `builder.AddService("orders")`).

- [ ] **Step 3: Update the two TypeScript sample AppHosts**

In `samples/DemoAppHostTypeScript/apphost.mts`:

```typescript
.withServiceEnvironment('DEMO_INJECTED_BY_APPHOST', 'true')
.withServiceReference(inventory)
```
and, separately,
```typescript
.withServiceHttpsEndpoint();
```

become Aspire's own native, already-exported names:

```typescript
.withEnvironment('DEMO_INJECTED_BY_APPHOST', 'true')
.withReference(inventory)
```
and
```typescript
.withHttpsEndpoint();
```

Apply the identical three-name substitution (`withServiceEnvironment` → `withEnvironment`,
`withServiceReference` → `withReference`, `withServiceHttpsEndpoint` → `withHttpsEndpoint`) to
`samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts`. Update the two files' surrounding comments
that explain the old `withService*` naming (e.g. the comment block a few lines above these calls in
`DemoAppHostTypeScript/apphost.mts` contrasting `withServiceReference()` against a bare interface's
generated `withReference()`) — that contrast no longer exists once both spellings are the same native
name, so the comment should instead note that `ServiceResource`'s declared shape is what lets these
calls project natively now, with no package-authored shim involved.

- [ ] **Step 4: Run tests and the TypeScript typecheck to verify everything passes**

Run: `dotnet build -c Release --no-restore -warnaserror`
Expected: PASS — confirms nothing else references the deleted `ServiceConfigurationExports` type.

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~UrlConsumerWaitTests"`
Expected: PASS.

Run (once, per the notes file's verify-leg guidance — ideally with the Aspire CLI upgraded to
`13.5.3` to match CI's pin, per Task 11's own note on this):
```bash
cd samples/DemoAppHostTypeScript && aspire restore --non-interactive --nologo && npx tsc --noEmit -p tsconfig.apphost.json
cd samples/DemoAppHostTypeScriptCodeCatalog && aspire restore --non-interactive --nologo && npx tsc --noEmit -p tsconfig.apphost.json
```
Expected: `tsc` exit 0 for both samples — the direct proof of ticket acceptance item 9 against the
now-final exported surface (shims removed, `AddService`'s return type changed).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "Retire the ten WithService*/WaitForService* shims (#313)"
```

---

## Task 10: Update the three ticket-named test files for the final export surface

**Files:**
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`

**Interfaces:**
- Consumes: the final assembly export surface after Tasks 7–9 (no `WithService*`/`WaitForService*`
  ids remain; `AddService`'s reflected return type is `IResourceBuilder<ServiceResource>`).

- [ ] **Step 1: Write the failing assertions**

`CatalogExportsTests.ExportedIds_MatchTheKnownSurface` reflects over the **whole assembly**
(`ExportedMethods()` in that file, not scoped to `ServiceCatalogBuilder`/`ServiceDefinitionBuilder`),
so it already includes every `[AspireExport]` method in `ServiceConfigurationExports` today — meaning
this test, not just `ServiceConfigurationExportsTests.cs`, breaks once Task 9 deletes that file. (This
is a correction to the spec's own §7, which read `CatalogExportsTests.cs` as "unaffected" — that
holds for the file's first three tests, which are scoped to `ServiceCatalogBuilder`/
`ServiceDefinitionBuilder`, but not for `ExportedIds_MatchTheKnownSurface`, which is assembly-wide by
design and design-documented as such in its own doc comment.)

Remove these ten entries from the `expected` array:

```csharp
CamelCase(nameof(ServiceConfigurationExports.WithServiceEnvironment)),
CamelCase(nameof(ServiceConfigurationExports.WithServiceEnvironmentFromParameter)),
CamelCase(nameof(ServiceConfigurationExports.WithServiceEnvironmentFromEndpoint)),
CamelCase(nameof(ServiceConfigurationExports.WithServiceReference)),
CamelCase(nameof(ServiceConfigurationExports.WithServiceConnectionString)),
CamelCase(nameof(ServiceConfigurationExports.WaitForService)),
CamelCase(nameof(ServiceConfigurationExports.WaitForServiceCompletion)),
CamelCase(nameof(ServiceConfigurationExports.WithServiceArg)),
CamelCase(nameof(ServiceConfigurationExports.WithServiceHttpsEndpoint)),
CamelCase(nameof(ServiceConfigurationExports.WithServiceHttpEndpoint)),
```

`"addService"` stays in the list unchanged (`AddService`'s exported id is unaffected by its return
type changing).

In `AddServiceTests.cs`, add a return-type assertion to the existing `AddService_IsExportedToAts`
test (line 484 in the pre-plan file) rather than leaving it silent on the one thing this whole ticket
changes:

```csharp
    [Fact]
    public void AddService_IsExportedToAts()
    {
        var method = typeof(ServiceSourcesBuilderExtensions).GetMethods()
            .Single(m => m.Name == nameof(ServiceSourcesBuilderExtensions.AddService));

        var exportAttribute = method.GetCustomAttributes(typeof(AspireExportAttribute), inherit: false);
        Assert.Single(exportAttribute);

        Assert.Equal(typeof(IResourceBuilder<ServiceResource>), method.ReturnType);

        var nameParameter = method.GetParameters().Single(p => p.Name == "name");
        var resourceNameAttribute = nameParameter.GetCustomAttributes(typeof(ResourceNameAttribute), inherit: false);
        Assert.Single(resourceNameAttribute);
    }
```

- [ ] **Step 2: Run tests to verify they fail against the pre-Task-9 build**

(This step is naturally satisfied by Task 9 already having deleted the ten shims before this task
runs — if executing tasks in order, skip straight to Step 3's "run to verify pass." If executing this
task in isolation against an unmodified checkout, `ExportedIds_MatchTheKnownSurface` fails with the
ten stale ids present and `AddService_IsExportedToAts`'s new assertion fails against the pre-ticket
`IResourceBuilder<IResourceWithServiceDiscovery>` return type.)

- [ ] **Step 3: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~CatalogExportsTests|FullyQualifiedName~AddServiceTests"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs
git commit -m "Update CatalogExportsTests and AddServiceTests for the final export surface (#313)"
```

---

## Task 11: `aspire run`-grade verification, full verify legs, CHANGELOG

**Files:**
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: the complete implementation (Tasks 1–10).

This task closes ticket acceptance items 5 and 6 — the parts the spec itself named as **not** settled
by design-time reasoning alone (spec §8) — and is the one task in this plan that is verification
rather than new code.

- [ ] **Step 1: Write the `aspire run`-grade probes**

Three real-run probes, per spec §8 and the ticket's acceptance item 5. Each needs a real container
runtime and/or a real local checkout — use `smoketest-config-layers.sh`/`smoketest-local-source.sh`'s
existing fixture pattern (a temporary AppHost project referencing this package by `ProjectReference`,
run under `dotnet run` against `DistributedApplicationTestingBuilder` where the existing smoketest
scripts already do this) rather than inventing a new harness:

1. **Endpoint URL resolution end-to-end** (spec §8 item 1): a consumer's
   `WithEnvironment("X", service.GetEndpoint("https"))` resolves to the real URL DCP allocated — for
   a `container`-sourced service and a `local`-sourced one. PR #320's own probe only verified this
   statically (typecheck-level); this must run for real and read back an actual `https://` value from
   the consumer's materialized environment.
2. **Direction-2 `WaitFor` releases correctly** (spec §8 item 2, and Open Question 4 in the amended
   spec): `otherResource.WaitFor(serviceBuilder)` — a consumer waiting on an `AddService` result,
   not the reverse — actually blocks and releases against a real run, for at least `container` and
   `local`. If this probe finds object-identity reliance rather than pure name-keying anywhere past
   `WaitForDependenciesAsync` (health-check annotation lookups in particular — see the amended spec's
   Open Question 4), that is a design bug to fix in this task, most plausibly by having
   `ServiceResourceBuilder` or `ResolvedService.Bridge` also register a second `WaitAnnotation` keyed
   to the real object wherever direction-2 waits land — not a reason to stop and re-ask the human,
   per this ticket's own framing of that question as a plan-time verification step.
3. **Direction-1 `WaitForCompletion` behaves identically to calling it on the real resource directly**
   (spec §8 item 3): `serviceBuilder.WaitForCompletion(migration)` reaches the real, registered
   resource and produces the same ordering as `builder.AddProject(...).WaitForCompletion(migration)`
   or `builder.AddContainer(...).WaitForCompletion(migration)` would.

Run each probe; record its real output (not just pass/fail) in the notes file, per
superpowers:verification-before-completion — this is exactly the category of claim that needs
evidence before assertion.

- [ ] **Step 2: Run every verify leg named in the notes file**

In the order the notes file gives them:

```bash
dotnet restore
dotnet build -c Release --no-restore -warnaserror
dotnet test -c Release --no-build --logger "trx;LogFilePrefix=results" --results-directory ./artifacts/test-results
dotnet pack src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj -c Release --no-build -o ./artifacts/package
./scripts/smoketest-container-source.sh
./scripts/smoketest-config-layers.sh
./scripts/smoketest-local-source.sh
```

For `typecheck-typescript`: upgrade the local Aspire CLI to `13.5.3` first
(`dotnet tool install --tool-path ... aspire.cli --version 13.5.3` or `aspire update`, per the notes
file) so this run actually matches CI's pin rather than the stale locally-installed `13.5.1`, then run
the two `tsc --noEmit` invocations from Task 9 Step 4 again as the final, CI-matching confirmation.

Run `verify-invariants`'s python checks too (cheap, and this ticket's own `.csproj` edits, if any, are
what would trigger `aspire-matrix.yml` — confirm whether any `.csproj` changed across this whole plan;
if none did, `aspire-matrix.yml` correctly does not fire on the PR and that should be said explicitly
in the PR body rather than assumed).

Expected: every leg green, with real output pasted into the notes file for each — not a bare
"passed" claim.

- [ ] **Step 3: Add the CHANGELOG entry**

Under `## [Unreleased]` → `### Breaking` in `CHANGELOG.md` (creating the `### Breaking` subsection
under `[Unreleased]` if today's `[Unreleased]` section doesn't already have one — check the current
file before assuming), matching the register of the existing `[#134]`/`[#291]`-style entries:

```markdown
- **`AddService` returns `IResourceBuilder<ServiceResource>`** ([#313]). `ServiceResource` is a
  concrete, sealed class implementing `IResourceWithServiceDiscovery`, `IResourceWithEnvironment`,
  `IResourceWithArgs`, `IResourceWithEndpoints` and `IResourceWithWaitSupport` — so
  `WithEnvironment`, `WithReference`, `WithArgs`, `WithHttpEndpoint`/`WithHttpsEndpoint`, `WaitFor`
  and `WaitForCompletion` now bind directly on the result of `AddService(...)`, for every source
  (`local`, `container`, `kubernetes`, `url`). `Configure<T>` and the ten
  `WithService*`/`WaitForService*` guest-language shims are removed — there is nothing left for
  them to reach that native vocabulary doesn't already cover:

  ```csharp
  // before
  builder.AddService("orders")
      .Configure<IResourceWithEnvironment>(r => r.WithReference(ordersDb))
      .Configure<IResourceWithWaitSupport>(r => r.WaitForCompletion(migrations));

  // after
  builder.AddService("orders")
      .WithReference(ordersDb)
      .WaitForCompletion(migrations);
  ```

  `As<T>()` is **renamed** to `Unwrap<T>()`, not removed: `As*` is Aspire's own convention for
  reinterpreting a resource for publish while returning the *same* builder type (`AsHttp2Service`,
  and similar) — never a downcast to a different type, which is exactly what this method does, so it
  moves off that prefix. The capability is unchanged: it stays the escape hatch for kind-specific
  vocabulary the five interfaces don't cover, on a non-`dotnet` `local` kind's own resource type —
  `service.Unwrap<JavaScriptAppResource>().WithRunScript("dev")` keeps working exactly as
  `service.As<JavaScriptAppResource>().WithRunScript("dev")` did. `GetServiceEndpoint()` and
  `GetEndpoint(...)` are unaffected either way.

  `ServiceResource` is never the object DCP registers or runs — it dual-writes configuration to the
  real, source-specific resource behind it (Aspire's own `ProjectResource` for `local`; an internal
  container/executable resource for `container`/`kubernetes`; nothing at all for `url`, which has no
  process to configure). An assembly compiled against an earlier version that names
  `IResourceWithServiceDiscovery` as `AddService`'s return type, or calls `Configure<T>`/`As<T>`/any
  `WithService*` method, no longer compiles against this version.
```

Add the `[#313]` link reference alongside the file's other numbered issue links, matching the
existing `[#NNN]: https://github.com/flojon/aspire-servicesources/issues/NNN` format.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -m "Add the Breaking CHANGELOG entry for the ServiceResource dual-write bridge (#313)"
```

---

## Self-review (performed while writing this plan, one round, per superpowers:writing-plans)

**Spec coverage:** Every numbered section of the spec has a task — §1 (Task 1), §2/§2.1/§2.2/§2.3
(Tasks 2, 3, 5), §3's per-interface-member mechanisms (proven by the existing tests each source's
task keeps green, plus Task 3's new copy-forward tests), §4 (Tasks 8, 9), §5 (Task 2's `Reachability`
+ Task 2's tests), §6 (Task 9 Step 4), §7 (Tasks 8, 9, 10 — corrected against the spec's own
under-scoped claim about `CatalogExportsTests.cs`, see Task 10 Step 1), §8 (Task 11). The amended
Open Questions section's four resolutions are folded in: #1 (As\<T\>() renamed to Unwrap\<T\>(), not
retired) throughout Task 8; #2
(warning wording) in Task 2's `Reachability.CapabilityLabel`; #3 (no action) needed no task; #4
(direction-2 WaitFor verification) is Task 11 Step 1 item 2.

**Placeholder scan:** No "TBD"/"add appropriate handling"/"similar to Task N" language appears; every
code step above shows the actual code, and every test step shows the actual assertions.

**Type consistency:** `IResourceBuilder<ServiceResource>` is the type every task from 3 onward passes
forward — Task 3 produces it, Task 4/6 propagate it through `IServiceSource.Resolve` and
`DeferredCheckout`, Task 7 is the point `AddService` itself returns it, Tasks 8–10 consume it. `real`
is consistently `IResourceBuilder<IResource>?` in `ServiceResourceBuilder`'s constructor and `Bridge`'s
own parameter is the generic `IResourceBuilder<TResource>` — both bind via `IResourceBuilder<T>`'s
covariance, checked explicitly in Task 3's test `Bridge_ReturnsAFacadeDistinctFromTheRealResource` and
Task 6's reliance on the same constraint for `InvokeKindHandler`'s `IResourceWithServiceDiscovery`-typed
`resourceBuilder`.

**Ordering is real, not just numbered:** Task 2 (the wrapper) must exist before Task 3 (`Bridge`
constructs it) before Task 4/5/6 (the four sources call `Bridge`/`BridgeUnregistered`) before Task 7
(`AddService` itself compiles only once every `Sources` dictionary entry's `Resolve` already returns
the new type) before Task 8/9 (retiring `Configure<T>`/the shims presupposes native vocabulary already
reaches the real resource, which is exactly what Tasks 2–7 built) before Task 10 (the export-surface
tests can only be updated once the surface they check has actually changed) before Task 11
(verification and CHANGELOG are last, once there is something correct to verify and announce). Each
task's Step 1 either runs the prior baseline or explicitly says why the expected result is red at that
point in the sequence — never a task claiming green on code a later task hasn't written yet.

**Three design bugs found and fixed in this same pass** (documented at the top, under "Deviations
from the spec's literal text"): the `real is not null` guard that would have silently dropped the
`"url"` skip-with-warning behaviour; the missing annotation copy-forward in `Bridge` that would have
broken `GetServiceEndpoint`/`GetEndpoint` and the endpoint mutation-in-place invariant for every
source; and `Unwrap<T>()` (the rename of `As<T>()`, per the human's superseding decision) checking
`service.Resource is T` — always the facade, never the real object, once the bridge is in place —
which would have made the renamed method throw for every call, defeating the entire reason it was
kept rather than retired. All three are now load-bearing parts of Task 2 and Task 3, and Task 2 and
Task 8 respectively, with tests that fail without each fix.

**One gap not fully closed by this plan, named rather than hidden:** Task 11 Step 1 item 2 (direction-2
`WaitFor` name-keyed verification) may surface a real design gap that needs code beyond what this plan
specifies (a second `WaitAnnotation` wired to the real object) — the plan says what to do if that
happens, but the exact code isn't written here because whether it's needed at all is what the probe
determines. This is a legitimate "verify then decide" step, not a placeholder: the two possible
outcomes and the correct response to each are both stated.

## Execution handoff

Plan complete and saved to
`docs/superpowers/plans/2026-09-10-313-service-resource-dualwrite-bridge-plan.md`. Two execution
options:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast
   iteration.
2. **Inline Execution** — execute tasks in this session using executing-plans, batch execution with
   checkpoints.
