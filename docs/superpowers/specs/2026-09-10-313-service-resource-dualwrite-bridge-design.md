# `AddService` returns `ServiceResource` — the dual-write bridge

**Date:** 2026-09-10
**Status:** Draft
**Resolves:** #313 (`AddService` returns a capability interface, not a concrete resource type —
which forces `Configure<T>`, the `WithService*` infix and `As<T>()`).
**Scope decision (made by the human, not reopened here):** implement the **dual-write
bridge/facade** approach spiked in PRs #319 and #320, not the "track upstream
microsoft/aspire#18052" or "accept a `local` feature regression" alternatives those PRs also
listed. This document designs the real implementation; PRs #319/#320 shipped no source code (spike
convention — all probe C# was written, run, and reverted) and are cited here only as evidenced
findings, re-verified against this repo's own pinned Aspire floor rather than trusted at face
value — see [Verification note](#verification-note).
**Builds on:**
[`2026-09-09-313-concrete-resource-ats-probe-findings.md`](https://github.com/flojon/aspire-servicesources/pull/319)
(ATS codegen feasibility — settled yes) and
[`2026-09-10-313-facade-dcp-dispatch-findings.md`](https://github.com/flojon/aspire-servicesources/pull/320)
(DCP dispatch wall + partial bridge probe), both still open PRs on branches
`worktree-spike-313-ts-codegen` and `worktree-313-servicereturn-facade-dispatch`. Do not touch
those worktrees; this stage branches fresh from `origin/main`.

---

## Verification note

Every class-hierarchy and dispatch claim below was re-checked directly against this repo's own
pinned Aspire floor — `13.5.2` (`Directory.Build.props:74`), not the `13.5.3` the spikes measured
— by decompiling `~/.nuget/packages/aspire.hosting/13.5.2/lib/net8.0/Aspire.Hosting.dll` with
`ilspycmd 11.0.0.9375`. Every one reproduced identically at the floor version. Citations below give
the decompiled member, not just the spike doc's paraphrase of it.

## Motivation (recap)

Today:

```csharp
// src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs:84-85
[AspireExport]
public static IResourceBuilder<IResourceWithServiceDiscovery> AddService(
    this IDistributedApplicationBuilder builder, [ResourceName] string name)
```

`IResourceBuilder<T>` is covariant, so an extension method constrained to
`IResourceBuilder<IResourceWithServiceDiscovery>` — one of Aspire's own capability interfaces — is
also offered on every other resource builder in the AppHost that happens to satisfy it
(`AddProject`, `AddContainer`, `AddExecutable` results included). That forced three permanent
workarounds: `Configure<T>`/`As<T>` (`ServiceConfigurationExtensions.cs`), the ten
`WithService*`/`WaitForService*` shims (`ServiceConfigurationExports.cs`), all three now retired by
this design (§4). PR #319 settled that a **public concrete class** does not hit the TypeScript
codegen wall the current interface return was chosen to avoid; PR #320 settled that **no single
concrete class can be the literal DCP-registered object** for all four sources, because container
dispatch is annotation-based while executable/project dispatch is nominal-type-based, and
`ProjectResource` and `ExecutableResource` are sibling types under `Resource`, not a chain. This
document is the design that follows from both: a facade that is **never** the registered DCP
resource for any source, and reaches the real registered resource (which, for `local`, is Aspire's
own `ProjectResource`) by dual-writing annotation instances.

---

## 1. The `ServiceResource` class

```csharp
namespace Aspire.Hosting.ServiceSources;

public sealed class ServiceResource(string name) : Resource(name),
    IResourceWithServiceDiscovery,
    IResourceWithEnvironment,
    IResourceWithArgs,
    IResourceWithEndpoints,
    IResourceWithWaitSupport;
```

This is exactly PR #319's measured **shape D** (`SpikeFacadeRichResource`): `: Resource` plus the
four capability interfaces beyond `IResourceWithServiceDiscovery`, scoring 76 generated TypeScript
handle members, `tsc` exit 0. Acceptance criterion 3 names precisely these five interfaces; nothing
more is added.

**Deliberately does not implement:**

- `IResourceWithoutLifetime` — see §5; giving every `ServiceResource` instance this marker
  unconditionally would also suppress `WaitFor` on `container`/`kubernetes`/`local`-sourced
  services, which must keep working. The `url` source's "no lifetime" behaviour is preserved a
  different way (§5), not through this interface.
- Any container-only vocabulary (`WithImage`, `WithBindMount`, `WithVolume`, `WithEntrypoint`,
  `WithDockerfile`, `WithLifetime`, …) — these describe *how* a service happened to resolve, which
  is exactly what this package exists to abstract over. An AppHost author who needs them has
  resolved to the wrong abstraction level for this call.
- `IComputeResource`, `IResourceWithProbes` — `ProjectResource`/`ExecutableResource`/
  `ContainerResource` all declare these (verified: decompiled class headers, `13.5.2`), but they
  are DCP-execution-model concerns tied to being the literal registered resource, which
  `ServiceResource` never is (§2). Declaring them on a facade that is never inspected by the code
  that consumes them would be surface with no effect — see the residual-risk table in §3 for what
  "never inspected" rests on.

**Why the facade's base class does not need to match the real resource's, and why this resolves
PR #320's wall rather than merely working around it:** DCP's dispatch — the thing PR #320 found
cannot be satisfied by one class — only ever inspects **registered** resources:

| Dispatch | Decompiled site (`13.5.2`) | What it walks |
|---|---|---|
| Container | `Aspire.Hosting.ContainerResourceExtensions.IsContainer` / `.GetContainerResources` | `resource.Annotations.OfType<ContainerImageAnnotation>().Any()` — annotation-based, no type check |
| Executable | `Aspire.Hosting.ExecutableResourceExtensions.GetExecutableResources` | `model.Resources.OfType<ExecutableResource>()` |
| Project | `Aspire.Hosting.ApplicationModel.ProjectResourceExtensions.GetProjectResources` | `model.Resources.OfType<ProjectResource>()` |

All three walk `model.Resources` (or test `IsContainer` against a resource already known to be in
it) — the model's registered-resource list, populated only by `IDistributedApplicationBuilder.AddResource`/
`AddProject`/`AddExecutable`/`AddContainer`. Under this design, `ServiceResource` is **never**
passed to any of those (§2) — only the real, source-specific object is (`ServiceContainerResource`,
`ServiceExecutableResource`, or the genuine `ProjectResource` `AddProject` returns). So the facade's
declared type is invisible to DCP dispatch entirely, and PR #320's wall — "a single class can't be
assignable to both `ExecutableResource` and `ProjectResource`" — does not apply to it, because the
facade is never asked to be either. That wall was specifically about literal single-object
registration across all four sources; this design does not attempt that.

Class hierarchy, verified at `13.5.2`:

```
public class ProjectResource : Resource, IResourceWithEnvironment, IResource, IResourceWithArgs,
    IResourceWithServiceDiscovery, IResourceWithEndpoints, IResourceWithWaitSupport,
    IResourceWithProbes, IComputeResource, IContainerFilesDestinationResource

public class ExecutableResource : Resource, IResourceWithEnvironment, IResource, IResourceWithArgs,
    IResourceWithEndpoints, IResourceWithWaitSupport, IResourceWithProbes, IComputeResource
```

`ProjectResource` and `ExecutableResource` are siblings under `Resource`, confirming PR #320's
finding independently of its own decompile (which read `13.5.3`).

---

## 2. The dual-write wrapper

### 2.1 Shape

```csharp
// internal — an AppHost author only ever holds IResourceBuilder<ServiceResource>
internal sealed class ServiceResourceBuilder(
    IDistributedApplicationBuilder applicationBuilder,
    ServiceResource facade,
    IResourceBuilder<IResource>? real,   // null only for the "url" source — see 2.3
    string source)
    : IResourceBuilder<ServiceResource>
{
    public IDistributedApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;
    public ServiceResource Resource { get; } = facade;

    public IResourceBuilder<ServiceResource> WithAnnotation<TAnnotation>(
        TAnnotation annotation, ResourceAnnotationMutationBehavior behavior = ResourceAnnotationMutationBehavior.Append)
        where TAnnotation : IResourceAnnotation
    {
        // Reachability.IsUnreachable is the §5 table, re-keyed by annotation type — the direct
        // port of today's ServiceConfigurationExtensions.IsUnreachable<T>, not a new mechanism.
        if (real is not null && Reachability.IsUnreachable(typeof(TAnnotation), source))
        {
            ServiceSourcesWarnings.For(ApplicationBuilder).AddSkip(facade.Name, source, typeof(TAnnotation).Name);
            return this;
        }

        // Mirrors Aspire's own DistributedApplicationResourceBuilder<T>.WithAnnotation exactly
        // (decompiled, 13.5.2) — Replace removes any existing TAnnotation before adding, Append
        // does not. Re-implemented here rather than delegated, because ResourceAnnotationCollection
        // itself has only a bare Add(IResourceAnnotation) — the Replace-or-Append branch lives on
        // the builder, not the collection, and this builder is the facade's own.
        if (behavior == ResourceAnnotationMutationBehavior.Replace
            && facade.Annotations.OfType<TAnnotation>().SingleOrDefault() is { } existing)
        {
            facade.Annotations.Remove(existing);
        }

        // The SAME instance goes into both collections — never a copy. This is what makes DCP's
        // mutation-in-place of an already-added annotation (see WithEndpoint's update branch in
        // §3) visible through either object, and what makes an EndpointAnnotation's
        // AllocatedEndpoint — set by DCP against the real, registered object during an actual run
        // — readable through the facade afterwards.
        facade.Annotations.Add(annotation);

        // real.WithAnnotation (not a direct Annotations.Add) so the real resource's own
        // Replace-or-Append bookkeeping runs too — it is a distinct collection with its own
        // possible pre-existing TAnnotation, and only Aspire's own builder implementation should
        // decide what "replace" means against it.
        real?.WithAnnotation(annotation, behavior);

        return this;
    }
}
```

`IResourceBuilder<T>.WithAnnotation<TAnnotation>` is an ordinary interface member
(`Aspire.Hosting.ApplicationModel.IResourceBuilder<T>`, decompiled), constrained only to
`TAnnotation : IResourceAnnotation` — nothing Aspire-internal about it that a hand-rolled
implementation cannot satisfy. Every native vocabulary method this design buys back
(`WithEnvironment`, `WithReference`, `WithArgs`, `WithHttpEndpoint`/`WithHttpsEndpoint`, `WaitFor`,
`WaitForCompletion`) is an extension method on `IResourceBuilder<T>` that — for a **new**
annotation — calls `builder.WithAnnotation(...)` (verified per-method in §3), so this one override
point intercepts all of them.

`real?.WithAnnotation(...)` reuses Aspire's **own** mutation-behavior implementation
(`DistributedApplicationResourceBuilder<T>.WithAnnotation`, decompiled above) on the real object,
rather than reimplementing it a second time — the real builder is a genuine
`IResourceBuilder<ContainerResource>`/`IResourceBuilder<ExecutableResource>`/
`IResourceBuilder<ProjectResource>` Aspire itself constructed, so its own Replace/Append logic is
already correct for that side.

### 2.2 Per-source wiring

| Source | Real registered resource | `real` in the wrapper | Notes |
|---|---|---|---|
| `container` | `ServiceContainerResource : ContainerResource` (`Sources/ServiceResources.cs:12`) | the builder `ContainerSource.Resolve` already builds | Unchanged: still built by hand via `WithImage`/`WithEndpoint`, still gets the `IResourceWithServiceDiscovery` `ContainerResource` itself lacks. |
| `kubernetes` | `ServiceExecutableResource : ExecutableResource` (`Sources/ServiceResources.cs:19`) | the builder `KubernetesSource.Resolve` already builds | Same construction as today. |
| `local` (`dotnet` kind) | Aspire's own `ProjectResource`, from `builder.AddProject(serviceName, projectPath)` (`Sources/LocalProjectSource.cs:223`) | that builder, unchanged | The genuinely new case: `real` is the framework's own type, not one of this package's. Every capability the facade declares is *also* natively on `ProjectResource` — the bridge exists only so the return type can be one fixed `ServiceResource` across all four sources, not because `local` itself needs help. |
| `local` (non-`dotnet` kind, e.g. JavaScript/Java) | Whatever `ILocalResourceKind.Resolve` returns (`JavaScriptAppResource`, a Java equivalent, …) | that builder | Kind-specific vocabulary beyond the five interfaces (`WithRunScript` and similar) is **not** reachable through `ServiceResource` — see [Open Question 1](#open-questions). |
| `url` | *none* — `ServiceUrlResource` is deliberately never registered (`Sources/UrlSource.cs:64`, `IServiceSource.cs:13-15`) | `null` | The facade **is** the only object; there is nothing to dual-write to. See §2.3. |

`ResolvedService.Tag` (today: tags the resource and widens the return type) is replaced by a
`ResolvedService.Bridge(IResourceBuilder<TResource> real, string serviceName, string source)`
that:

1. Constructs the `ServiceResource` facade and adds `ServiceSourceAnnotation` to **both** the
   facade's own `Annotations` and (when `real is not null`) the real resource's — directly, ahead
   of and outside the reachability table in §5, since this is core plumbing every source needs
   unconditionally, not user-facing configuration that could be skipped. This split matters and is
   not the same choice as tagging the facade alone: `ServiceSourceAnnotation`'s readers do not
   agree on which object they hold.
   - `ServiceEndpointExtensions.Explain` and (today) `ServiceConfigurationExtensions` read it off
     `service.Resource` — whatever `AddService` returned, i.e. the facade.
   - `ServiceStartupFailureNotices.Notice` (`ServiceStartupFailureNotices.cs:305`) reads it off
     `published.Resource`, where `published` is a `ResourceEvent` from Aspire's own notification
     stream — which only ever carries **registered** resources. `BufferingPrepareOutputSink.Flush`
     (`BufferingPrepareOutputSink.cs:126-129`) reads it the same way, iterating
     `@event.Model.Resources` directly. Both would silently stop finding the annotation for every
     `container`/`kubernetes`/`local`-sourced service — breaking checkout-progress logging and
     startup-failure reporting, both existing, shipped features — had this document's first draft
     ("tag the facade, not the real object") gone unchecked. Caught only by tracing where
     `ServiceSourceAnnotation` is actually read in this repo's own code, not by inspecting
     `ResolvedService.cs` in isolation.
   - For `url` there is no real object, so only the facade is tagged — unchanged from today, since
     `ServiceUrlResource` is unregistered today too and neither of the two notification-based
     readers above can see it regardless of source.
2. Wraps `real` in a `ServiceResourceBuilder` and returns
   `IResourceBuilder<ServiceResource>`.

### 2.3 `url`: no bridging, because there is nothing to bridge to

`UrlSource.Resolve` keeps building a `ServiceUrlResource`-shaped object (renamed under this design
— it can simply be a `ServiceResource` instance that is never registered, carrying the hand-built
`EndpointAnnotation` with its `AllocatedEndpoint` set eagerly, exactly as `UrlSource.cs:52-61` does
today) and calls `ResolvedService.Bridge(builder.CreateResourceBuilder(facade), serviceName,
"url")` with `real: null`. The `ServiceResourceBuilder`'s `WithAnnotation` degrades to
single-write (facade only) when `real is null` — every annotation the AppHost author adds through
the returned builder is genuinely inert for `url` in exactly the way `Configure<T>`'s skip-with-
warning already treats it (§5), so writing it to the facade alone and warning is correct, not a
gap.

`DropWaitsOnUrlServices` (`Sources/UrlSource.cs:174-202`) currently matches
`wait.Resource is ServiceUrlResource`. Under this design that becomes `wait.Resource is
ServiceResource sr && sr.Annotations.OfType<ServiceSourceAnnotation>().Any(a => a.Source ==
"url")` — the type check alone no longer distinguishes a `url`-sourced facade from a `container`-
or `local`-sourced one, since all four now share the `ServiceResource` type. The `RegisterContainerConsumerCheck`
pre-flight (`UrlSource.cs:104-122`, matching `ConsumedUrlService`) needs the identical change.

---

## 3. How each interface member reaches the real resource — and the actual residual risk per one

Every mechanism below was read from the decompiled `13.5.2` assembly, not assumed.

### `WithEnvironment` (literal value, parameter, endpoint, or another resource)

`ResourceBuilderExtensions.WithEnvironment<T>(builder, name, value)` (and the `ReferenceExpression`/
`Func<string>`/callback overloads) all end in `return builder.WithAnnotation(new
EnvironmentAnnotation(...))` or `return builder.WithAnnotation(new
EnvironmentCallbackAnnotation(...))`. Reaches the real resource entirely through §2's
`WithAnnotation` override — no other seam.

**Residual risk, checked, not hypothetical:** none found for the *write* side — every overload
decompiled routes through `WithAnnotation`. Nothing in `ResourceBuilderExtensions` was found adding
an `EnvironmentAnnotation`/`EnvironmentCallbackAnnotation` by mutating `builder.Resource.Annotations`
directly.

### `WithReference` (service discovery)

`WithReference<TDestination>(builder, source)` where `source : IResourceBuilder<IResourceWithServiceDiscovery>`
is: `builder.ApplyEndpoints(source.Resource); return builder;` — it captures **`source.Resource`**,
i.e. the facade instance (our `ServiceResource`), not `real.Resource`. `ApplyEndpoints` (private,
not independently decompiled here, but its callers and PR #320's own probe agree) adds an
`EnvironmentCallbackAnnotation` to the *consumer* (`builder`, the destination) whose callback, run
lazily at environment-materialization time, reads endpoints off the captured object's own
`Annotations` collection.

**Residual risk, checked:** this is why the dual-write must add the identical `EndpointAnnotation`
*instance* to the facade's own `Annotations`, not only the real object's — `WithReference` never
touches `real` at all, only what the facade captured at the moment `WithReference` was called. PR
#320's probe verified exactly this path in isolation (invoking the resulting
`EnvironmentCallbackAnnotation.Callback` directly, since a bare test builder cannot run
`GetEnvironmentVariableValuesAsync` to completion); this design generalizes it rather than
introducing a new mechanism.

### `WithArgs`

`WithArgs<T>(builder, string[] args)` → `builder.WithArgs(callback)` → `builder.WithAnnotation(new
CommandLineArgsCallbackAnnotation(...))`. Same seam as `WithEnvironment`; reached via §2's override,
dual-written to `real` so the process DCP actually starts (the real, registered
`ServiceContainerResource`/`ServiceExecutableResource`/`ProjectResource`) sees the argument
callback — DCP only ever reads argument annotations off the object it registered, so a
facade-only write would be silently inert.

### Endpoints (`WithHttpEndpoint`/`WithHttpsEndpoint`/`GetEndpoint`)

`WithHttpEndpoint<T>` → `builder.WithEndpoint(port, targetPort, "http", ...)`. `WithEndpoint<T>`'s
own body:

```csharp
EndpointAnnotation? endpointAnnotation = builder.Resource.Annotations
    .OfType<EndpointAnnotation>()
    .FirstOrDefault(sb => string.Equals(sb.Name, resolvedName, ...));
if (endpointAnnotation != null)
{
    if (port.HasValue) endpointAnnotation.Port = port;
    // ... mutates the found annotation's properties in place, returns builder directly
}
// else: builder.WithAnnotation(new EndpointAnnotation(...))
```

**Residual risk, checked, and this is the one genuinely worth naming precisely:** calling
`WithHttpEndpoint()` a *second* time for the same endpoint name does **not** go through
`WithAnnotation` at all — it finds the existing `EndpointAnnotation` on `builder.Resource.Annotations`
(the facade's own collection, under this design) and mutates its properties directly, bypassing
the override in §2 entirely. This is exactly the pattern
`ServiceSourcesBuilderExtensions.UseDeferredCheckout`'s own remarks describe as load-bearing today
("`WithHttpEndpoint` updates an endpoint of the same name using its non-null arguments only... so
there is one call, not one per path" — `ServiceSourcesBuilderExtensions.cs:180-182`). It resolves
correctly under this design **only because the dual-write puts the same object instance, not a
copy, into both collections**: the facade's copy of the reference is mutated in place, and since
it is the identical object also sitting in `real.Resource.Annotations`, the mutation is visible
there too with no further action. Had the design copied annotation values instead of sharing
instances, this update path would silently update only the facade and never reach DCP. This is not
a gap to fix — it is the reason "share the instance, not the value" is the load-bearing design
choice PR #320's probe made and this document keeps — but it means **no future change to this
bridge may switch from sharing instances to copying values** without re-auditing this exact path.

`GetEndpoint<T>(builder, name)` → `builder.Resource.GetEndpoint(name)` — again reads off the
facade's own `Annotations`, which is why the shared-instance requirement above is not particular to
`WithEndpoint`'s update branch; it is the general rule this whole design rests on.

**Still unverified end-to-end (ticket acceptance item 5, not settled by this document):** the
`EndpointAnnotation.AllocatedEndpoint` DCP sets during a real run is set against whichever object
reference DCP itself holds — the real, registered resource. Because the annotation instance is
shared, reading `AllocatedEndpoint` through the facade's copy of the same reference should see it
too, with no further plumbing — but this was only checked as a static/typecheck-level argument by
PR #320 (its own probe explicitly could not exercise this without a live `aspire run`: "resolving
an `EndpointReference`'s actual value... hangs against a bare test builder with no live app"). This
implementation must add an `aspire run`-grade test (real container runtime or a real local
project) that resolves a consumer's `WithEnvironment("X", service.GetEndpoint("https"))` to an
actual URL string, not just a key, before this is considered proven.

### `WaitFor` / `WaitForCompletion`

`WaitFor<T>(builder, dependency)` → `WaitForCore` → (after self-wait and parent-wait guards)
`builder.WithAnnotation(new WaitAnnotation(dependency.Resource, WaitType.WaitUntilHealthy) {
WaitBehavior = waitBehavior })`. Two directions matter, and they are not symmetric:

1. **The service waits for something else** — `serviceBuilder.WaitFor(migrations)`. Here
   `builder` in `WaitForCore` **is** our `ServiceResourceBuilder`, so the `WaitAnnotation` is added
   through §2's override and dual-written onto `real`. This direction *requires* the dual-write:
   `ResourceNotificationService.WaitForDependenciesAsync(resource, ...)` (decompiled) is invoked by
   the orchestrator against the **real, registered** resource — `resource.TryGetAnnotationsOfType<WaitAnnotation>()`
   is read off that object, never off an unregistered facade the orchestrator never iterates. A
   facade-only write here would be silently inert, identically to the args case above.
2. **Something else waits for the service** — `otherBuilder.WaitFor(serviceBuilder)`. Here
   `dependency.Resource` (captured into the `WaitAnnotation`) is the **facade** instance, and the
   annotation lands on `otherBuilder`'s own resource, not ours — no dual-write involved on our
   side at all. Whether this resolves correctly rests on how the wait is later honoured:
   `ResourceNotificationService` keys every lookup by resource **name**
   (`WaitForResourceAsync(string resourceName, ...)`, `TryGetCurrentState(string resourceId, ...)`,
   both decompiled) rather than object identity, and `WaitForDependenciesAsync` groups pending
   dependencies via `waitAnnotation.Resource.GetResolvedResourceNames()` — a name, not a reference.
   Since the facade and the real object are constructed with the identical `serviceName`, name-keyed
   lookup finds the real object's published state regardless of which object the `WaitAnnotation`
   captured. **This is a real de-risking finding this document adds beyond what PR #320 checked**
   (its probe covered direction 1's mechanism only), but it is still not a full proof: whether every
   code path `WaitUntilHealthyAsync`/`WaitUntilCompletionAsync`/`WaitUntilStartedAsync` walks from
   there (health-check annotation lookups in particular) is also purely name-keyed, rather than
   assuming a reference at some point, was not traced past `WaitForDependenciesAsync` itself.
   Flagged for the `aspire run`-grade verification acceptance item 5 requires — direction 2 is the
   more likely place a subtle failure would hide, precisely because it needs no dual-write to
   *look* like it works.

### `Configure<T>`/`As<T>` themselves

Retired (§4) — nothing reaches them once native vocabulary is in place.

---

## 4. Retiring `Configure<T>`, the `WithService*`/`WaitForService*` shims, and `As<T>()`

- **`Configure<T>`/`As<T>` (`ServiceConfigurationExtensions.cs`)** are deleted. Every capability
  they existed to reach — `IResourceWithEnvironment`, `IResourceWithArgs`, `IResourceWithEndpoints`,
  `IResourceWithWaitSupport` — is now a real interface `ServiceResource` implements, so Aspire's own
  extension methods bind directly. The out-of-band skip-with-warning behaviour `Configure<T>` and
  `As<T>` implemented moves into `ServiceResourceBuilder.WithAnnotation` (§5) — it does not
  disappear, it changes which type it is keyed on (annotation type instead of a generic `T`).
- **The ten `WithService*`/`WaitForService*` shims (`ServiceConfigurationExports.cs`)** are deleted.
  They existed solely as the guest-language (TypeScript/polyglot) face of `Configure<T>`, each
  carrying `[AspireExport]` because a generic method cannot itself project (ATS erases `T` to its
  constraint — `ServiceConfigurationExports.cs:26-33`). With `ServiceResource` concrete, Aspire's
  **own** `[AspireExport]`-carrying extension methods (`withEnvironment`, `withReference`,
  `withArgs`, `waitFor`, `waitForCompletion`, `withHttpEndpoint`, `withHttpsEndpoint`) already
  project onto the generated handle (verified: PR #319's shape-D handle enumerates exactly this
  vocabulary) — there is nothing left for a package-authored shim to add.
- **`GetServiceEndpoint` (`ServiceEndpointExtensions.cs`)** is unaffected — it was already
  `[AspireExport]`-able because it is non-generic and returns `EndpointReference`, a type ATS
  models. It keeps working unchanged; it reads `service.Resource.Annotations.OfType<EndpointAnnotation>()`,
  which is now the facade's own dual-written collection instead of the real resource's collection
  directly — behaviourally identical, since the annotation instances are shared (§2, §3).

## 5. Preserving `Configure`'s skip-with-warning for `url`/`kubernetes`

Today, `ServiceConfigurationExtensions.IsUnreachable<T>` treats a source as `OutOfBandSources`
(`url`, `kubernetes`) and skips **every** capability `T` except `IResourceWithWaitSupport` for
`kubernetes` specifically (`"kubernetes"` resolves to a real local `kubectl port-forward` process,
so wait ordering against it is real; `url` has no process at all, so nothing survives).

**Scope of this table:** it governs only annotations added *through the returned
`IResourceBuilder<ServiceResource>`* — i.e. the AppHost author configuring the service itself
(§3's "direction 1" for `WaitAnnotation`: `serviceBuilder.WaitForCompletion(x)`,
`serviceBuilder.WithEnvironment(...)`, and so on). It has nothing to do with another resource
waiting *on* a `url`-sourced service (§3's "direction 2": `otherBuilder.WaitFor(serviceBuilder)`)
— that case adds the `WaitAnnotation` to `otherBuilder`'s own `Annotations`, never reaches
`ServiceResourceBuilder.WithAnnotation` at all, and is handled entirely by §2.3's updated
`DropWaitsOnUrlServices` instead. The two mechanisms are independent and both are required; do not
read the "url: skip + warn" row below as also describing what happens when something else waits on
a `url` service — that path drops the wait silently-but-reported, not skip-with-warning through
this table.

This design re-keys the identical table by **annotation type** instead of generic `T`, since native
vocabulary no longer passes a capability type parameter through a single dispatcher:

| Annotation added | Capability it serves | `container`/`local` | `kubernetes` | `url` |
|---|---|---|---|---|
| `EnvironmentAnnotation` / `EnvironmentCallbackAnnotation` | `IResourceWithEnvironment` | apply | skip + warn | skip + warn |
| `CommandLineArgsCallbackAnnotation` | `IResourceWithArgs` | apply | skip + warn | skip + warn |
| `EndpointAnnotation` | `IResourceWithEndpoints` | apply | skip + warn | skip + warn |
| `WaitAnnotation` | `IResourceWithWaitSupport` | apply | **apply** | skip + warn |

— an exact re-expression of today's table (`ServiceConfigurationExtensions.cs:150-153`), not a
behavioural change. The warning itself (`ServiceSourcesWarnings.AddSkip`) is unchanged; only the
label passed (today, `typeof(T).Name`; under this design, `typeof(TAnnotation).Name`) changes, and
should be adjusted so the message still names a capability an AppHost author recognizes (e.g. map
`EnvironmentAnnotation`/`EnvironmentCallbackAnnotation` to a shared "environment" label) rather than
a raw annotation type name — see [Open Question 2](#open-questions).

`As<T>()`'s throwing behaviour (rather than `Configure<T>`'s skip) has no equivalent need under
this design: there is no longer a generic escape hatch returning `IResourceBuilder<T>` for an
out-of-band source to throw from, because the native methods this design buys back are the only
public surface, and they degrade to skip-with-warning uniformly. The one place `As<T>`'s
throw-not-skip behaviour mattered — reaching a **kind-specific** resource type
(`service.As<JavaScriptAppResource>().WithRunScript("dev")`) — is not replaced by anything native
and is called out as [Open Question 1](#open-questions).

## 6. TypeScript codegen compatibility

`ServiceResource`'s declared shape (`: Resource, IResourceWithServiceDiscovery,
IResourceWithEnvironment, IResourceWithArgs, IResourceWithEndpoints, IResourceWithWaitSupport`) is
character-for-character PR #319's measured shape D — 76 generated handle members, `tsc` exit 0
against `samples/DemoAppHostTypeScript`. ATS's codegen reads the **declared public type shape**
of the method's return type, not how the returned builder is implemented internally, so the
`ServiceResourceBuilder` wrapper (§2) — entirely an implementation detail behind
`IResourceBuilder<ServiceResource>` — has no bearing on what the generator emits. No new ATS
probe is required to re-confirm shape D; it is required to re-confirm the **whole exported
surface** once the ten `WithService*` shims are removed (§4) and `AddService`'s return type
changes — the `typecheck-typescript` CI leg (`.github/workflows/ci.yml`, both
`samples/DemoAppHostTypeScript` and `samples/DemoAppHostTypeScriptCodeCatalog`) is exactly that
check, and per the ticket notes must be run before the PR, ideally against CLI `13.5.3` to match
CI's pin (the locally installed CLI is `13.5.1` as of this writing).

## 7. Test and export-surface impact (acceptance item 10)

- `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs:484`
  (`AddService_IsExportedToAts`) asserts `AddService`'s return type indirectly via reflection over
  its `[AspireExport]`/`[ResourceName]` attributes — unaffected by the return-type change itself,
  but any test in this file asserting `IResourceBuilder<IResourceWithServiceDiscovery>`
  specifically (rather than duck-typing through the interface) needs updating to
  `IResourceBuilder<ServiceResource>`.
- `test/Aspire.Hosting.ServiceSources.Tests/ServiceConfigurationExportsTests.cs` — exercises the
  ten shims this design deletes; the whole file is retired, and its coverage (that a service's
  configuration reaches the real resource, and that out-of-band sources skip with a warning) needs
  re-homing as tests against `ServiceResourceBuilder` directly.
- `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs` — guards
  `ServiceCatalogBuilder`/`ServiceDefinitionBuilder`'s own export shape (unrelated builder types);
  unaffected unless a new reflection-based guard is added for `ServiceResource`'s own export shape,
  which would be a reasonable addition given `CatalogExportsTests`' own stated purpose (catching an
  accidental breaking export mistake with nothing else watching for it).

## 8. What the implementation must still prove (not settled by this document)

Per the ticket's own acceptance checklist (items 5 and 6), the following are **design-time**
claims this document makes with decompiled evidence, not **run-time** proof, and the
implementation is not done until an `aspire run`-grade test (real container runtime and/or a real
local checkout; `kubectl` only if a real cluster is available) confirms each:

1. A consumer's `WithEnvironment("X", service.GetEndpoint("https"))` resolves to the real URL
   string DCP allocated, for a `container`-sourced service and for a `local`-sourced one — not
   just the environment-variable *key*, which PR #320 already verified statically.
2. `otherResource.WaitFor(serviceBuilder)` (direction 2 in §3) actually blocks and releases
   correctly against a real run, for at least `container` and `local`.
3. `serviceBuilder.WaitForCompletion(migration)` (direction 1) reaches the real resource and
   behaves identically to calling it on `AddProject(...)`/`AddContainer(...)` directly.

## Attack surface

None added. This design changes an in-process object graph (which C# object a builder wraps, and
which annotation instances it shares) between two collections Aspire itself owns; it parses no new
untrusted input, constructs no new shell command, URL, path, or query, and crosses no process or
trust boundary that `container`/`kubernetes`/`url`/`local` resolution did not already cross today.
The reachability table in §5 is a stricter gate than today's `IsUnreachable<T>`, not a looser one —
it can only suppress configuration a developer's chosen source cannot honour, never apply
configuration somewhere new. The one behavioural change worth naming here rather than treating as
purely internal: `ServiceSourceAnnotation` is now readable off two objects instead of one for three
of the four sources (§2), which is more copies of the same non-secret metadata (service name,
source string), not new exposure.

## Open Questions

1. **RESOLVED (human decision, 2026-09-10): keep `As<T>()` as an escape hatch; it is excluded from
   "retired."** Only `Configure<T>` and the ten `WithService*`/`WaitForService*` shims are retired —
   native vocabulary on `ServiceResource`'s five interfaces replaces those. `As<T>()` stays available
   for kind-specific vocabulary on non-`dotnet` local kinds that the five interfaces don't cover
   (e.g. `service.As<JavaScriptAppResource>().WithRunScript("dev")`), which is exactly the gap this
   question originally raised. This closes acceptance-checklist item 7 with `As<T>()` explicitly
   excluded from "retired." No further design work is needed for this question: `As<T>()`'s existing
   signature, throw-on-unreachable-source behaviour, and receiver type are all unchanged by this
   document — see the implementation plan
   (`docs/superpowers/plans/2026-09-10-313-service-resource-dualwrite-bridge-plan.md`) for the exact
   file-level treatment.
2. **RESOLVED (implementation-time judgment call, folded into the plan): warning message wording once
   the skip is keyed by annotation type.** The plan maps each annotation type to the native method
   name an AppHost author actually called (`EnvironmentAnnotation`/`EnvironmentCallbackAnnotation` →
   "WithEnvironment", `CommandLineArgsCallbackAnnotation` → "WithArgs", `EndpointAnnotation` →
   "WithHttpEndpoint/WithHttpsEndpoint", `WaitAnnotation` → "WaitFor/WaitForCompletion") via a small
   `Reachability.CapabilityLabel` helper. Reasonable, not escalated further.
3. **RESOLVED (confirmed complete, no action needed): whether `IResourceBuilder<T>` gains members
   beyond `WithAnnotation` in a later Aspire release.** Confirmed complete at this repo's pinned floor
   (`13.5.2`) at plan-writing time. Not re-verified against a later floor as part of this stage; a
   heads-up for whoever next moves the pinned floor, not a blocking gap here.
4. **RESOLVED as a plan task, not further design here: direction-2 `WaitFor` (§3) rests on name-keyed
   lookup found only as far as `WaitForDependenciesAsync`.** The implementation plan makes this an
   explicit verification step — the `aspire run`-grade probe the ticket's acceptance item 5 already
   requires must exercise `otherResource.WaitFor(serviceBuilder)` for at least `container` and `local`
   and confirm it releases correctly. Any finding of object-identity reliance (rather than pure
   name-keying) surfaced by that probe is a plan-time bug to design around — most likely, wiring a
   second, redundant `WaitAnnotation` onto the real resource for this direction too — not a reason to
   stop and ask again.
