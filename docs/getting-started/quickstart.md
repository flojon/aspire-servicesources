# Quickstart

This walkthrough wires two services that live in their own repositories — a `storefront` that
calls an `orders` API — into an AppHost. The catalog is declared in C# with `AddServiceCatalog`;
the same thing works from TypeScript, see [Authoring the catalog in code](../guides/catalog-in-code.md).

## 1. Declare the catalog and the services

In the AppHost's `Program.cs`:

```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddServiceCatalog(catalog =>
{
    catalog.AddService("orders")
        .WithRepository("https://github.com/example/orders", defaultRef: "main")
        .WithProject("src/Orders.Api/Orders.Api.csproj");

    catalog.AddService("storefront")
        .WithRepository("https://github.com/example/storefront", defaultRef: "main")
        .WithProject("src/Storefront/Storefront.csproj");
});

var orders = builder.AddService("orders");

builder.AddService("storefront")
    .WithReference(orders);

builder.Build().Run();
```

The catalog says *how* each service could be resolved. `AddService("orders")` is the AppHost's
dependency on it: a plain string name rather than a generated `Projects.*` type, because
`AddProject<T>()` only works for a project referenced from the AppHost's own `.csproj`, and these
live in other repositories.

`AddServiceCatalog` must be called before the first `AddService(...)`. A service that isn't a .NET
project takes `AsJavaScript`/`AsJava` instead of `WithProject` — see
[Non-.NET services](../guides/non-dotnet-services.md).

## 2. Pick your own source

Create `servicesources.local.json` next to the AppHost project, and gitignore it — it's
per-developer:

```json
{
  "services": {
    "orders": { "source": "repository" },
    "storefront": { "source": "repository" }
  }
}
```

That file is the base layer of the AppHost's own configuration — an environment variable or an
`appsettings.json` entry can override any of it for a single run, without an edit. See
[Overriding `servicesources.local.json`](../guides/configuration.md#overriding-servicesourceslocaljson).

## 3. Run it

Running the AppHost now clones each service into
`<AppHostDirectory>/.servicesources/checkouts/<service>/`, checks out `main`, and runs it via
Aspire's own project orchestration, with `storefront` wired to `orders` through service discovery
exactly like a project reference would be.

Switch `orders` to another source — a dev cluster, a fixed URL, a container image — by editing
only `servicesources.local.json`; `Program.cs` never changes. See the
[sources overview](../sources/index.md).

## Defaulting a source

A catalog entry may also declare a default source, so a service resolves without a
`servicesources.local.json` entry at all:

```csharp
catalog.AddService("orders")
    .WithRepository("https://github.com/example/orders", defaultRef: "main")
    .WithProject("src/Orders.Api/Orders.Api.csproj")
    .WithDefaultSource("repository");
```

!!! warning "A default of `repository` means every developer, and CI, clones and builds that repository"
    That includes a machine or pipeline that never explicitly asked for it. If CI should not
    clone, CI must pin its own source (an environment variable, or its own configuration layer)
    rather than relying on the absence of a file. A default of
    [`path`](../sources/path.md) carries no such cost.

## Next steps

- [Sources overview](../sources/index.md) — every way a developer can resolve a service.
- [The YAML catalog](../guides/yaml-catalog.md) — declare the catalog in `servicesources.yaml`
  instead of code.
- [Samples](samples.md) — runnable C# and TypeScript AppHosts.
- [Troubleshooting](../troubleshooting.md) — reading the errors this package raises.
