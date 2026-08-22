# Aspire.Hosting.ServiceSources — Real Resources for AppHost Configuration

**Date:** 2026-08-22
**Status:** Design — implemented.
**Resolves:** GitHub issues #53 (AppHost cannot apply configuration to a resolved service) and #58
(container consumers can't `WithReference()` a facade).
**Supersedes:** the reference-only facade model established in the
[milestone 1a design](2026-08-09-servicesources-design.md) and carried through the
[deferred-resolution design](2026-08-15-servicesources-phase2-deferred-resolution-design.md).

## Motivation

`AddService()` returned a `ServiceResource` facade that was deliberately never added to
`builder.Resources`. Two independently-filed issues are symptoms of that single decision:

- **#53** — the AppHost cannot apply its own configuration (`WithReference`, `WithEnvironment`,
  `WaitFor`) to the resolved service. A real multi-repo AppHost had ~25 such calls on one service,
  every one carrying a value produced by the AppHost's own graph (a generated secret parameter, a
  container's password reference, a sibling's endpoint, a dynamically allocated port). None can be
  expressed in `servicesources.yaml`/`servicesources.local.json`.
- **#58** — a container consumer referencing a facade fails at startup with
  `Host endpoint 'x' on resource 'y' should have an associated DCP Service resource already set up`,
  because DCP has no Service for an unregistered resource.

Fixing #53 by replaying annotations onto the facade would entrench the facade and make #58 harder.
This design removes the facade instead, which resolves both.

## Findings that constrain the design

Established empirically against Aspire.Hosting 13.4.6 and Aspire CLI 13.5.1 / 13.6.0-pr.19577
(throwaway probes, since discarded):

1. **The old `<remarks>` on `AddService` were wrong.** `WithEnvironment`, `WithReference`, `WaitFor`
   and `WithArgs` did not "compile but silently no-op" on the facade — they were **CS0311 compile
   errors**, because `IResourceWithServiceDiscovery` does not derive from `IResourceWithEnvironment`
   / `IResourceWithArgs` / `IResourceWithWaitSupport`. Only `WithHttpEndpoint` compiled and no-opped.
2. **`ProjectResource` implements `IResourceWithServiceDiscovery`; `ContainerResource` and
   `ExecutableResource` do not.** That, and only that, is why the facade existed.
3. **Subclassing works.** `class X : ContainerResource, IResourceWithServiceDiscovery` registers via
   `AddResource`, is `WithReference`-able by a container consumer, and accepts
   `WithEnvironment`/`WithArgs`/`WaitFor`. This is Aspire's own integration pattern.
4. **`ExternalServiceResource` is `sealed`.** The `url` source cannot delegate to it, confirming
   #58's upstream blocker (microsoft/aspire#9965, #15961, #15993) is unavoidable here.
5. **`AddProject` validates the project path at add time**, so a project resource cannot be
   registered eagerly with a placeholder path. Registering a `ProjectResource` with a *custom*
   `IProjectMetadata` does not throw, but skips `WithProjectDefaults` — `internal`, reads
   `launchSettings.json` at add time, and contributes eight annotations (launch-profile endpoint,
   OTLP exporter, certificate trust, debugging support, five environment callbacks). Hand-rolling it
   is too fragile to maintain across net8/9/10 × Aspire 13.x.
6. **`IResourceBuilder<T>` is covariant (`out T`).** Extension methods named like Aspire's own,
   declared on `IResourceBuilder<IResourceWithServiceDiscovery>`, would therefore also bind to
   `IResourceBuilder<ProjectResource>` and become ambiguous, breaking ordinary
   `AddProject(...).WithEnvironment(...)` calls in any AppHost referencing this package.
7. **Satellite kinds delegate to official Aspire integrations.** #59's JavaScript kind returns
   `IResourceBuilder<JavaScriptAppResource>` from `AddViteApp`/`AddNodeApp`. That type is Aspire's,
   so no package-defined interface can be *required* of `ILocalResourceKind.Resolve` without forcing
   satellites to abandon the official integrations.
