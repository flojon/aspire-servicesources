# Aspire.Hosting.ServiceSources — Backing Service Source Design

**Status:** Draft — revised 2026-08-22 against `main` at #62, which removed the `ServiceResource` facade and shipped `Configure<T>`/`As<T>`; the proposed `AddService(configure:)` parameter and `WaitFor` shim are withdrawn as a result, and `AddBackingService` is now the design's only new public surface. Earlier questions were settled by prototype and against a `kind` cluster; the open items are a guest-language gap introduced by #51 and two team decisions. See Revision Notes.
**Date:** 2026-08-15 (revised 2026-08-21, 2026-08-22)
**Scope:** Extends the local-vs-kubernetes source-switching model from services to the backing resources a service connects to: databases (Postgres, SQL Server) and, on exactly the same mechanism, message brokers and caches (RabbitMQ, Redis, …). The mechanism is connection-string-based and backend-agnostic — see [Generalization](#generalization-beyond-databases), where this is verified rather than assumed. Closes out the "Database/queue source switching" item from the [phase 2 reference doc](2026-08-09-servicesources-phase2-future-work.md) and [issue #10](https://github.com/flojon/aspire-servicesources/issues/10).

## Revision Notes

### 2026-08-22 — rebased onto #62 and the work merged with it

`main` has moved substantially since the previous revision, and #62 in particular removed the foundation two sections of this design were built on. Changes that matter here:

- **The `ServiceResource` facade is gone (#62, closing #53 and #58).** Every source now returns the real, registered resource — `ProjectResource`, `ServiceContainerResource`, `ServiceExecutableResource`, or whatever a satellite kind produces. `ServiceResource.cs` no longer exists. The "any builder-extension call chained onto it silently does nothing" premise is obsolete.
- **`Configure<T>()` / `As<T>()` already ship** (`ServiceConfigurationExtensions`). This supersedes the `configure:` callback parameter this design proposed for `AddService`, and the `WaitFor` shim that went with it. Both are deleted below; no change to `AddService` or `IServiceSource.Resolve()` is needed any more.
- **Reachability is per capability *and* source**, via `IsUnreachable<T>` and `ServiceSourceAnnotation`, with skip-and-log rather than silent loss. Notably `Configure<IResourceWithWaitSupport>` **is** honoured for `"kubernetes"`, which contradicts this design's earlier claim that backing-service wiring only ever matters for locally-running services.
- **Local resolution is synchronous again.** `PendingLocalResolutions` is gone, replaced by `LocalCheckoutPrefetch`: clones for every `"local"` service start together on the first `AddService`, but `Resolve()` blocks on its own checkout and calls `builder.AddProject(...)` inline. The `BeforeStartEvent` ordering analysis below is therefore moot — retained only as a record of what was measured.
- **`ILocalResourceKind` (#41/#55)** means a `"local"` service can resolve to a resource type this package has never heard of. Anything this design says about "the two resource types the callback may see" no longer holds; capability-based `Configure<T>` handles it.
- **Guest-language AppHosts (#51, and `ServiceConfigurationExports`)** consume this package through ATS, where generic methods lose their type parameter and overloads are silently dropped. That is a new constraint on `AddBackingService`'s surface — see [Guest-language exports](#guest-language-exports).

### Earlier — corrections against the tree at PR #12

The first draft of this document was written against the tree at PR #12 and was corrected against `main` as it then stood. Four premises changed:

- **`"cluster"` is called `"kubernetes"`.** The source key, the class (`KubernetesSource`), and its helper (`KubernetesSource.BuildPortForwardArgs`) all use the `kubernetes` name. This document now does too.
- **`local` service resolution is deferred, not synchronous.** `LocalProjectSource.Resolve()` no longer creates the `AddProject` builder; it creates the facade and queues a `PendingResolution`, and `PendingLocalResolutions.ResolveAllAsync` calls `builder.AddProject(...)` from a `BeforeStartEvent` handler. The `local:` callback design below is rewritten around this.
- **`IResourceBuilder<T>` *is* covariant** (`IResourceBuilder<out T>`). The first draft asserted the opposite and built an argument on it. Verified by compiling `IResourceBuilder<IResourceWithConnectionString> x = b.AddPostgres("pg").AddDatabase("orders");` and `IResourceBuilder<IResourceWithServiceDiscovery> p = b.AddProject(...);` against Aspire 13.4.2 — both are legal implicit conversions, no cast or `CreateResourceBuilder` re-view needed.
- **There is now a `"container"` source**, which — like `"local"` — produces a real, locally-running, registered resource. It gets the same treatment as `"local"` throughout.

## Problem

Milestone 1a and the [kubernetes source design](2026-08-13-servicesources-cluster-source-design.md) let a developer switch a *service* between a local checkout and an already-running instance in a shared Kubernetes dev cluster. Services commonly depend on a database or broker, and a developer needs the same choice for it: run it locally (an Aspire-managed container, or one they already run themselves) or connect to the cluster's copy (directly through an ingress/gateway, or via `kubectl port-forward` when no ingress exists).

This is a different shape of problem than the service case, for three reasons:

1. **The abstraction must carry a connection string, not just service-discovery endpoints.** `IServiceSource.Resolve()`'s return type (`IResourceBuilder<IResourceWithServiceDiscovery>`) doesn't fit — `WithReference()` for a database needs `IResourceWithConnectionString`.
2. **Whether the AppHost's wiring reaches the service depends on that service's source.** Since #62 every source but `"url"` returns a real, registered resource, so there is always something to configure — but what that configuration *reaches* differs. Environment given to a `"kubernetes"`-sourced service lands on the local `kubectl port-forward` process, never on the pod behind it; a `"url"`-sourced service has no registered resource at all. This is no longer this design's problem to solve: `Configure<T>` already skips-and-warns per capability and source (see [Consuming a backing service](#consuming-a-backing-service-nothing-new-needed)). It does mean a backing-service reference is not uniformly meaningful across sources, and the design must not pretend otherwise.
3. **Local provisioning shouldn't be reinvented.** Unlike services (where the catalog owns `repository`/`project` so `AddService()` can build the local case itself), local provisioning of a database or broker (image/version, extra config) already exists as ordinary Aspire code (`builder.AddPostgres(...)`, `builder.AddRabbitMQ(...)`) in the AppHost. The `"local"` source should wrap that, not replace it.

## Architecture

### `AddBackingService()`

```csharp
[AspireExportIgnore]   // the `local` callback cannot cross ATS — see Guest-language exports
public static IResourceBuilder<IResourceWithConnectionString> AddBackingService(
    this IDistributedApplicationBuilder builder,
    [ResourceName] string name,
    Func<IResourceBuilder<IResourceWithConnectionString>> local)
```

`[ResourceName]` matches `AddService` and lets Aspire's own naming analyzers (enabled package-wide in #61) validate call sites.

(On the method name, and why it isn't `AddDatabase`, see [Naming](#naming).)

Resolves `name`'s developer config (local.json only — see Config Schema) and dispatches on `source`:

- **`"local"`** (default when no entry, or `"source": "local"`) — invokes the caller-supplied `local` factory as-is and returns its result. No catalog, no provisioning logic of our own — this is exactly `builder.AddPostgres("orders-pg").AddDatabase("orders")` or similar, written by the AppHost author like any other Aspire resource.
- **`"external"`** — builds a `ReferenceExpression` from the config's `connectionString` (after placeholder substitution — see Templating) and calls Aspire's own `ConnectionStringBuilderExtensions.AddConnectionString(builder, name, expression)`, which returns a real `IResourceBuilder<ConnectionStringResource>`. Covers both a manually-run local instance and a cluster database reachable directly through an ingress/gateway — from Aspire's perspective these are identical: "connect to this host:port," no process to manage.
- **`"kubernetes"`** — same `AddConnectionString(...)` mechanism as `"external"`, but first allocates a local port (`IPortAllocator`, existing seam) and adds a `kubectl port-forward` `AddExecutable(...)` (same shape as `KubernetesSource`), then substitutes `{port}` in the connection-string template with the allocated port before building the expression. **It must also attach a TCP health check on that local port via `.WithHealthCheck(...)`** — without it a consumer's `.WaitFor(...)` does not actually wait for the tunnel, which was measured, not assumed (see [Resolved by Prototype](#resolved-by-prototype)).

Called once per logical backing service; the returned builder is reused across every consumer, exactly like vanilla Aspire (`var db = builder.AddPostgres(...); a.WithReference(db); b.WithReference(db);`) — no caching/memoization needed on our side.

**No facade class is needed here, and no type gymnastics either.** `IResourceBuilder<out T>` is covariant, so every branch's concrete builder (`PostgresDatabaseResource`, `RabbitMQServerResource`, `ConnectionStringResource`, …) converts implicitly to the declared `IResourceBuilder<IResourceWithConnectionString>` return type. This is worth stating plainly because the first draft claimed the opposite and proposed a `CreateResourceBuilder<IResourceWithConnectionString>(resource)` re-view to work around it; that workaround is unnecessary.

> This correction has since been overtaken by events: #62 removed `ServiceResource` entirely, on the grounds that the only thing the facade bought — `ContainerResource`/`ExecutableResource` not implementing `IResourceWithServiceDiscovery` — is better solved by subclassing, which is Aspire's own integration pattern. Nothing in this package now depends on a covariance workaround.

### Consuming a backing service: nothing new needed

Earlier drafts proposed adding a `configure:` callback parameter to `AddService`, plus a `WaitFor` shim to bridge a constraint gap. **All of that is superseded by #62**, which removed the `ServiceResource` facade, made every source return the real registered resource, and shipped a general configuration mechanism. The design now consumes what already exists:

```csharp
var ordersDb = builder.AddBackingService("orders-db",
    local: () => builder.AddPostgres("orders-pg").AddDatabase("orders"));

var events = builder.AddBackingService("orders-events",
    local: () => builder.AddRabbitMQ("rabbit"));

builder.AddService("orders")
    .Configure<IResourceWithEnvironment>(r => r.WithReference(ordersDb).WithReference(events))
    .Configure<IResourceWithWaitSupport>(r => r.WaitFor(ordersDb));

builder.AddService("orders-migrator")
    .Configure<IResourceWithEnvironment>(r => r.WithReference(ordersDb))
    .Configure<IResourceWithWaitSupport>(r => r.WaitFor(ordersDb));
```

This is strictly better than the callback parameter, for reasons the earlier draft could not have reached:

- **It covers resource types this design has never heard of.** A `"local"` service may resolve through a satellite `ILocalResourceKind` to any Aspire integration's resource. A callback typed on `IResourceWithEnvironment` would have worked for those too, but `Configure<T>` also reaches `As<JavaScriptAppResource>().WithRunScript("dev")`-style, kind-specific extensions.
- **The `WaitFor` shim is unnecessary.** The constraint gap it existed to bridge (`WithReference` needs `IResourceWithEnvironment`, `WaitFor` needs `IResourceWithWaitSupport`, and no Aspire interface combines them) is answered by naming the capability per call instead of finding one type that satisfies both.
- **Reachability is already handled, and more precisely than "structural fact".** `Configure<T>` skips-and-logs when the capability cannot reach the service behind its source, and `IsUnreachable<T>` is keyed on the capability *as well as* the source.

That last point changes a conclusion in the Problem section. The earlier framing — backing-service wiring is meaningful only for locally-running services — is **half wrong**:

| Service source | `Configure<IResourceWithEnvironment>` (`WithReference`) | `Configure<IResourceWithWaitSupport>` (`WaitFor`) |
|---|---|---|
| `local`, `container` | applied | applied |
| `kubernetes` | skipped + warned — would reach `kubectl`, not the service | **applied** — holding the port-forward back is exactly what was asked |
| `url` | skipped + warned — nothing registered | skipped + warned |

So `.WaitFor(ordersDb)` written against a local service **survives** a developer switching that service to `"kubernetes"`, which is precisely the property the package exists to protect. `WithReference` does not survive, and is warned about rather than silently dropped.

**No change to `AddService` or `IServiceSource.Resolve()` is required by this design.** The only new public surface is `AddBackingService` itself.

#### Ordering constraint

`AddBackingService(...)` must be called before the `AddService(...)` whose `Configure` references it — ordinary C# variable ordering, nothing more. Since #62, `LocalProjectSource.Resolve()` calls `builder.AddProject(...)` synchronously (blocking on a checkout that `LocalCheckoutPrefetch` started in parallel on the first `"local"` `AddService`), so there is no deferred phase to sequence against.

### Naming

`AddDatabase` — the first draft's name — is not a compile conflict: Aspire's `AddDatabase` is an extension on `IResourceBuilder<PostgresServerResource>` / `IResourceBuilder<SqlServerServerResource>`, while ours would be on `IDistributedApplicationBuilder`. But it reads badly, because the two appear on the same line in the common case:

```csharp
builder.AddDatabase("orders-db", local: () => builder.AddPostgres("pg").AddDatabase("orders"));
//      ^ ours                                                          ^ Aspire's
```

It is also simply wrong once the same mechanism carries RabbitMQ and Redis (see below).

**No candidate name is ruled out by the compiler.** Aspire's existing `IDistributedApplicationBuilder` members were enumerated by reflection, and each competing name was then tried as an actual extension method with our proposed signature: `AddConnectionString`, `AddExternalService`, and `AddResource` all compile alongside the Aspire members they share a name with, and Aspire's own versions stay callable. Our signature `(string name, Func<IResourceBuilder<IResourceWithConnectionString>> local)` matches none of theirs, so overload resolution separates them cleanly. (`AddResource` is an *instance* member of the interface, `AddResource<T>(T resource)`; an instance method only suppresses an extension method when it is applicable to the call, which a one-argument `IResource` overload never is here.)

So the decision is entirely about readability, and the meaningful axis is how badly a name collides with vocabulary an AppHost author already has in scope:

| Name | Verdict |
|---|---|
| `AddBackingService` | **Chosen.** Unused anywhere in Aspire, so it adds nothing to an existing overload set. Names the thing on its own terms — a backing service in the 12-factor sense — which suits an API whose result is declared standalone and only later referenced. Accurate for every backend. Caveat below. |
| `AddDependency` | Runner-up, also unused by Aspire. Reads slightly oddly at the declaration site (`var ordersDb = builder.AddDependency(...)` — a dependency *of what?*), since nothing has referenced it yet. |
| `AddConnection` | Viable, also unused. Slightly narrower reading ("a connection" vs "a thing you connect to"). |
| `AddInfra` / `AddInfrastructure` | Good semantic fit, but "infrastructure" is taken vocabulary: `Aspire.Hosting.Azure` ships `AddAzureInfrastructure`, `ConfigureInfrastructure` and `AzureResourceInfrastructure`, where it means the Azure/Bicep provisioning object model (verified by reflection; core `Aspire.Hosting` has no `Infra*` members). In the very common AppHost that references the Azure package, ours would read as a sibling of `AddAzureInfrastructure` while meaning something unrelated. `AddInfra` is additionally the only clipped name on `builder.` — Aspire's house style spells things out (`AddContainerRegistry`, `AddCertificateAuthorityCollection`). |
| `AddDatabase` | Legal, and on a *different* receiver type from Aspire's — but the two land on adjacent lines in the common case (above), and the name stops being true for brokers and caches. |
| `AddResource` | Legal, but among the worst: same receiver type as Aspire's, so both appear in one IntelliSense list on `builder.` with unrelated meanings, and a wrong-arity call produces an overload-resolution error naming a method the author never meant to call. Also overpromises — the contract is `IResourceWithConnectionString`, not any resource. |
| `AddConnectionString` | Legal, same objection as `AddResource` — same receiver, and here the two meanings are close enough to genuinely mislead ("returns a connection string resource" vs "builds one from a template"). |
| `AddExternalService` | Legal, but it means an external *HTTP* service in Aspire, and ours would cover a local Postgres container. Actively misleading. |

**Caveat on the chosen name.** `Service` already means something specific in this package — `AddService()` is the source-switched *application* resource, and `servicesources.yaml` / the `services:` config section are about those. `AddBackingService` deliberately borrows that word for a different kind of thing, relying on the "backing" qualifier to separate them. This is accepted as a readability trade: the qualifier is doing real work in prose ("a service and the backing services it connects to"), and the two never appear as competing overloads. It does mean documentation should avoid the bare word "service" where either could be meant.

This document uses `AddBackingService` throughout, with `backingServices:` as the matching local.json section. The `"external"` source *key* is worth a second look for the same reason the `AddExternalService` row is: Aspire already uses "external" for something narrower, and the service-side equivalent of this source is already called `"url"`.

### Guest-language exports

#51 exports `AddService` to TypeScript AppHosts through ATS, and #62 had to add non-generic `ServiceConfigurationExports` shims because ATS drops a generic method's type parameter and silently keeps only the first overload of a name. `AddBackingService` inherits a harder version of that problem: **its `local` parameter is a `Func<>` returning a resource builder, and a callback cannot cross the ATS boundary at all.**

The consequences, none of which have been prototyped:

- The `"external"` and `"kubernetes"` branches are fine — they need no callback, only config, and produce a `ConnectionStringResource` like any other.
- The `"local"` branch has no guest-language equivalent as designed. A TypeScript AppHost cannot pass a lambda that calls `builder.AddPostgres(...)`.
- The obvious answer is to make the local case *declarative for guest languages only* — image, tag, port in the catalog, the way the `"container"` service source already works — which reopens the [config-schema question](#config-schema) this design deliberately closed by keeping the catalog untouched.

This is a genuinely new constraint introduced after the design was written, and it is the largest piece of unfinished work remaining. Left as an open question rather than answered here.

## Generalization Beyond Databases

**The design carries non-database backends with no mechanism changes at all.** Every branch of `AddBackingService` deals only in `IResourceWithConnectionString`, `ReferenceExpression`, a TCP port, and a `kubectl port-forward` — none of which know what protocol is on the wire. Verified by compiling the proposed `local` factory type against four backends unchanged:

```csharp
Func<IResourceBuilder<IResourceWithConnectionString>> pg     = () => b.AddPostgres("pg").AddDatabase("orders");
Func<IResourceBuilder<IResourceWithConnectionString>> mssql  = () => b.AddSqlServer("sql").AddDatabase("orders");
Func<IResourceBuilder<IResourceWithConnectionString>> rabbit = () => b.AddRabbitMQ("rabbit");
Func<IResourceBuilder<IResourceWithConnectionString>> redis  = () => b.AddRedis("cache");
```

All four compile against Aspire 13.4.2. The `"external"` and `"kubernetes"` branches are equally indifferent — `amqp://user:pass@localhost:{port}/` and `localhost:{port},password=…` are just connection-string templates like any other.

Two caveats, neither structural:

- **Connection-string syntax varies**, so the whole-string-rewrite option in Open Questions gets *worse* the more backends are in scope (ADO.NET `Host=`/`Port=` vs. AMQP/Redis URI authority sections). This argues for the per-field-placeholder approach.
- **Multi-port backends** (RabbitMQ's AMQP 5672 plus management 15672) need one port-forward per port if both are wanted. The current shape allocates one `{port}` per backing service; a second entry is the workaround, or `{port:<name>}` if this turns out to matter.

The practical consequence is naming, not architecture: hence `AddBackingService` over `AddDatabase`, and `backingServices:` over `databases:` in config.

## Config Schema

`servicesources.yaml` (catalog) is **not touched** by this design. For services, catalog data is load-bearing for every source, including `"kubernetes"`. For backing services, catalog data would only ever be consulted for `"kubernetes"` (`service`, remote `port`) — two fields, thin enough that per-developer duplication in local.json (already accepted for the far larger `connectionString` field) isn't a meaningful cost, and it keeps this pass smaller: no changes to catalog parsing at all, only local-config parsing gains a `backingServices:` section. If duplication proves painful in practice, a catalog override can be layered in later without breaking this shape.

`servicesources.local.json` — new `backingServices:` section, parallel to `services:`:

```json
{
  "backingServices": {
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

New config model classes: `BackingServiceDeveloperConfig` (`Source`, `Service`, `Port`, `Context`, `Namespace`, `ConnectionString`), loaded through the existing `ServiceSourcesConfigCache` alongside `services:`. Field validation should reuse the `ServiceDeveloperConfigValidator` pattern (per-source `RelevantFields`, reject leftovers) that `main` already applies to services.

## Templating

Two placeholder kinds inside `connectionString`:

- **`{port}`** — the locally-allocated port from `IPortAllocator`, substituted as a literal during `AddBackingService()`. Meaningful (and required) only for `"kubernetes"`.
- **`{secret:<name>:<key>}`** — a Kubernetes secret value, fetched via `kubectl get secret <name> -n <namespace> --context <context> -o jsonpath='{.data.<key>}'` and base64-decoded, through a new `IKubernetesSecretReader` seam (mirrors `IGitClient`/`IPortAllocator` — fake-able in unit tests, no real `kubectl` invocation there). Usable by both `"external"` and `"kubernetes"`.

### Secret fetches are deferred, not synchronous

The first draft resolved `{secret:...}` synchronously during `AddBackingService()`. That is the wrong default: it runs a `kubectl` process during AppHost construction, on the same code path that `main` deliberately moved *off* of when local project resolution became deferred and parallel. It also fails the whole AppHost at construction time when a developer is merely not logged into the cluster.

**Aspire supports deferral directly, and it is a better fit.** Each `{secret:...}` placeholder becomes a `ParameterResource` created with the lazy-callback overload, interpolated into the `ReferenceExpression` rather than substituted as text:

```csharp
var password = builder.AddParameter($"{name}-{secretName}-{key}",
    () => secretReader.Read(context, ns, secretName, key), secret: true);

var expr = ReferenceExpression.Create(
    $"Host=localhost;Port={localPort.ToString()};Database=orders;Username=dev;Password={password.Resource}");

var backingService = builder.AddConnectionString(name, expr);
```

Verified behavior against Aspire 13.4.2:

- The callback is **not** invoked by `AddParameter(...)`, nor by `AddConnectionString(...)` — zero invocations at construction time.
- It fires on first resolution of the connection-string expression, at app start.
- Aspire **memoizes** it: resolving the same expression twice invoked the callback exactly once, so N consumers of one backing service produce one `kubectl` call, not N.
- `secret: true` also gets dashboard masking for free.

Consequences for the rest of the design: the connection string is assembled at `AddBackingService()` time as a `ReferenceExpression` (structure fixed early, values late), `{port}` stays an eager literal substitution (the port is known synchronously), and secret-fetch failures surface at start time as a failed parameter resolution rather than as a constructor throw. The `IKubernetesSecretReader` seam is synchronous (`Func<string>` is the only callback shape `AddParameter` offers), so a fetch blocks one start-time resolution; that is acceptable, but the reader should carry its own timeout rather than inheriting `kubectl`'s default.

This mechanism reuses one path for both "just the password is secret" (`Password={secret:orders-creds:password}`) and, for `"external"`, "the whole connection string is one secret value" (`connectionString: "{secret:orders-full-cs:connectionString}"`). See Open Questions for why the whole-string case doesn't extend cleanly to `"kubernetes"`.

## Error Handling

Fail fast at `AddBackingService()`-call time, naming the backing service and the missing field — same philosophy as the existing service sources:

- `"kubernetes"` missing `service`, `port`, `context`, or `connectionString`.
- `"external"` missing `connectionString`.
- A `{port}` placeholder present for a source where it isn't resolvable, or a `{secret:...}` placeholder with no `context`/`namespace` to resolve it against.
- A malformed placeholder (e.g. `{secret:name}` with no key) — caught by parsing at `Add`-time even though the *fetch* is deferred.
- Whole-string mode selected but the local port matching the configured remote `port` is already in use — named explicitly, since this mode deliberately bypasses `IPortAllocator`.

Runtime errors (`kubectl` not on `PATH`, secret not found, invalid context) surface at app start: for the port-forward, through the `ExecutableResource`'s own state/logs; for a secret fetch, as a failed `ParameterResource` resolution, which Aspire reports against that parameter in the dashboard. The error message should name the backing service, the secret, and the key, since the parameter name alone won't be obvious to the developer.

## Testing

- Config parsing: new `backingServices:` section, each fail-fast path, leftover-field rejection.
- Placeholder handling: `{port}` literal substitution and `{secret:name:key}` → `ParameterResource` wiring, including multiple placeholders and mixed use, via fake `IPortAllocator`/`IKubernetesSecretReader` — no real socket or `kubectl` calls.
- **Deferral:** assert the fake secret reader is *not* called during `AddBackingService()`, is called on first expression resolution, and is called exactly once across repeated resolutions.
- Source dispatch: `"local"` invokes the given factory and nothing else; `"external"`/`"kubernetes"` build the expected `ConnectionStringResource`; `"kubernetes"` additionally builds the expected port-forward `AddExecutable` args (reusing `KubernetesSource.BuildPortForwardArgs`-style coverage).
- Consumption through the shipped `Configure<T>`: `Configure<IResourceWithEnvironment>(r => r.WithReference(db))` reaches a `"local"`- and a `"container"`-sourced service, and is skipped-and-warned for `"kubernetes"`/`"url"`; `Configure<IResourceWithWaitSupport>(r => r.WaitFor(db))` is additionally honoured for `"kubernetes"`. These assert existing behaviour against a backing-service argument rather than testing new code, and exist to catch a regression in the interaction.
- The `WaitFor` shim: exercised through both a project-sourced and a container-sourced service, asserting the cast never throws.
- `"kubernetes"` attaches a health check annotation to the returned resource (regression guard for the `WaitFor` gap found by prototype).
- Whole-string mode: a template that is exactly one `{secret:...}` placeholder selects same-port forwarding; the resolved value has every in-cluster host form (`<service>`, `.<namespace>`, `.svc`, `.svc.cluster.local`) replaced and the port left untouched; a mixed template does not select the mode; an occupied local port fails fast with the backing service and port named.
- End-to-end, in the style of `AddServiceIntegrationTests`: a `"local"`-sourced service configured with `.Configure<IResourceWithEnvironment>(r => r.WithReference(db))` has `ConnectionStrings__<name>` in its materialised environment.

## Resolved by Prototype

Both questions that blocked this design have been answered by running a real AppHost (Aspire 13.4.6, DCP, Linux) rather than by reasoning about the lifecycle. The probes are described here so the results can be re-checked when Aspire changes.

**Applying `WithReference`/`WaitFor` under `BeforeStartEvent` works.** *(Moot as of #62 — `PendingLocalResolutions` is gone and local resolution is synchronous again. Kept as a record of what was measured, in case deferred registration returns.)* This was the highest-risk unknown at the time: `PendingLocalResolutions` registered the project resource from a `BeforeStartEvent` handler, and if Aspire had already read env-var and wait annotations by then, the callback would silently do nothing for `"local"` services.

Probe: a `ConnectionStringResource` registered normally, and a consumer executable added *from inside* a `BeforeStartEvent` handler with `.WithReference(db).WaitFor(db)`. After `StartAsync`, the consumer reached `Running` with `ConnectionStrings__orders-db=Host=localhost;Port=5432;Database=orders` present in its materialised environment. Annotations applied that late are honoured; env-var injection happens well after `BeforeStartEvent`. The callback design was therefore sound — though #62 has since made the question academic by removing the deferred phase.

**`WaitFor` on a `ConnectionStringResource` does *not* gate on the port-forward tunnel — but a health check fixes it.** This was previously listed as "accept and document"; that would have been the wrong call, because the failure is silent and total rather than a small race.

Probe: a `tunnel` executable that only begins listening after 8 seconds, plus a `ConnectionStringResource` pointing at that port, plus a consumer with `.WaitFor(db)`. The connection-string resource reported `Running` at 3.4s — as soon as its template resolved, knowing nothing about the tunnel — and the consumer started at 3.4s too, roughly 5 seconds before anything was listening. A consumer that connects on startup would fail.

The fix, verified in a second run: attach a TCP health check to the resource the `"kubernetes"` branch returns.

```csharp
builder.Services.AddHealthChecks().AddCheck($"{name}-tcp", () => /* TcpClient connect to 127.0.0.1:localPort */);

var backingService = builder.AddConnectionString(name, expr)
    .WithHealthCheck($"{name}-tcp");
```

With the health check attached, the same consumer reached `Running` at 11.5s — i.e. it genuinely waited for the tunnel. `WaitFor` waits for Running *and* healthy, so this makes `.WaitFor(ordersDb)` mean what an AppHost author expects.

**This makes the health check a required part of the `"kubernetes"` branch, not an optional nicety** — without it, `WaitFor` on a kubernetes-sourced backing service is decorative. It should be added to the Architecture section's `"kubernetes"` bullet and covered by a test that asserts the health check annotation is present. The `"external"` branch has the same gap in principle (nothing is being waited on), but there the developer is pointing at something they already run, so a connectivity check is a convenience rather than a correctness fix; making it opt-in via a config flag is probably right, and is left open.

## Resolved Against a Real Cluster

A `kind` cluster (v0.30.0, Kubernetes in Docker) settled the two remaining *technical* questions. What is left after this needs a team decision, not an experiment.

### Whole-connection-string-in-secret: two supported modes

Both secret shapes occur in practice and both must work. Per-field is preferred where it exists; whole-string is supported through same-port forwarding.

**Operator-generated secrets carry both shapes.** CloudNativePG 1.24, deployed into `kind`, generates one `kubernetes.io/basic-auth` secret per cluster (`<cluster>-app`) with **nine** keys:

| Kind | Keys | Example value |
|---|---|---|
| Per-field | `host`, `port`, `dbname`, `username`, `user`, `password` | `orders-pg-rw`, `5432`, `orders`, `orders_app` |
| Whole-string | `uri`, `jdbc-uri`, `pgpass` | `postgresql://orders_app:…@orders-pg-rw.default:5432/orders` |

The whole-string keys confirm the original concern — `uri` bakes in the in-cluster address `orders-pg-rw.default:5432`, so fetching it verbatim bypasses the tunnel.

**But hand-authored secrets often carry only the whole string.** A team using [Sealed Secrets](https://github.com/bitnami-labs/sealed-secrets) commonly seals a single `connectionString` value, with no per-field keys to fall back on, and re-shaping it means re-sealing against the cluster's key and a commit to the GitOps repo — frequently owned by a platform team, not the developer. An earlier draft of this section restricted `"kubernetes"` to per-field placeholders on the grounds that operators supply them anyway; that reasoning does not survive contact with hand-authored sealed secrets, so it has been withdrawn.

Verified in `kind` with the sealed-secrets controller v0.27.1: a `SealedSecret` holding only `connectionString` decrypts into an **ordinary** `Secret`, read back byte-identically by the exact `kubectl get secret <name> -o jsonpath='{.data.<key>}'` command `IKubernetesSecretReader` uses. **Sealing changes nothing about the fetch** — only about the shape available.

#### Whole-string mode: same-port forwarding, host-token rewrite

The fix avoids connection-string parsing entirely. If the local port equals the remote port, the port needs no rewriting, and the *host* is a single unambiguous token — one literal string replacement, identical for every backend and dialect.

Verified end-to-end: the sealed `postgresql://orders_app:…@orders-pg-rw.default:5432/orders`, forwarded with `kubectl port-forward service/orders-pg-rw 5432:5432`, with `orders-pg-rw.default` replaced by the local host and nothing else touched, authenticated against the real database and returned `db=orders, usr=orders_app`.

Selection happens at `Add`-time from the *template shape*, which is local config and therefore known early even though the secret value is not: **if the whole `connectionString` template is exactly one `{secret:...}` placeholder, use whole-string mode.** Then:

- Allocate the local port as the *same number* as the configured remote `port`, bypassing `IPortAllocator`.
- Fail fast, naming the backing service and port, if that local port is already in use. This is the real cost of the mode — it gives up the collision avoidance `IPortAllocator` exists to provide — and a clear error is what makes it tolerable.
- At resolution time, replace every in-cluster form of the host with `localhost`: `<service>`, `<service>.<namespace>`, `<service>.<namespace>.svc`, and `<service>.<namespace>.svc.cluster.local`. All four are derived from config already present for the port-forward.

Anything richer stays rejected. **Parsing and rewriting the connection string per backend is not adopted** — it reintroduces the per-dialect grammars (`Host=`/`Port=`, `Server=host,1433`, URI authorities) that the single-field design exists to avoid, and the host-token replacement above achieves the same result without knowing the dialect.

**Per-field placeholders remain preferred** where the secret offers them: they keep `IPortAllocator`'s collision avoidance, which whole-string mode must give up.

### Multi-port backends: one port-forward, many ports

`kubectl port-forward` accepts **multiple port pairs in a single invocation**, against one Service, from one process. Verified against a two-port Service in `kind`:

```
kubectl port-forward service/broker 25672:5672 35672:15672
  Forwarding from 127.0.0.1:25672 -> 5672
  Forwarding from 127.0.0.1:35672 -> 15672
```

Both forwarded ports carried real traffic to their respective listeners. So the earlier suggestion — "a second backing-service entry is the workaround" — is unnecessary and would be actively worse, since two entries means two `kubectl` processes and two tunnels to the same Service.

**Decision: `port` accepts either a single port or a named map, and `{port:<name>}` resolves against it.** One `AddExecutable`, one process, one health check per forwarded port.

```json
"orders-events": {
  "source": "kubernetes",
  "service": "rabbitmq",
  "port": { "amqp": 5672, "management": 15672 },
  "connectionString": "amqp://dev:{secret:rabbit-creds:password}@localhost:{port:amqp}/"
}
```

The single-port form stays the common case and keeps `{port}` as a shorthand for it.

### End-to-end tunnel check

Port-forwarding the operator-created `orders-pg-rw` Service to a local port and TCP-connecting to it succeeded, confirming that the health-check gating fix from the previous section works against a real Kubernetes Service and not just the `nc` stand-in it was developed against.

## Open Questions

**How guest-language AppHosts declare a `"local"` backing service.** The `local` factory is a C# callback and cannot cross ATS, so as designed `AddBackingService` is C#-only for that source. See [Guest-language exports](#guest-language-exports). This is the largest open item and it interacts with the catalog question below: a declarative local spec would most naturally live in `servicesources.yaml`.

**Whether `servicesources.yaml` should be involved at all.** This draft keeps all backing-service config in local.json, reasoning that catalog data would only ever matter for `"kubernetes"` and be thin (`service`/`port`). But since `connectionString` can be secret-backed via `{secret:name:key}` placeholders rather than embedding literal credentials, the *template itself* may contain no actual secret material. If so, it (along with `service`/`port`) could live in the catalog as shared, committed, team-wide data instead of being hand-copied into every developer's local.json, mirroring the services split (catalog = shared identity, local.json = per-developer environment choice) more closely than this draft does. Worth revisiting once it's clear how often a team's `connectionString` template really is secret-free versus genuinely varying per developer (e.g. a personal username).

**Whether `"external"` should get an opt-in connectivity health check.** The `"kubernetes"` branch now requires one (see Resolved by Prototype). `"external"` points at something the developer already runs, so a check there is a convenience — it turns "connection refused, deep in your app's startup" into "this backing service is unhealthy" on the dashboard. Probably a `"healthCheck": true` config flag; not designed here.

**The name of the direct-connection source.** Currently `"external"`, which is the weakest name in the schema: Aspire already uses "external" for `AddExternalService` (an external *HTTP* service), and the service-side source meaning the same thing — "a fixed address of something already running that we do not manage" — is called `"url"`. Reusing `"url"` would maximise consistency but misdescribes an ADO.NET-style `Host=…;Port=…` string; `"existing"` or `"connectionString"` are accurate but break the parallel with the service-side vocabulary. Not settled.
