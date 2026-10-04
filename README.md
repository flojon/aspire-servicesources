# Aspire.Hosting.ServiceSources

[![NuGet](https://img.shields.io/nuget/v/KoalaSoft.Aspire.Hosting.ServiceSources?logo=nuget&label=nuget)](https://www.nuget.org/packages/KoalaSoft.Aspire.Hosting.ServiceSources)
[![Downloads](https://img.shields.io/nuget/dt/KoalaSoft.Aspire.Hosting.ServiceSources?logo=nuget&label=downloads)](https://www.nuget.org/packages/KoalaSoft.Aspire.Hosting.ServiceSources)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](https://github.com/flojon/aspire-servicesources/blob/main/LICENSE)
[![Docs](https://img.shields.io/readthedocs/aspire-servicesources)](https://aspire-servicesources.readthedocs.io/)

An Aspire AppHost extension that lets `builder.AddService("orders")` resolve to a real,
running resource whose *source* is chosen per developer, not baked into the AppHost.

The AppHost says *what* it depends on; each developer's own config file decides *where* it comes
from — a managed git checkout (`"repository"`), a directory already checked out beside the AppHost
(`"path"`), a `kubectl port-forward` against a dev cluster (`"kubernetes"`), a fixed URL
(`"url"`), a published container image (`"container"`), or nothing at all (`"disabled"`). The
AppHost code never changes when a developer switches.

**📖 Documentation: <https://aspire-servicesources.readthedocs.io/>**

## Install

```bash
dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources
```

Requires .NET 8+ and Aspire 13.6.0+ — move your AppHost's `Aspire.AppHost.Sdk` to 13.6.0 or later
at the same time, or you get a mixed Aspire family. Services in other languages need one extra
package each. See
[Installation](https://aspire-servicesources.readthedocs.io/en/latest/getting-started/installation/).

## Quickstart

**1. Declare the catalog and the services** in the AppHost's `Program.cs`:

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

**2. Pick your source** in `servicesources.local.json` next to the AppHost project (gitignore this
file — it's per-developer):

```json
{
  "services": {
    "orders": { "source": "repository" },
    "storefront": { "source": "repository" }
  }
}
```

Run the AppHost: each service is cloned into `.servicesources/checkouts/<service>/`, checked out at
`main`, and run by Aspire, with `storefront` wired to `orders` through service discovery. Switch
`orders` to a dev cluster, a URL or a container by editing only `servicesources.local.json`. The
full walkthrough is in the
[Quickstart](https://aspire-servicesources.readthedocs.io/en/latest/getting-started/quickstart/);
prefer a committed `servicesources.yaml` over code? See
[The YAML catalog](https://aspire-servicesources.readthedocs.io/en/latest/guides/yaml-catalog/).

## Learn more

- [Sources](https://aspire-servicesources.readthedocs.io/en/latest/sources/) — every way to resolve a service.
- [Authoring the catalog in code](https://aspire-servicesources.readthedocs.io/en/latest/guides/catalog-in-code/) — the full catalog API, in C# and TypeScript.
- [Non-.NET services](https://aspire-servicesources.readthedocs.io/en/latest/guides/non-dotnet-services/) — JavaScript, Java, or your own `kind`.
- [Backing services](https://aspire-servicesources.readthedocs.io/en/latest/guides/backing-services/) — databases, brokers and caches.
- [Samples](https://github.com/flojon/aspire-servicesources/tree/main/samples) — runnable C# and TypeScript AppHosts.

## Status

Early stage, evolving fast; while below `1.0.0`, a minor release can break. See the
[changelog](https://github.com/flojon/aspire-servicesources/blob/main/CHANGELOG.md),
[`SECURITY.md`](https://github.com/flojon/aspire-servicesources/blob/main/SECURITY.md) for the
trust model a resolved service runs under, and
[`RELEASING.md`](https://github.com/flojon/aspire-servicesources/blob/main/RELEASING.md) for how a
release is cut. Licensed under [MIT](https://github.com/flojon/aspire-servicesources/blob/main/LICENSE).