8. **`AddService`'s return type must stay `IResourceBuilder<IResourceWithServiceDiscovery>`.**
   Aspire's TypeScript code generator emits **nothing at all** for an exported method returning a
   custom interface. Verified by A/B-ing both return types in a single codegen run:

   | Return type | CLI 13.5.1 | CLI 13.6.0-pr.19577 |
   | --- | --- | --- |
   | `IResourceBuilder<IResourceWithServiceDiscovery>` | generated; `ResourceWithServiceDiscoveryPromise` undeclared (the #19507 bug) | generated; Promise type declared — works |
   | a custom `IServiceBuilder` interface | **not generated at all** | **not generated at all** |

   So narrowing the return type would silently drop `addService` from the TypeScript SDK on every
   Aspire version, breaking #51 worse than the bug #19577 fixes. This finding killed an earlier
   draft of this design.

## Architecture

### Every source returns its real, registered resource

`ServiceResource` is deleted. `IServiceSource.Resolve` returns the resource Aspire actually runs:

| Source | Resource | Notes |
| --- | --- | --- |
| `local` (dotnet kind) | `ProjectResource` via `builder.AddProject(name, path)` | already `IResourceWithServiceDiscovery` |
| `local` (satellite kind) | whatever the kind returns | `ILocalResourceKind` **unchanged** |
| `container` | `ServiceContainerResource : ContainerResource, IResourceWithServiceDiscovery` | new |
| `kubernetes` | `ServiceExecutableResource : ExecutableResource, IResourceWithServiceDiscovery` | new |
| `url` | `ServiceUrlResource : Resource, IResourceWithServiceDiscovery` | stays **unregistered** — see below |

Each resource is tagged with a `ServiceSourceAnnotation` recording the service name and source, so
errors can name what a developer would actually change.

`ILocalResourceKind` requires no change, so the held #44/#45 satellite work (PRs #59/#60) is
unaffected and gains #58's fix for free.

### The `url` source stays unregistered

`ExternalServiceResource` is sealed and a bare registered `Resource` gives DCP nothing to
materialize, so #58 cannot be fixed for `url`, and registering it speculatively would risk the
host-process consumer case that works today. The resource keeps its current shape; a
`BeforeStartEvent` pre-flight detects a container consumer holding an `EndpointReferenceAnnotation`
targeting a `ServiceUrlResource` and throws a `ServiceSourcesConfigurationException` naming the
service, its source and the upstream issues — replacing the raw DCP stack trace (#58 option 3).

### Configuration API (#53)

`AddService` keeps returning `IResourceBuilder<IResourceWithServiceDiscovery>` (finding 8), so the
configuration surface is two extension methods with names that collide with nothing in Aspire
(finding 6):

```csharp
builder.AddService("backend")
    .Configure<IResourceWithEnvironment>(r => r
        .WithReference(planningDb)
        .WithEnvironment("DBPASSWORD", postgres.Resource.PasswordParameter)
        .WithEnvironment("Services__CommonAuth", commonAuth.GetEndpoint("https")))
    .Configure<IResourceWithWaitSupport>(r => r.WaitForCompletion(migrationService));

backend.As<JavaScriptAppResource>().WithRunScript("dev");   // escape hatch
```

`As<T>` retypes the underlying builder; `Configure<T>` is `As<T>` plus a callback, so capabilities
chain. Neither needs updating as Aspire's API grows, and both reach satellite-specific extensions.
The trade-off versus a flat fluent chain is that a capability must be named and cannot be mixed in
one lambda.

**Out-of-band sources are skipped and reported, not applied and not fatal.** `url` and `kubernetes`
resolve to something already running elsewhere, so `Configure<T>` checks the source *before* the
type check — the kubernetes resource is an `ExecutableResource` wrapping `kubectl port-forward` and
would otherwise happily accept environment variables that never reach the service.

Skipping rather than throwing preserves the package's core promise: a developer switching a service
to a remote source in their own `servicesources.local.json` must not break a `Program.cs` they don't
own. Silently dropping the configuration is the failure mode #53 was filed about, so every skip is
logged. Warnings are buffered during composition — `AddService` runs before there is an `ILogger` —
and flushed at `BeforeStartEvent`, still ahead of DCP.

`As<T>` is the exception: it **throws** for those sources, because it must return a builder and the
alternatives are handing back the `kubectl` executable (silently configuring the wrong process) or
returning null. The asymmetry is documented on both methods — `Configure` is for configuration that
should survive a source switch, `As` for when the AppHost genuinely requires a specific type.

### Guest-language exports

`Configure<T>`/`As<T>` cannot cross Aspire's Type System, so #51's TypeScript AppHost would be able
to *resolve* a service but never configure it — the precise failure #53 describes. Two further
codegen constraints, established by generating against Aspire CLI 13.6.0 and reading the output:

- **A generic method loses its type parameter.** `Configure<T>` projects as `configure(...)` with no
  `T`, and `T` is the capability being requested — so it would arrive broken rather than absent.
- **Overloads are silently dropped.** Only the first overload of a name reaches the generated SDK.

`ServiceConfigurationExports` therefore carries one non-generic, distinctly-named `[AspireExport]`
shim per shape — `WithServiceEnvironment`, `WithServiceEnvironmentFromParameter`,
`WithServiceEnvironmentFromEndpoint`, `WithServiceReference`, `WithServiceConnectionString`,
`WaitForService`, `WaitForServiceCompletion`, `WithServiceArg` — each delegating to `Configure<T>`
so the skip-and-log behaviour is inherited rather than duplicated. Verified generating onto
`ResourceWithServiceDiscoveryPromise` and chaining, with the TypeScript sample type-checking clean.

They are marked `[EditorBrowsable(Never)]`: `IResourceBuilder<T>`'s covariance would otherwise put
eight extra methods on every resource builder in IntelliSense, and C# should use `Configure<T>`,
which reaches every Aspire extension method rather than the mirrored subset. ATS exports them
regardless of the attribute — also verified. Two reflection tests pin both codegen constraints so a
future contributor can't silently break guest-language support by adding a generic export or an
overload.

### Eager resolution with parallel checkouts preserved

`AddService` must return a real resource, so `local` resolution can no longer wait for
`BeforeStartEvent`. Parallel git checkouts are preserved by moving the trigger rather than removing
it: the **first** `AddService` call prefetches, in parallel, the checkout for every service in
`servicesources.local.json` whose `source` is `local`. Wall-clock stays `max(checkout)`.

`PendingLocalResolutions` is replaced by `LocalCheckoutPrefetch`, keyed per-builder the same way.
`AddProject(name, realPath)` is then called normally, so all of `WithProjectDefaults`' annotations
come for free and cannot drift with Aspire versions (finding 5).

Prefetch is speculative, so it must never invent a failure:

- a service in `servicesources.local.json` but absent from `servicesources.yaml` is **skipped**;
- a checkout failure is **captured, not thrown**, and re-thrown only when that service is requested;
- a configuration-loading failure aborts the prefetch silently, leaving `ResolveService` to report it.

### Consequences accepted

- **Cross-service failure aggregation is lost.** `BeforeStartEvent` used to report every local
  service's failure in one message. With eager resolution `AddService("a")` must throw before
  `AddService("b")` is reached. Inherent to returning a real resource.
- **`ILocalResourceKind.Validate`'s "before *any* service touches the app model" guarantee is
  lost**, for the same reason; its doc is updated to say it runs before that service's own resource
  is created.
- **Satellite kind registration must precede the first `AddService` call.** The unregistered-kind
  error says so.
- **`AddService`'s signature is unchanged**, so this is not a breaking change for C# AppHosts, and
  #51's TypeScript export behaves exactly as before (still requiring Aspire CLI 13.6.0 for the
  unrelated #19507 codegen bug — no better, no worse).

## Testing

Unit tests cover: each source returning a registered resource; a container consumer referencing
container- and local-sourced services; the `url` pre-flight firing for a container consumer and
staying silent for a host-process one; `Configure<T>`/`As<T>` landing annotations on the real
resource; `Configure<T>` skipping `url` and `kubernetes` without throwing, recording a message that
names the service and source, and that message reaching the log at `BeforeStartEvent`; `As<T>`
throwing for the same sources; every exported shim landing its annotation on the real resource,
inheriting the skip, and chaining; every `[AspireExport]` method being non-generic and uniquely
named; parallel prefetch; catalog-missing services being skipped; and a checkout failure surfacing
only for the service that asked. The existing `AddServiceIntegrationTests` were updated from deferred to
eager expectations and continue to exercise a real git fixture.
