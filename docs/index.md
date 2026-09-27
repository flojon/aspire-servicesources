# Aspire.Hosting.ServiceSources

An Aspire AppHost extension that lets `builder.AddService("orders")` resolve to a real,
running resource whose *source* is chosen per developer, not baked into the AppHost.

## Why

`AddProject<T>()` assumes a service lives in the AppHost's own solution. In a real
microservice environment, services live in separate repositories, and different developers
want different things for the same service: clone it locally to edit, run it from an
already-checked-out working copy, reach an instance already running in a shared Kubernetes
dev cluster, hit a fixed URL, or just run a published container image. The AppHost should
only describe *what* it depends on; where that dependency actually comes from is a
per-developer choice, made without ever touching the AppHost's `.csproj`/`.sln`.

`AddService()` is the seam: the AppHost calls it once per service, and a developer-local
config file decides how it's actually resolved — a managed or self-managed local git
checkout (`"local"`), a `kubectl port-forward` against a dev cluster (`"kubernetes"`), a
fixed, already-known URL (`"url"`), a published container image run locally
(`"container"`), or nothing at all (`"disabled"`) — behind one stable return type, so the
AppHost code never has to change when a developer switches sources.

## Install

Published on nuget.org as [`KoalaSoft.Aspire.Hosting.ServiceSources`](https://www.nuget.org/packages/KoalaSoft.Aspire.Hosting.ServiceSources).

```bash
dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources
```

Requires .NET 8 or later (net8.0, net9.0, and net10.0 are all supported) and an AppHost
project using the `Aspire.AppHost.Sdk`. A service that isn't a .NET project needs Aspire's
hosting package for its language too — see [Non-.NET local services](kinds.md).

## Getting started

**1. Declare the service in `Program.cs`:**

```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

var orders = builder.AddService("orders");
var api = builder.AddProject<Projects.Api>("api")
    .WithReference(orders);

builder.Build().Run();
```

**2. Add the shared catalog, `servicesources.yaml`, next to the AppHost project (commit this
file):**

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main          # optional; branch, tag, or commit SHA
```

(A service that isn't a .NET project also takes a `kind` — see
[Non-.NET local services](kinds.md).)

**3. Add your own `servicesources.local.json` next to it (gitignore this file — it's
per-developer):**

```json
{
  "services": {
    "orders": { "source": "local" }
  }
}
```

That's it — running the AppHost now clones `orders` into
`<AppHostDirectory>/.servicesources/checkouts/orders/`, checks out `main`, and runs it via
Aspire's own project orchestration, wired up to `api` through service discovery exactly like
a project reference would be.

## Documentation map

- [Authoring the catalog in code](authoring-in-code.md) — declare the same catalog in C# or
  TypeScript instead of yaml, via `AddServiceCatalog`.
- [The `"local"` source](local-source.md) — managed git checkouts, the `prepare` bootstrap
  step, why a checkout doesn't inherit your repository's build settings, and grouping several
  services under one repository.
- [Non-.NET local services (`kind`)](kinds.md) — the built-in `javascript` and `java` kinds,
  and how to implement your own.
- [Other sources: `kubernetes`, `url`, `container`, `disabled`](other-sources.md) — a
  `kubectl port-forward` against a dev cluster, a fixed URL, a published container image, or
  turning a service off, plus combining several sources on one catalog entry.
- [Configuring and consuming a resolved service](configuration.md) — overriding
  `servicesources.local.json` from higher configuration layers, `ServiceResource`'s native
  Aspire vocabulary, and naming a service's endpoint portably across sources.
- [Backing services: databases, brokers and caches](backing-services.md) — `AddBackingService()`,
  reaching one through a cluster tunnel, and connection-string placeholders.

## Status

Early stage, evolving fast. `"local"`, `"kubernetes"`, `"url"`, `"container"` and `"disabled"`
sources are all implemented. Changes are recorded in the
[changelog](https://github.com/flojon/aspire-servicesources/blob/main/CHANGELOG.md); how a
release is cut is in
[`RELEASING.md`](https://github.com/flojon/aspire-servicesources/blob/main/RELEASING.md); the
trust model a resolved service runs under is in
[`SECURITY.md`](https://github.com/flojon/aspire-servicesources/blob/main/SECURITY.md).

You're viewing the **`{{ readthedocs.version }}`** build of these docs. Use the version
selector in the corner to switch between `latest` (tracks `main`, may contain unreleased
changes) and `stable`/numbered releases.
