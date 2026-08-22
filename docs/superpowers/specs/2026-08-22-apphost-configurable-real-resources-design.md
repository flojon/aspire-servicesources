# Aspire.Hosting.ServiceSources — Real Resources for AppHost Configuration

**Date:** 2026-08-22
**Status:** Design — approved, ready for implementation.
**Resolves:** GitHub issues #53 (AppHost cannot apply configuration to a resolved service) and #58
(container consumers can't `WithReference()` a facade).
**Supersedes:** the reference-only facade model established in the
[milestone 1a design](2026-08-09-servicesources-design.md) and carried through the
[deferred-resolution design](2026-08-15-servicesources-phase2-deferred-resolution-design.md).

## Motivation

`AddService()` returns a `ServiceResource` facade that is deliberately never added to
`builder.Resources`. Two independently-filed issues are symptoms of that single decision:

- **#53** — the AppHost cannot apply its own configuration (`WithReference`, `WithEnvironment`,
  `WaitFor`) to the resolved service. A real multi-repo AppHost had ~25 such calls on one service,
  every one of them carrying a value produced by the AppHost's own graph (a generated secret
  parameter, a container's password reference, a sibling's endpoint, a dynamically allocated port).
  None can be expressed in `servicesources.yaml`/`servicesources.local.json`.
- **#58** — a container consumer referencing a facade fails at startup with
  `Host endpoint 'x' on resource 'y' should have an associated DCP Service resource already set up`,
  because DCP has no Service for an unregistered resource.

Fixing #53 with record-and-replay onto the facade would entrench the facade and make #58 harder.
This design removes the facade instead, which resolves both.

## Findings that constrain the design

Established empirically against Aspire.Hosting 13.4.6 (throwaway probes, since discarded):

1. **The `<remarks>` on `AddService` are wrong.** `WithEnvironment`, `WithReference`, `WaitFor` and
   `WithArgs` do not "compile but silently no-op" on the facade — they are **CS0311 compile
   errors**, because `IResourceWithServiceDiscovery` does not derive from `IResourceWithEnvironment`
   / `IResourceWithArgs` / `IResourceWithWaitSupport`. Only `WithHttpEndpoint` (which needs just
   `IResourceWithEndpoints`) compiles and no-ops.
2. **`ProjectResource` implements `IResourceWithServiceDiscovery`; `ContainerResource` and
   `ExecutableResource` do not.** That, and only that, is why the facade exists.
3. **Subclassing works.** `class X : ContainerResource, IResourceWithServiceDiscovery` registers via
   `AddResource`, is `WithReference`-able by a container consumer, and accepts
   `WithEnvironment`/`WithArgs`/`WaitFor`. This is Aspire's own integration pattern.
4. **`ExternalServiceResource` is `sealed`.** The `url` source cannot delegate to it, confirming
   #58's upstream blocker (microsoft/aspire#9965, #15961, #15993) is unavoidable here.
5. **`AddProject` validates the project path at add time** (`DistributedApplicationException:
   Project file '...' was not found`), so a project resource cannot be registered eagerly with a
   placeholder path. Registering a `ProjectResource` with a *custom* `IProjectMetadata` does not
   throw — but it also skips `WithProjectDefaults`, which is `internal`, reads `launchSettings.json`
   from disk at add time, and contributes eight annotations (launch-profile endpoint, OTLP exporter,
   certificate trust, debugging support, five environment callbacks). Hand-rolling that is too
   fragile to maintain across net8/9/10 × Aspire 13.x.
6. **`IResourceBuilder<T>` is covariant (`out T`).** Extension methods declared directly on
   `IResourceBuilder<IResourceWithServiceDiscovery>` would therefore also bind to
   `IResourceBuilder<ProjectResource>` and collide with Aspire's own, breaking ordinary
   `AddProject(...).WithEnvironment(...)` calls in every AppHost. A distinct marker interface avoids
   this; verified to bind unambiguously in both directions.
7. **Satellite kinds delegate to official Aspire integrations.** #59's JavaScript kind returns
   `IResourceBuilder<JavaScriptAppResource>` from `AddViteApp`/`AddNextJsApp`/`AddNodeApp`. That type
   is Aspire's, so no package-defined interface can be *required* of `ILocalResourceKind.Resolve`
   without forcing satellites to abandon the official integrations.

## Architecture

### Every source returns its real, registered resource

The facade (`ServiceResource`) is deleted. `IServiceSource.Resolve` returns the resource Aspire
actually runs:

| Source | Resource | Notes |
| --- | --- | --- |
| `local` (dotnet kind) | `ProjectResource` via `builder.AddProject(name, path)` | already `IResourceWithServiceDiscovery` |
| `local` (satellite kind) | whatever the kind returns | `ILocalResourceKind` **unchanged** |
| `container` | `ServiceContainerResource : ContainerResource, IResourceWithServiceDiscovery` | new |
| `kubernetes` | `ServiceExecutableResource : ExecutableResource, IResourceWithServiceDiscovery` | new |
| `url` | `ServiceUrlResource : Resource, IResourceWithServiceDiscovery` | stays **unregistered** — see below |

`ILocalResourceKind` requires no change, so the held #44/#45 satellite work (PRs #59/#60) is
unaffected and gains #58's fix for free.

