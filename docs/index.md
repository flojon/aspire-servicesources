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
config file decides how it's actually resolved — a managed git checkout (`"repository"`) or a
directory already checked out beside the AppHost (`"path"`), a `kubectl port-forward` against
a dev cluster (`"kubernetes"`), a fixed, already-known URL (`"url"`), a published container
image run locally (`"container"`), or nothing at all (`"disabled"`) — behind one stable return
type, so the AppHost code never has to change when a developer switches sources.

## Install

Published on nuget.org as [`KoalaSoft.Aspire.Hosting.ServiceSources`](https://www.nuget.org/packages/KoalaSoft.Aspire.Hosting.ServiceSources).
If every service your AppHost declares is a .NET project, this is the only package you need:

```bash
dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources
```

> **This package floors Aspire at 13.5.2, so an AppHost still on 13.4.x gets a mixed Aspire
> family.** NuGet takes the highest floor, so `Aspire.Hosting` is lifted to 13.5.2 while your
> `Aspire.AppHost.Sdk`, `Aspire.Hosting.AppHost` and the DCP and dashboard packages the SDK
> pins to it stay where they are. Nothing warns about it at restore. Move your AppHost's own
> Aspire version to 13.5.2 or later at the same time:
>
> ```xml
> <Sdk Name="Aspire.AppHost.Sdk" Version="13.5.2" />
> ```

Services that aren't .NET projects need Aspire's hosting package for their language,
referenced by your AppHost alongside this one — see
[Non-.NET local services](kinds.md):

| `kind` | Package | Minimum |
| --- | --- | --- |
| `java` | `CommunityToolkit.Aspire.Hosting.Java` | 13.3.0 |
| `javascript` | `Aspire.Hosting.JavaScript` | 13.5.2 |

```bash
dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources

# then one line per language the AppHost actually declares a service for
dotnet add package Aspire.Hosting.JavaScript              # kind: javascript
dotnet add package CommunityToolkit.Aspire.Hosting.Java   # kind: java
```

Add one per language you actually use, and nothing for a language you don't. This package
compiles against both but declares neither as a dependency, so an AppHost with no
`javascript` service never sees `Aspire.Hosting.JavaScript`, nor the Aspire floor it carries
— which is the point, because a mixed Aspire family restores clean and then throws
`TypeLoadException` on first resolve. The version is yours to choose: pick any release at or
above the minimum above, and the two need not match each other.

Forget one and you are told which. Below the minimum, your build fails naming the package and
the version it resolved — `SERVICESOURCES001` for `Aspire.Hosting.JavaScript`,
`SERVICESOURCES002` for `CommunityToolkit.Aspire.Hosting.Java`. A prerelease of the minimum
counts as below it. Missing entirely, the first `AddService()` for a service of that kind fails
with a message naming the package to install — which is the only report a guest-language AppHost
gets, since the Aspire CLI hands it this package through a project reference and a project
reference imports no build-time checks.

The build-time check also fires for a package that arrived transitively, from a graph your AppHost
does not control — where neither raising it nor removing it may be yours to do. Set
`ServiceSourcesSkipGuestLanguageFloorCheck=true` in that project to turn the check off; the version
problem is then reported at run time, by the service that needed it.

Requires .NET 8 or later (net8.0, net9.0, and net10.0 are all supported) and an AppHost
project using the `Aspire.AppHost.Sdk`. A service that isn't a .NET project needs Aspire's
hosting package for its language too — see [Non-.NET local services](kinds.md).

## Getting started

A service catalog is declared in the AppHost's own language — C# directly, or TypeScript
(and any other Aspire guest language) through Aspire's Type System — via
`AddServiceCatalog`, covered in full under
[Authoring the catalog in code](authoring-in-code.md). It can be declared in
`servicesources.yaml` instead; see [The yaml catalog](yaml-catalog.md) for that walkthrough.
Everything else on this site applies the same way regardless of which one you use.

**1. Declare the catalog and the service in `Program.cs`:**

```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddServiceCatalog(catalog =>
{
    catalog.AddService("orders")
        .WithRepository("https://github.com/example/orders", defaultRef: "main")
        .WithProject("src/Orders.Api/Orders.Api.csproj");
});

var orders = builder.AddService("orders");             // "orders" lives in a separate repository
var api = builder.AddProject<Projects.Api>("api")       // "api" is part of *this* AppHost's own solution
    .WithReference(orders);

builder.Build().Run();
```

`AddProject<Projects.Api>()` only works for a project `ProjectReference`d from the AppHost's own
`.csproj` — an external service like `orders` has no such reference to generate `Projects.Orders`
from, which is exactly what `AddService()` is for. (A service that isn't a .NET project also
takes a `WithKind`/`AsJava`/`AsJavaScript` call instead of `WithProject` — see
[Non-.NET local services](kinds.md).) `AddServiceCatalog` must be called before the first
`AddService(...)` call anywhere in the AppHost.

**2. Add your own `servicesources.local.json` next to the AppHost project (gitignore this
file — it's per-developer):**

```json
{
  "services": {
    "orders": { "source": "repository" }
  }
}
```

That's it — running the AppHost now clones `orders` into
`<AppHostDirectory>/.servicesources/checkouts/orders/`, checks out `main`, and runs it via
Aspire's own project orchestration, wired up to `api` through service discovery exactly like
a project reference would be.

## Documentation map

- [Authoring the catalog in code](authoring-in-code.md) — the full `AddServiceCatalog` API in
  C# and TypeScript: every source, `kind` options, `prepare`, and `defaultSource`.
- [The yaml catalog](yaml-catalog.md) — declare the same catalog in `servicesources.yaml`
  instead of code.
- [The `"repository"` and `"path"` sources](local-source.md) — managed git checkouts, a
  directory already checked out beside the AppHost, the `prepare` bootstrap step, why a
  checkout doesn't inherit your repository's build settings, and grouping several services
  under one repository.
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

Early stage, evolving fast. `"repository"`, `"path"`, `"kubernetes"`, `"url"`, `"container"`
and `"disabled"` sources are all implemented. Changes are recorded in the
[changelog](https://github.com/flojon/aspire-servicesources/blob/main/CHANGELOG.md); how a
release is cut is in
[`RELEASING.md`](https://github.com/flojon/aspire-servicesources/blob/main/RELEASING.md); the
trust model a resolved service runs under is in
[`SECURITY.md`](https://github.com/flojon/aspire-servicesources/blob/main/SECURITY.md).

Use the version selector in the corner to switch between **latest** (tracks `main`, may
contain unreleased changes) and **stable**/numbered releases.
