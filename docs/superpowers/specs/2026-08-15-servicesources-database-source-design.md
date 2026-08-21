# Aspire.Hosting.ServiceSources — Dependency Source Design

**Status:** Draft — revised 2026-08-21 against `main` (see Revision Notes). Expect further revision once this is prototyped against a real service that consumes a database.
**Date:** 2026-08-15 (revised 2026-08-21)
**Scope:** Extends the local-vs-kubernetes source-switching model from services to the backing resources a service connects to: databases (Postgres, SQL Server) and, on exactly the same mechanism, message brokers and caches (RabbitMQ, Redis, …). The mechanism is connection-string-based and backend-agnostic — see [Generalization](#generalization-beyond-databases), where this is verified rather than assumed. Closes out the "Database/queue source switching" item from the [phase 2 reference doc](2026-08-09-servicesources-phase2-future-work.md) and [issue #10](https://github.com/flojon/aspire-servicesources/issues/10).

## Revision Notes

The first draft of this document was written against the tree at PR #12 and has been corrected against current `main`. Four premises changed:

- **`"cluster"` is called `"kubernetes"`.** The source key, the class (`KubernetesSource`), and its helper (`KubernetesSource.BuildPortForwardArgs`) all use the `kubernetes` name. This document now does too.
- **`local` service resolution is deferred, not synchronous.** `LocalProjectSource.Resolve()` no longer creates the `AddProject` builder; it creates the facade and queues a `PendingResolution`, and `PendingLocalResolutions.ResolveAllAsync` calls `builder.AddProject(...)` from a `BeforeStartEvent` handler. The `local:` callback design below is rewritten around this.
- **`IResourceBuilder<T>` *is* covariant** (`IResourceBuilder<out T>`). The first draft asserted the opposite and built an argument on it. Verified by compiling `IResourceBuilder<IResourceWithConnectionString> x = b.AddPostgres("pg").AddDatabase("orders");` and `IResourceBuilder<IResourceWithServiceDiscovery> p = b.AddProject(...);` against Aspire 13.4.2 — both are legal implicit conversions, no cast or `CreateResourceBuilder` re-view needed.
- **There is now a `"container"` source**, which — like `"local"` — produces a real, locally-running, registered resource. It gets the same treatment as `"local"` throughout.

## Problem

Milestone 1a and the [kubernetes source design](2026-08-13-servicesources-cluster-source-design.md) let a developer switch a *service* between a local checkout and an already-running instance in a shared Kubernetes dev cluster. Services commonly depend on a database or broker, and a developer needs the same choice for it: run it locally (an Aspire-managed container, or one they already run themselves) or connect to the cluster's copy (directly through an ingress/gateway, or via `kubectl port-forward` when no ingress exists).

This is a different shape of problem than the service case, for three reasons:

1. **The abstraction must carry a connection string, not just service-discovery endpoints.** `IServiceSource.Resolve()`'s return type (`IResourceBuilder<IResourceWithServiceDiscovery>`) doesn't fit — `WithReference()` for a database needs `IResourceWithConnectionString`.
2. **`WithReference()`/`WaitFor()` only work on a resource Aspire actually manages the lifecycle of.** `ServiceResource` (the facade `AddService()` returns) is deliberately never registered, so any builder-extension call chained onto it already silently does nothing — existing, documented milestone-1a behavior, not something new here. Dependency wiring for a service can therefore only happen against the *real* underlying builder, inside the resolution path. That restricts it to the two sources that produce a real, locally-running resource whose environment we control: `"local"` (an `AddProject` resource) and `"container"` (an `AddContainer` resource). A `"kubernetes"`-sourced service is an already-running remote pod — setting env vars on the local `kubectl port-forward` executable would not reach it — and a `"url"`-sourced service has no resource at all.
3. **Local provisioning shouldn't be reinvented.** Unlike services (where the catalog owns `repository`/`project` so `AddService()` can build the local case itself), local provisioning of a database or broker (image/version, extra config) already exists as ordinary Aspire code (`builder.AddPostgres(...)`, `builder.AddRabbitMQ(...)`) in the AppHost. The `"local"` source should wrap that, not replace it.

## Architecture

### `AddDependency()`

```csharp
public static IResourceBuilder<IResourceWithConnectionString> AddDependency(
    this IDistributedApplicationBuilder builder,
    string name,
    Func<IResourceBuilder<IResourceWithConnectionString>> local)
```

(On the method name, and why it isn't `AddDatabase`, see [Naming](#naming).)

Resolves `name`'s developer config (local.json only — see Config Schema) and dispatches on `source`:

- **`"local"`** (default when no entry, or `"source": "local"`) — invokes the caller-supplied `local` factory as-is and returns its result. No catalog, no provisioning logic of our own — this is exactly `builder.AddPostgres("orders-pg").AddDatabase("orders")` or similar, written by the AppHost author like any other Aspire resource.
- **`"external"`** — builds a `ReferenceExpression` from the config's `connectionString` (after placeholder substitution — see Templating) and calls Aspire's own `ConnectionStringBuilderExtensions.AddConnectionString(builder, name, expression)`, which returns a real `IResourceBuilder<ConnectionStringResource>`. Covers both a manually-run local instance and a cluster database reachable directly through an ingress/gateway — from Aspire's perspective these are identical: "connect to this host:port," no process to manage.
- **`"kubernetes"`** — same `AddConnectionString(...)` mechanism as `"external"`, but first allocates a local port (`IPortAllocator`, existing seam) and adds a `kubectl port-forward` `AddExecutable(...)` (same shape as `KubernetesSource`), then substitutes `{port}` in the connection-string template with the allocated port before building the expression.

Called once per logical dependency; the returned builder is reused across every consumer, exactly like vanilla Aspire (`var db = builder.AddPostgres(...); a.WithReference(db); b.WithReference(db);`) — no caching/memoization needed on our side.

**No facade class is needed here, and no type gymnastics either.** `IResourceBuilder<out T>` is covariant, so every branch's concrete builder (`PostgresDatabaseResource`, `RabbitMQServerResource`, `ConnectionStringResource`, …) converts implicitly to the declared `IResourceBuilder<IResourceWithConnectionString>` return type. This is worth stating plainly because the first draft claimed the opposite and proposed a `CreateResourceBuilder<IResourceWithConnectionString>(resource)` re-view to work around it; that workaround is unnecessary.

> The same correction weakens — but does not by itself invalidate — the stated rationale for `ServiceResource`'s existence, which cites non-covariance. `ServiceResource` has other reasons to exist (deferred local resolution needs a handle to hand back before the real resource exists; the `"url"` source has no underlying resource at all). Re-examining it is out of scope here, but it should not be cited as precedent for a covariance workaround.

### `AddService(..., local: ...)`

```csharp
public static IResourceBuilder<IResourceWithServiceDiscovery> AddService(
    this IDistributedApplicationBuilder builder,
    string name,
    Action<IResourceBuilder<IResourceWithEnvironment>>? configure = null)
```

`configure` is a configuration callback, not a factory. It is invoked by the two sources that produce a real, locally-running resource, against that real builder:

- **`ContainerSource`** invokes it synchronously inside `Resolve()`, against the `AddContainer` builder, before `ServiceResource.CreateFacade(...)` wraps it.
- **`LocalProjectSource`** *cannot* invoke it inside `Resolve()`, because as of the deferred-resolution work there is no project builder at that point. `Resolve()` creates the facade and queues a `PendingResolution`; the real `builder.AddProject(...)` call happens later, in `PendingLocalResolutions.ResolveAllAsync` under `BeforeStartEvent`. The callback must therefore be carried on the `PendingResolution` record and invoked there, immediately after `AddProject` returns and alongside the existing `CopyEndpointAnnotations` call.

`KubernetesSource` and `UrlSource` receive the same parameter (broadening `IServiceSource.Resolve()`'s signature, as anticipated in issue #10) and never invoke it. This makes "dependency wiring only applies to a locally-running service" a structural fact rather than a caveat the AppHost author has to remember.

**The parameter type is widened to `IResourceWithEnvironment`, not `ProjectResource`,** so that one callback serves both `ProjectResource` and `ContainerResource`. Covariance makes both concrete builders convert implicitly, with no cast at the call site. One wrinkle: `WithReference` constrains to `IResourceWithEnvironment` but `WaitFor` constrains to `IResourceWithWaitSupport`, and no interface in Aspire combines the two (`IComputeResource`, the nearest common ancestor of `ProjectResource` and `ContainerResource`, does not derive from `IResourceWithEnvironment` — verified by compiler error CS0311). A small package-local shim closes the gap:

```csharp
// Safe by construction: the only sources that invoke the callback resolve to
// ProjectResource / ContainerResource, both of which implement IResourceWithWaitSupport.
public static IResourceBuilder<IResourceWithEnvironment> WaitFor(
    this IResourceBuilder<IResourceWithEnvironment> builder,
    IResourceBuilder<IResourceWithConnectionString> dependency)
{
    var waitable = (IResourceWithWaitSupport)builder.Resource;
    builder.ApplicationBuilder.CreateResourceBuilder(waitable).WaitFor(dependency);
    return builder;
}
```

With that shim in place, `s => s.WithReference(db).WaitFor(db)` compiles and runs unchanged for both a project-sourced and a container-sourced service (verified against Aspire 13.4.2).

A callback is chosen over a plain `dependencies: IResourceBuilder<IResourceWithConnectionString>[]` list because a list can only ever mean "call `.WithReference()` on each" — a callback lets the caller compose whatever's needed (`.WithReference(db).WaitFor(db)`, `.WithEnvironment(...)`, etc.) with ordinary Aspire chaining.

```csharp
var ordersDb = builder.AddDependency("orders-db",
    local: () => builder.AddPostgres("orders-pg").AddDatabase("orders"));

var events = builder.AddDependency("orders-events",
    local: () => builder.AddRabbitMQ("rabbit"));

var orders = builder.AddService("orders",
    configure: s => s.WithReference(ordersDb).WaitFor(ordersDb).WithReference(events));

var migrator = builder.AddService("orders-migrator",
    configure: s => s.WithReference(ordersDb).WaitFor(ordersDb));
```

#### Ordering constraint

Because the `"local"` service path now runs its callback under `BeforeStartEvent`, the dependency builder passed into that callback must be fully constructed by then. For `"local"`/`"external"` dependencies this is automatic (they are built during `AddDependency()`). It also means `AddDependency` must not itself defer, or the two deferral schedules would need ordering between them — a good reason to keep dependency resolution synchronous at `Add`-time even where the *value* it produces is deferred (see Templating).

### Naming

`AddDatabase` — the first draft's name — is not a compile conflict: Aspire's `AddDatabase` is an extension on `IResourceBuilder<PostgresServerResource>` / `IResourceBuilder<SqlServerServerResource>`, while ours would be on `IDistributedApplicationBuilder`. But it reads badly, because the two appear on the same line in the common case:

```csharp
builder.AddDatabase("orders-db", local: () => builder.AddPostgres("pg").AddDatabase("orders"));
//      ^ ours                                                          ^ Aspire's
```

It is also simply wrong once the same mechanism carries RabbitMQ and Redis (see below).

**No candidate name is ruled out by the compiler.** Aspire's existing `IDistributedApplicationBuilder` members were enumerated by reflection, and each competing name was then tried as an actual extension method with our proposed signature: `AddConnectionString`, `AddExternalService`, and `AddResource` all compile alongside the Aspire members they share a name with, and Aspire's own versions stay callable. Our signature `(string name, Func<IResourceBuilder<IResourceWithConnectionString>> local)` matches none of theirs, so overload resolution separates them cleanly. (`AddResource` is an *instance* member of the interface, `AddResource<T>(T resource)`; an instance method only suppresses an extension method when it is applicable to the call, which a one-argument `IResource` overload never is here.)

So the decision is entirely about readability, and the meaningful axis is how badly a name pollutes an existing overload set:

| Name | Verdict |
|---|---|
| `AddDependency` | **Recommended.** The only candidate that introduces a name Aspire.Hosting does not already use anywhere, so it adds nothing to an existing overload set. Pairs naturally with `AddService`; accurate for every backend. |
| `AddConnection` | Viable alternative, also unused by Aspire. Slightly narrower reading ("a connection" vs "a thing you connect to"). |
| `AddBackingService` | Unused by Aspire, but collides conceptually with our own `AddService`, which means something quite different. |
| `AddDatabase` | Legal, and on a *different* receiver type from Aspire's — but the two land on adjacent lines in the common case (above), and the name stops being true for brokers and caches. |
| `AddResource` | Legal, but the worst of the group: same receiver type as Aspire's, so both appear in one IntelliSense list on `builder.` with unrelated meanings, and a wrong-arity call produces an overload-resolution error mentioning a method the author never meant to call. |
| `AddConnectionString` | Legal, same objection as `AddResource` — same receiver, and here the two meanings are close enough to genuinely mislead ("returns a connection string resource" vs "builds one from a template"). |
| `AddExternalService` | Legal, but it means an external *HTTP* service in Aspire, and ours would cover a local Postgres container. Actively misleading. |

This document uses `AddDependency` throughout. The `"external"` source *key* is worth a second look for the same reason the `AddExternalService` row is: Aspire already uses "external" for something narrower, and the service-side equivalent of this source is already called `"url"`.

## Generalization Beyond Databases

**The design carries non-database backends with no mechanism changes at all.** Every branch of `AddDependency` deals only in `IResourceWithConnectionString`, `ReferenceExpression`, a TCP port, and a `kubectl port-forward` — none of which know what protocol is on the wire. Verified by compiling the proposed `local` factory type against four backends unchanged:

```csharp
Func<IResourceBuilder<IResourceWithConnectionString>> pg     = () => b.AddPostgres("pg").AddDatabase("orders");
Func<IResourceBuilder<IResourceWithConnectionString>> mssql  = () => b.AddSqlServer("sql").AddDatabase("orders");
Func<IResourceBuilder<IResourceWithConnectionString>> rabbit = () => b.AddRabbitMQ("rabbit");
Func<IResourceBuilder<IResourceWithConnectionString>> redis  = () => b.AddRedis("cache");
```

All four compile against Aspire 13.4.2. The `"external"` and `"kubernetes"` branches are equally indifferent — `amqp://user:pass@localhost:{port}/` and `localhost:{port},password=…` are just connection-string templates like any other.

Two caveats, neither structural:

- **Connection-string syntax varies**, so the whole-string-rewrite option in Open Questions gets *worse* the more backends are in scope (ADO.NET `Host=`/`Port=` vs. AMQP/Redis URI authority sections). This argues for the per-field-placeholder approach.
- **Multi-port backends** (RabbitMQ's AMQP 5672 plus management 15672) need one port-forward per port if both are wanted. The current shape allocates one `{port}` per dependency; a second dependency entry is the workaround, or `{port:<name>}` if this turns out to matter.

The practical consequence is naming, not architecture: hence `AddDependency` over `AddDatabase`, and `dependencies:` over `databases:` in config.

## Config Schema

`servicesources.yaml` (catalog) is **not touched** by this design. For services, catalog data is load-bearing for every source, including `"kubernetes"`. For dependencies, catalog data would only ever be consulted for `"kubernetes"` (`service`, remote `port`) — two fields, thin enough that per-developer duplication in local.json (already accepted for the far larger `connectionString` field) isn't a meaningful cost, and it keeps this pass smaller: no changes to catalog parsing at all, only local-config parsing gains a `dependencies:` section. If duplication proves painful in practice, a catalog override can be layered in later without breaking this shape.

`servicesources.local.json` — new `dependencies:` section, parallel to `services:`:

```json
{
  "dependencies": {
    "orders-db": {
      "source": "kubernetes",
      "service": "orders-pg",
      "port": 5432,
      "context": "dev-west",
      "namespace": "orders",
      "connectionString": "Host=localhost;Port={port};Database=orders;Username=dev;Password={secret:orders-creds:password}"
    }
  }
}
```

- `source`: `"local"` (default if the entry or field is omitted), `"external"`, or `"kubernetes"`.
- `service`, `port`: the k8s Service name and remote port to forward to. `"kubernetes"` only.
- `context`, `namespace`: same meaning as the existing service kubernetes source. Required for `"kubernetes"`; also usable by `"external"` purely for secret lookups (see Templating) even without port-forwarding.
- `connectionString`: required for `"external"` and `"kubernetes"`. A literal connection string, optionally containing placeholders resolved as described below.

New config model classes: `DependencyDeveloperConfig` (`Source`, `Service`, `Port`, `Context`, `Namespace`, `ConnectionString`), loaded through the existing `ServiceSourcesConfigCache` alongside `services:`. Field validation should reuse the `ServiceDeveloperConfigValidator` pattern (per-source `RelevantFields`, reject leftovers) that `main` already applies to services.

## Templating

Two placeholder kinds inside `connectionString`:

- **`{port}`** — the locally-allocated port from `IPortAllocator`, substituted as a literal during `AddDependency()`. Meaningful (and required) only for `"kubernetes"`.
- **`{secret:<name>:<key>}`** — a Kubernetes secret value, fetched via `kubectl get secret <name> -n <namespace> --context <context> -o jsonpath='{.data.<key>}'` and base64-decoded, through a new `IKubernetesSecretReader` seam (mirrors `IGitClient`/`IPortAllocator` — fake-able in unit tests, no real `kubectl` invocation there). Usable by both `"external"` and `"kubernetes"`.

### Secret fetches are deferred, not synchronous

The first draft resolved `{secret:...}` synchronously during `AddDependency()`. That is the wrong default: it runs a `kubectl` process during AppHost construction, on the same code path that `main` deliberately moved *off* of when local project resolution became deferred and parallel. It also fails the whole AppHost at construction time when a developer is merely not logged into the cluster.

**Aspire supports deferral directly, and it is a better fit.** Each `{secret:...}` placeholder becomes a `ParameterResource` created with the lazy-callback overload, interpolated into the `ReferenceExpression` rather than substituted as text:

```csharp
var password = builder.AddParameter($"{name}-{secretName}-{key}",
    () => secretReader.Read(context, ns, secretName, key), secret: true);

var expr = ReferenceExpression.Create(
    $"Host=localhost;Port={localPort.ToString()};Database=orders;Username=dev;Password={password.Resource}");

var dependency = builder.AddConnectionString(name, expr);
```

Verified behavior against Aspire 13.4.2:

- The callback is **not** invoked by `AddParameter(...)`, nor by `AddConnectionString(...)` — zero invocations at construction time.
- It fires on first resolution of the connection-string expression, at app start.
- Aspire **memoizes** it: resolving the same expression twice invoked the callback exactly once, so N consumers of one dependency produce one `kubectl` call, not N.
- `secret: true` also gets dashboard masking for free.

Consequences for the rest of the design: the connection string is assembled at `AddDependency()` time as a `ReferenceExpression` (structure fixed early, values late), `{port}` stays an eager literal substitution (the port is known synchronously), and secret-fetch failures surface at start time as a failed parameter resolution rather than as a constructor throw. The `IKubernetesSecretReader` seam is synchronous (`Func<string>` is the only callback shape `AddParameter` offers), so a fetch blocks one start-time resolution; that is acceptable, but the reader should carry its own timeout rather than inheriting `kubectl`'s default.

This mechanism reuses one path for both "just the password is secret" (`Password={secret:orders-creds:password}`) and, for `"external"`, "the whole connection string is one secret value" (`connectionString: "{secret:orders-full-cs:connectionString}"`). See Open Questions for why the whole-string case doesn't extend cleanly to `"kubernetes"`.

## Error Handling

Fail fast at `AddDependency()`-call time, naming the dependency and the missing field — same philosophy as the existing service sources:

- `"kubernetes"` missing `service`, `port`, `context`, or `connectionString`.
- `"external"` missing `connectionString`.
- A `{port}` placeholder present for a source where it isn't resolvable, or a `{secret:...}` placeholder with no `context`/`namespace` to resolve it against.
- A malformed placeholder (e.g. `{secret:name}` with no key) — caught by parsing at `Add`-time even though the *fetch* is deferred.

Runtime errors (`kubectl` not on `PATH`, secret not found, invalid context) surface at app start: for the port-forward, through the `ExecutableResource`'s own state/logs; for a secret fetch, as a failed `ParameterResource` resolution, which Aspire reports against that parameter in the dashboard. The error message should name the dependency, the secret, and the key, since the parameter name alone won't be obvious to the developer.

## Testing

- Config parsing: new `dependencies:` section, each fail-fast path, leftover-field rejection.
- Placeholder handling: `{port}` literal substitution and `{secret:name:key}` → `ParameterResource` wiring, including multiple placeholders and mixed use, via fake `IPortAllocator`/`IKubernetesSecretReader` — no real socket or `kubectl` calls.
- **Deferral:** assert the fake secret reader is *not* called during `AddDependency()`, is called on first expression resolution, and is called exactly once across repeated resolutions.
- Source dispatch: `"local"` invokes the given factory and nothing else; `"external"`/`"kubernetes"` build the expected `ConnectionStringResource`; `"kubernetes"` additionally builds the expected port-forward `AddExecutable` args (reusing `KubernetesSource.BuildPortForwardArgs`-style coverage).
- `AddService(configure:)`: the callback fires for `ContainerSource` inside `Resolve()`; fires for `LocalProjectSource` only after `BeforeStartEvent` has run, against the real `AddProject` builder; and is never invoked by `KubernetesSource` or `UrlSource`.
- The `WaitFor` shim: exercised through both a project-sourced and a container-sourced service, asserting the cast never throws.

## Open Questions

**Does `WithReference`/`WaitFor` still take effect when applied under `BeforeStartEvent`?** This is the highest-risk unknown in the design and it is a direct consequence of deferred local resolution. `PendingLocalResolutions` already calls `builder.AddProject(...)` that late and Aspire evidently builds and runs the result, but env-var injection and wait-graph construction may be read at different points in the start sequence than resource registration. If `WaitFor` in particular is processed before `BeforeStartEvent` handlers complete, the callback would silently do nothing for `"local"` services — the exact failure mode `ServiceResource`'s facade already has, reintroduced one layer down. **This must be resolved by prototype before the rest of the design is worth building**; if it doesn't hold, the options are to move the callback to an earlier event, to have `LocalProjectSource` register a placeholder project resource eagerly, or to reconsider deferred resolution for services that declare dependencies.

**Kubernetes + whole-connection-string-in-secret.** For `"kubernetes"`, a secret holding the *entire* connection string doesn't work as-is: the value was written for in-cluster use and bakes in the real Service host:port, not our dynamically-allocated `localhost:{port}`. Fetching it verbatim produces a connection string that bypasses the port-forward tunnel entirely. Options, none chosen yet:

- Restrict `"kubernetes"` to per-field secret placeholders only and document that a whole-string secret is an `"external"`-only pattern. Simplest, but pushes a real constraint onto the developer's secret layout, which they may not control. The [generalization](#generalization-beyond-databases) argument favors this one, since parsing gets harder the more backends are supported.
- Parse and rewrite the host/port portion of the fetched string before use. Reintroduces the backend-specific parsing (Postgres `Host=`/`Port=` vs. SQL Server `Server=`/`,1433` vs. AMQP/Redis URIs) that "full connection string as one field" was meant to avoid.
- Bind the port-forward to the exact remote port number found in the fetched string instead of an `IPortAllocator`-chosen free port, so no rewrite is needed. Reintroduces local port-collision risk that `IPortAllocator` exists to prevent. Also now impossible to do at `Add`-time, since the secret is deferred and the port is needed early.
- Something else entirely — worth revisiting once this is tried against a real cluster secret shape.

**Whether `servicesources.yaml` should be involved at all.** This draft keeps all dependency config in local.json, reasoning that catalog data would only ever matter for `"kubernetes"` and be thin (`service`/`port`). But since `connectionString` can be secret-backed via `{secret:name:key}` placeholders rather than embedding literal credentials, the *template itself* may contain no actual secret material. If so, it (along with `service`/`port`) could live in the catalog as shared, committed, team-wide data instead of being hand-copied into every developer's local.json, mirroring the services split (catalog = shared identity, local.json = per-developer environment choice) more closely than this draft does. Worth revisiting once it's clear how often a team's `connectionString` template really is secret-free versus genuinely varying per developer (e.g. a personal username).

**`WaitFor` readiness for the port-forward tunnel.** `.WaitFor(ordersDb)` targets the `ConnectionStringResource`, which has no process/health state of its own — it's unclear whether this actually delays a consumer until the underlying `kubectl port-forward` executable is listening, or whether an explicit dependency needs wiring between the two. `KubernetesSource` already accepts a small, documented TOCTOU-style race for port allocation; the same posture (accept and document) may be the right default, but should be confirmed against a real port-forward.

**Multi-port backends.** Whether `{port:<name>}` is worth adding for cases like RabbitMQ's AMQP + management ports, or whether a second dependency entry is an acceptable workaround. Defer until someone asks.
