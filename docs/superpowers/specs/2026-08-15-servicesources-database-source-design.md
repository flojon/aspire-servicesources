# Aspire.Hosting.ServiceSources — Database Source Design

**Status:** Draft — first pass; expect revision once this is prototyped against a real service that consumes a database.
**Date:** 2026-08-15
**Scope:** Extends the local-vs-cluster source-switching model from services to databases (Postgres, SQL Server today; the mechanism is connection-string-based and DB-type-agnostic, so Redis/RabbitMQ could reuse it later without changes — not attempted here). Closes out the "Database/queue source switching" item from the [phase 2 reference doc](2026-08-09-servicesources-phase2-future-work.md) and [issue #10](https://github.com/flojon/aspire-servicesources/issues/10).

## Problem

Milestone 1a and the [cluster source design](2026-08-13-servicesources-cluster-source-design.md) let a developer switch a *service* between a local checkout and an already-running instance in a shared Kubernetes dev cluster. Services commonly depend on a database (Postgres, SQL Server), and a developer needs the same choice for it: run it locally (an Aspire-managed container, or one they already run themselves) or connect to the cluster's copy (directly through an ingress/gateway, or via `kubectl port-forward` when no ingress exists).

This is a different shape of problem than the service case, for three reasons surfaced during design:

1. **The abstraction needs must carry a connection string, not just service-discovery endpoints.** `IServiceSource.Resolve()`'s return type (`IResourceBuilder<IResourceWithServiceDiscovery>`) doesn't fit — `WithReference()` on a database needs `IResourceWithConnectionString`.
2. **`WithReference()`/`WaitFor()` only work on a resource Aspire actually manages the lifecycle of.** `ServiceResource` (the facade `AddService()` returns) is deliberately never registered, so any builder-extension call chained onto it already silently does nothing — this is existing, documented milestone-1a behavior, not something new to databases. It means database wiring for a service can only happen *inside* that service's own `IServiceSource.Resolve()`, against the real underlying builder, before it's wrapped in the facade. Consequently, database source selection is only ever meaningful for a `"local"`-sourced service (a real, registered `AddProject` resource) — a `"cluster"`-sourced service is an already-running remote pod whose environment we have no way to influence via a local `kubectl port-forward` executable's env vars, regardless of which database source was picked.
3. **Local provisioning shouldn't be reinvented.** Unlike services (where the catalog owns `repository`/`project` so `AddService()` can build the local case itself), database provisioning (image/version, extra config) already exists as ordinary Aspire code (`builder.AddPostgres(...)`) in the AppHost. The `"local"` source should wrap that, not replace it.

## Architecture

### `AddDatabase()`

```csharp
public static IResourceBuilder<IResourceWithConnectionString> AddDatabase(
    this IDistributedApplicationBuilder builder,
    string name,
    Func<IResourceBuilder<IResourceWithConnectionString>> local)
```

Resolves `name`'s developer config (local.json only — see Config Schema) and dispatches on `source`:

- **`"local"`** (default when no entry, or `"source": "local"`) — invokes the caller-supplied `local` factory as-is and returns its result. No catalog, no provisioning logic of our own — this is exactly `builder.AddPostgres("orders-pg").AddDatabase("orders")` or similar, written by the AppHost author like any other Aspire resource.
- **`"external"`** — builds a `ReferenceExpression` from the config's `connectionString` (after placeholder substitution — see Templating) and calls Aspire's own `ConnectionStringBuilderExtensions.AddConnectionString(builder, name, expression)`, which returns a real `IResourceBuilder<ConnectionStringResource>`. Covers both a manually-run local instance and a cluster database reachable directly through an ingress/gateway — from Aspire's perspective these are identical: "connect to this host:port," no process to manage.
- **`"cluster"`** — same `AddConnectionString(...)` mechanism as `"external"`, but first allocates a local port (`IPortAllocator`, existing seam) and adds a `kubectl port-forward` `AddExecutable(...)` (same shape as today's service `ClusterSource`), then substitutes `{port}` in the connection-string template with the allocated port before building the expression.

Called once per logical database; the returned builder is reused across every consumer, exactly like vanilla Aspire (`var db = builder.AddPostgres(...); a.WithReference(db); b.WithReference(db);`) — no caching/memoization needed on our side.

No custom facade class is needed here, unlike `ServiceResource`. `ServiceResource` exists because `AddProject`/`AddExecutable` return different concrete types and `IResourceBuilder<T>` isn't covariant, so unifying them required a genuinely new (deliberately unregistered) resource object with copied annotations. For databases, every branch already produces a resource implementing `IResourceWithConnectionString` — the only problem is the same covariance issue, which Aspire's own code solves by re-viewing an *already-registered* resource through a differently-typed builder handle: `builder.CreateResourceBuilder<IResourceWithConnectionString>(resource)`. This isn't a proxy; it's the same object, just accessed via a different generic-typed `IResourceBuilder<T>`. (Aspire's own `ParameterResourceBuilderExtensions.AddConnectionString` XML docs show this exact pattern for its `ConnectionStringParameterResource` surrogate.)

### `AddService(..., local: ...)`

```csharp
public static IResourceBuilder<IResourceWithServiceDiscovery> AddService(
    this IDistributedApplicationBuilder builder,
    string name,
    Action<IResourceBuilder<ProjectResource>>? local = null)
```

`local` is a configuration callback, not a factory — invoked only by `LocalProjectSource.Resolve()`, against the real `AddProject` builder, before it's wrapped in the `ServiceResource` facade. `ClusterSource.Resolve()` receives the same parameter (broadening `IServiceSource.Resolve()`'s signature, as anticipated in issue #10) and never calls it — not a no-op guarded against, just code that doesn't exist on that path. This makes "database wiring only applies to a local-sourced service" a structural fact, not a documented caveat the AppHost author has to remember.

Chosen over a plain `databases: IResourceBuilder<IResourceWithConnectionString>[]` list because a list can only ever mean "call `.WithReference()` on each" — a callback lets the caller compose whatever's needed (`.WithReference(db).WaitFor(db)`, `.WithEnvironment(...)`, etc.) with ordinary Aspire chaining.

Naming: `local` is reused for both `AddDatabase`'s factory and `AddService`'s callback deliberately — both mean "this only runs for the local source," just one builds and the other configures.

```csharp
var ordersDb = builder.AddDatabase("orders-db",
    local: () => builder.AddPostgres("orders-pg").AddDatabase("orders"));

var orders = builder.AddService("orders",
    local: s => s.WithReference(ordersDb).WaitFor(ordersDb));

var migrator = builder.AddService("orders-migrator",
    local: s => s.WithReference(ordersDb).WaitFor(ordersDb));
```

## Config Schema

`servicesources.yaml` (catalog) is **not touched** by this design. For services, catalog data is load-bearing for every source, including `"cluster"`. For databases, catalog data would only ever be consulted for `"cluster"` (`service`, remote `port`) — two fields, thin enough that per-developer duplication in local.json (already accepted for the far larger `connectionString` field) isn't a meaningful cost, and it keeps this pass smaller: no changes to catalog parsing at all, only local-config parsing gains a `databases:` section. If duplication proves painful in practice, a catalog override can be layered in later without breaking this shape.

`servicesources.local.json` — new `databases:` section, parallel to `services:`:

```json
{
  "databases": {
    "orders-db": {
      "source": "cluster",
      "service": "orders-pg",
      "port": 5432,
      "context": "dev-west",
      "namespace": "orders",
      "connectionString": "Host=localhost;Port={port};Database=orders;Username=dev;Password={secret:orders-creds:password}"
    }
  }
}
```

- `source`: `"local"` (default if the entry or field is omitted), `"external"`, or `"cluster"`.
- `service`, `port`: the k8s Service name and remote port to forward to. `"cluster"` only.
- `context`, `namespace`: same meaning as the existing service cluster source. Required for `"cluster"`; also usable by `"external"`/`"cluster"` purely for secret lookups (see Templating) even without port-forwarding.
- `connectionString`: required for `"external"` and `"cluster"`. A literal ADO.NET-style connection string, optionally containing placeholders resolved at `AddDatabase()` time.

New config model classes: `DatabaseDeveloperConfig` (`Source`, `Service`, `Port`, `Context`, `Namespace`, `ConnectionString`), loaded through the existing `ServiceSourcesConfigCache` alongside `services:`.

## Templating

Two placeholder kinds inside `connectionString`, both resolved synchronously during `AddDatabase()`:

- **`{port}`** — the locally-allocated port from `IPortAllocator`. Meaningful (and required) only for `"cluster"`.
- **`{secret:<name>:<key>}`** — fetched via `kubectl get secret <name> -n <namespace> --context <context> -o jsonpath='{.data.<key>}'`, base64-decoded, through a new `IKubernetesSecretReader` seam (mirrors `IGitClient`/`IPortAllocator` — fake-able in unit tests, no real `kubectl` invocation there). Usable by both `"external"` and `"cluster"`.

This reuses one mechanism for both "just the password is secret" (`Password={secret:orders-creds:password}`) and, for `"external"`, "the whole connection string is one secret value" (`connectionString: "{secret:orders-full-cs:connectionString}"`). See Open Questions for why the whole-string case doesn't extend cleanly to `"cluster"`.

## Error Handling

Fail fast at `AddDatabase()`-call time, naming the database and the missing field — same philosophy as the existing service sources:

- `"cluster"` missing `service`, `port`, `context`, or `connectionString`.
- `"external"` missing `connectionString`.
- A `{secret:...}` or `{port}` placeholder present in a `connectionString` for a source where it isn't resolvable.

Runtime errors (`kubectl` not on `PATH`, secret not found, invalid context) surface through the port-forward `ExecutableResource`'s own state/logs where applicable, or throw directly for the synchronous secret-fetch (which, unlike the port-forward process itself, has no separate resource identity to report through).

## Testing

- Config parsing: new `databases:` section, each fail-fast path.
- Placeholder substitution: `{port}` and `{secret:name:key}`, including multiple placeholders and mixed use, via fake `IPortAllocator`/`IKubernetesSecretReader` — no real socket or `kubectl` calls.
- Source dispatch: `"local"` invokes the given factory and nothing else; `"external"`/`"cluster"` build the expected `ConnectionStringResource`; `"cluster"` additionally builds the expected port-forward `AddExecutable` args (reusing `ClusterSource.BuildPortForwardArgs`-style coverage).
- `AddService(local:)`: the callback fires for `LocalProjectSource` against the real `AddProject` builder and is never invoked by `ClusterSource`.

## Open Questions

**Cluster + whole-connection-string-in-secret.** For `"cluster"`, a secret holding the *entire* connection string doesn't work as-is: the value was written for in-cluster use and bakes in the real Service host:port, not our dynamically-allocated `localhost:{port}`. Fetching it verbatim produces a connection string that bypasses the port-forward tunnel entirely. Options, none chosen yet — needs a prototyping pass against what these secrets actually look like in practice:

- Restrict `"cluster"` to per-field secret placeholders only (as in the example above) and document that a whole-string secret is an `"external"`-only pattern. Simplest, but pushes a real constraint onto the developer's secret layout, which they may not control.
- Parse and rewrite the host/port portion of the fetched string before use. Reintroduces the DB-type-specific connection-string parsing (Postgres `Host=`/`Port=` vs. SQL Server `Server=`/`,1433`, etc.) that choosing "full connection string as one field" was meant to avoid.
- Bind the port-forward to the exact remote port number found in the fetched string instead of an `IPortAllocator`-chosen free port, so no rewrite is needed. Reintroduces local port-collision risk that `IPortAllocator` exists to prevent.
- Something else entirely — worth revisiting once this is tried against a real cluster secret shape.

**`WaitFor` readiness for the port-forward tunnel.** `.WaitFor(ordersDb)` in an `AddService(local:)` callback targets the `ConnectionStringResource`, which has no process/health state of its own — it's unclear whether this actually delays a consumer until the underlying `kubectl port-forward` executable is listening, or whether the two need an explicit dependency wired between them. `ClusterSource` already accepts a small, documented TOCTOU-style race for port allocation; the same posture (accept and document) may be the right default here too, but should be confirmed once this is exercised against a real port-forward.