### The `url` source stays unregistered

`ExternalServiceResource` is sealed and a bare registered `Resource` gives DCP nothing to
materialize, so #58 cannot be fixed for `url`. Registering it speculatively risks regressing the
host-process consumer case that works today. Instead the resource keeps its current shape and a
`BeforeStartEvent` guard detects a container consumer holding an `EndpointReferenceAnnotation` that
targets a `ServiceUrlResource`, and throws a `ServiceSourcesConfigurationException` naming the
service, its source and the upstream issues — replacing the raw DCP stack trace (#58 option 3).

### `IServiceBuilder` — the return type

```csharp
public interface IServiceBuilder : IResourceBuilder<IResourceWithServiceDiscovery>;
```

`AddService` returns `IServiceBuilder`, backed by an internal `ServiceBuilder` wrapping the real
resource builder. Because it is a distinct interface, the configuration methods below bind only to
an `AddService` result and never shadow Aspire's own (finding 6). Because it still *is* an
`IResourceBuilder<IResourceWithServiceDiscovery>`, every consumer-side `WithReference(service)` and
`service.GetEndpoint(...)` call keeps working unchanged.

### Configuration API (#53)

A capability cannot be expressed in the static type, because the underlying resource may be any
Aspire type including a satellite's (finding 7). So the methods runtime-cast and delegate:

```csharp
var backend = builder.AddService("backend")
    .WithReference(planningDb)
    .WithEnvironment("DBPASSWORD", postgres.Resource.PasswordParameter)
    .WithEnvironment("Services__CommonAuth", commonAuth.GetEndpoint("https"))
    .WaitForCompletion(migrationService)
    .WithHttpsEndpoint();          // Aspire's own — already compiles, now actually takes effect
```

Mirrored: the `WithReference`, `WithEnvironment`, `WaitFor`, `WaitForCompletion` and `WithArgs`
overloads exercised by real AppHosts. Plus one general escape hatch that never needs updating as
Aspire's API grows, and reaches satellite-specific extensions too:

```csharp
public static IResourceBuilder<T> As<T>(this IServiceBuilder service) where T : IResource;

backend.As<JavaScriptAppResource>().WithRunScript("dev");
```

Both throw `ServiceSourcesConfigurationException` naming the service, its configured source and the
missing capability when the resolved resource cannot accept the call — for example configuring a
`url`-sourced service. This is a runtime error, not a compile-time one; that is the unavoidable
price of a single return type over heterogeneous resource types.

### Eager resolution with parallel checkouts preserved

`AddService` must return a real resource, so `local` resolution can no longer wait for
`BeforeStartEvent`. Parallel git checkouts are preserved by moving the trigger rather than removing
it: the **first** `AddService` call prefetches, in parallel, the checkout for every service in
`servicesources.local.json` whose `source` is `local`. Subsequent calls find their checkout already
resolved. Wall-clock stays `max(checkout)`, exactly as today.

`PendingLocalResolutions` is replaced by a `LocalCheckoutPrefetch` coordinator with the same
`ConditionalWeakTable`-per-builder keying. `AddProject(name, realPath)` is then called normally, so
all eight of `WithProjectDefaults`' annotations come for free and cannot drift with Aspire versions
(finding 5).

Prefetch is speculative, so it must not invent failures:

- A service listed in `servicesources.local.json` but absent from `servicesources.yaml` is **skipped**
  during prefetch; `AddService` still reports it if actually requested.
- A checkout failure is **captured, not thrown**, and re-thrown only when that service is requested.

### Consequences accepted

- **Cross-service failure aggregation is lost.** Today `BeforeStartEvent` reports every local
  service's failure in one message. With eager resolution `AddService("a")` must throw before
  `AddService("b")` is reached, so each service reports its own failure. This is inherent to
  returning a real resource and cannot be recovered without reintroducing the facade.
- **`ILocalResourceKind.Validate`'s "before *any* service touches the app model" guarantee is
  lost**, for the same reason. Its contract doc is updated to say it runs before that service's own
  resource is created.
- **Satellite kind registration must precede the first `AddService` call.** The unregistered-kind
  error message says so.
- **`AddService`'s return type changes** from `IResourceBuilder<IResourceWithServiceDiscovery>` to
  `IServiceBuilder`. Source-compatible for `var` callers; breaking for explicitly-typed locals.
  Acceptable pre-1.0 (v0.2.0 has no meaningful download count).

## Testing

- Unit: each source returns a registered resource present in `builder.Resources`; the container and
  kubernetes resources satisfy `IResourceWithServiceDiscovery`; a container consumer's
  `WithReference` against each source resolves.
- Unit: every mirrored method lands the expected annotation on the real resource; each throws a
  `ServiceSourcesConfigurationException` naming the service for a `url`-sourced service.
- Unit: `As<T>()` returns a working builder for a matching type and throws for a mismatch.
- Unit: prefetch resolves multiple `local` services in parallel; a catalog-missing service is
  skipped; a checkout failure surfaces only when that service is requested.
- Unit: the `BeforeStartEvent` guard throws a ServiceSources error for a container consumer of a
  `url` service, and stays silent for a host-process consumer.
- Regression: the existing `AddServiceIntegrationTests` continue to pass against real resources.
