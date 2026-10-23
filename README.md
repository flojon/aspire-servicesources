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

Requires .NET 10+ and Aspire 13.6.1+ — move your AppHost's `Aspire.AppHost.Sdk` to 13.6.1 or later
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

## Using `AddService()` with existing AppHost helpers

`AddService()` returns an `IResourceBuilder<ServiceResource>`. `ServiceResource` does not inherit
`ProjectResource` — a service isn't always a project, and the type shouldn't reveal which source
resolved it — so a helper hard-typed to `IResourceBuilder<ProjectResource>` won't accept it. Two
approaches cover most cases.

**1. Widen your own helpers** to a generic constrained to the capabilities their body uses, instead
of a concrete resource type. A helper that only calls `WithReference`, `WaitFor` and `WithEnvironment`
needs nothing more than the interfaces behind them:

```csharp
static class AppHostExtensions
{
    // was: this IResourceBuilder<ProjectResource> app
    public static IResourceBuilder<T> AddOllama<T>(
        this IResourceBuilder<T> app,
        IResourceBuilder<IResourceWithServiceDiscovery> ollama,
        string model)
        where T : IResourceWithEnvironment, IResourceWithWaitSupport
    {
        return app.WithReference(ollama).WaitFor(ollama).WithEnvironment("OLLAMA_MODEL", model);
    }
}
```

This accepts a project, a container or a `ServiceResource`. `ServiceResource` implements
`IResourceWithServiceDiscovery`, `IResourceWithEnvironment`, `IResourceWithArgs`,
`IResourceWithEndpoints` and `IResourceWithWaitSupport`; parameters that take another resource
should use the same interface-typed form (`IResourceBuilder<IResourceWithServiceDiscovery>`) rather
than `IResourceBuilder<ProjectResource>`.

**2. Use `GetServiceEndpoint()` where an API takes only an `EndpointReference`.** Many integrations
accept the resource builder through an interface-typed overload, and those take a service as is
(Aspire.Hosting.Yarp's `AddCluster` has an `IResourceBuilder<IResourceWithServiceDiscovery>` overload).
Where one accepts only an `EndpointReference`, or you need the service's URL as a value,
`GetServiceEndpoint()` returns the `https` endpoint, else `http`, else the service's only endpoint —
whichever source resolved it:

```csharp
var commonAuth = builder.AddService("common-auth");

builder.AddProject<Projects.Web>("web")
    .WithEnvironment("Services__CommonAuth", commonAuth.GetServiceEndpoint());
```

It throws `ServiceSourcesConfigurationException` if the service exposes no endpoints, or several and
none named `http` or `https`; name the one you mean with `GetEndpoint("...")`. The endpoint is chosen
when you call it, so call it after any `Configure(...)` that adds an endpoint.

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
