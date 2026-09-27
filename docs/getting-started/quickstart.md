# Quickstart

A service catalog can be declared either in `servicesources.yaml` — the walkthrough below — or in
code via `AddServiceCatalog`, covered under
[Authoring the catalog in code](../guides/catalog-in-code.md). This walkthrough uses yaml because
it's the simplest on-ramp; everything else in these docs applies the same way regardless of which
one you use.

## 1. Declare the service in `Program.cs`

```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

var orders = builder.AddService("orders");
var api = builder.AddProject<Projects.Api>("api")
    .WithReference(orders);

builder.Build().Run();
```

## 2. Add the shared catalog

Create `servicesources.yaml` next to the AppHost project, and commit it:

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main          # optional; branch, tag, or commit SHA
```

A service that isn't a .NET project also takes a `kind` — see
[Non-.NET services](../guides/non-dotnet-services.md).

## 3. Pick your own source

Create `servicesources.local.json` next to it, and gitignore it — it's per-developer:

```json
{
  "services": {
    "orders": { "source": "repository" }
  }
}
```

That file is the base layer of the AppHost's own configuration — an environment variable or an
`appsettings.json` entry can override any of it for a single run, without an edit. See
[Overriding `servicesources.local.json`](../guides/configuration.md#overriding-servicesourceslocaljson).

## 4. Run it

That's it — running the AppHost now clones `orders` into
`<AppHostDirectory>/.servicesources/checkouts/orders/`, checks out `main`, and runs it via
Aspire's own project orchestration, wired up to `api` through service discovery exactly like
a project reference would be.

## Defaulting a source

A catalog entry may also declare `defaultSource`, so a service resolves without a
`servicesources.local.json` entry at all:

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main
    defaultSource: repository     # optional; the source a developer gets with no entry of their own
```

!!! warning "`defaultSource: repository` means every developer, and CI, clones and builds that repository by default"
    That includes a machine or pipeline that never explicitly asked for it. If CI should not
    clone, CI must pin its own source (an environment variable, or its own configuration layer)
    rather than relying on the absence of a file.

## Next steps

- [Sources overview](../sources/index.md) — every way a developer can resolve a service, and
  combining them on one catalog entry.
- [Samples](samples.md) — runnable AppHosts in C# and TypeScript.
- [Troubleshooting](../troubleshooting.md) — reading the errors this package raises.
