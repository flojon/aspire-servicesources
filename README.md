# Aspire.Hosting.ServiceSources

[![NuGet](https://img.shields.io/nuget/v/KoalaSoft.Aspire.Hosting.ServiceSources?logo=nuget&label=nuget)](https://www.nuget.org/packages/KoalaSoft.Aspire.Hosting.ServiceSources)
[![Downloads](https://img.shields.io/nuget/dt/KoalaSoft.Aspire.Hosting.ServiceSources?logo=nuget&label=downloads)](https://www.nuget.org/packages/KoalaSoft.Aspire.Hosting.ServiceSources)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](https://github.com/flojon/aspire-servicesources/blob/main/LICENSE)

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
[Non-.NET local services](https://github.com/flojon/aspire-servicesources/blob/main/docs/kinds.md):

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

Or reference the project directly from your AppHost instead:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj" />
</ItemGroup>
```

Requires .NET 8 or later (net8.0, net9.0, and net10.0 are all supported) and an AppHost
project using the `Aspire.AppHost.Sdk` (`aspire new` / `aspire restore` sets this up).

Every release is listed in the
[changelog](https://github.com/flojon/aspire-servicesources/blob/main/CHANGELOG.md), which
is where breaking changes and their migrations are recorded. Check it before upgrading —
while the version is below `1.0.0`, a breaking change can ship in a minor release.

## Getting started

A service catalog can be declared either in `servicesources.yaml` — the walkthrough below
— or in code via `AddServiceCatalog`, covered in full under
[Authoring the catalog in code](https://github.com/flojon/aspire-servicesources/blob/main/docs/authoring-in-code.md). This walkthrough uses
yaml because it's the simplest on-ramp; the "in code" section covers the alternative in
full, and everything else in this README applies the same way regardless of which one you
use.

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
[Non-.NET local services](https://github.com/flojon/aspire-servicesources/blob/main/docs/kinds.md).)

A catalog entry may also declare `defaultSource`, so a service resolves without a
`servicesources.local.json` entry at all:

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main
    defaultSource: local     # optional; the source a developer gets with no entry of their own
```

**`defaultSource: local` means every developer, and CI, clones and builds that repository by
default** — including on a machine or pipeline that never explicitly asked for it. If CI should not
clone, CI must pin its own source (an environment variable, or its own configuration layer) rather
than relying on the absence of a file.

**3. Add your own `servicesources.local.json` next to it (gitignore this file — it's
per-developer):**

```json
{
  "services": {
    "orders": { "source": "local" }
  }
}
```

That file is the base layer of the AppHost's own configuration — an environment variable or an
`appsettings.json` entry can override any of it for a single run, without an edit. See
[Overriding `servicesources.local.json`](https://github.com/flojon/aspire-servicesources/blob/main/docs/configuration.md#overriding-servicesourceslocaljson).

That's it — running the AppHost now clones `orders` into
`<AppHostDirectory>/.servicesources/checkouts/orders/`, checks out `main`, and runs it via
Aspire's own project orchestration, wired up to `api` through service discovery exactly like
a project reference would be.

## Documentation map

The rest of the reference lives alongside this file, split by topic:

- [Authoring the catalog in code](https://github.com/flojon/aspire-servicesources/blob/main/docs/authoring-in-code.md) — declare the same catalog in C# or
  TypeScript instead of yaml, via `AddServiceCatalog`.
- [The `"local"` source](https://github.com/flojon/aspire-servicesources/blob/main/docs/local-source.md) — managed git checkouts, the `prepare` bootstrap
  step, why a checkout doesn't inherit your repository's build settings, and grouping several
  services under one repository.
- [Non-.NET local services (`kind`)](https://github.com/flojon/aspire-servicesources/blob/main/docs/kinds.md) — the built-in `javascript` and `java` kinds,
  and how to implement your own.
- [Other sources: `kubernetes`, `url`, `container`, `disabled`](https://github.com/flojon/aspire-servicesources/blob/main/docs/other-sources.md) — a
  `kubectl port-forward` against a dev cluster, a fixed URL, a published container image, or
  turning a service off, plus combining several sources on one catalog entry.
- [Configuring and consuming a resolved service](https://github.com/flojon/aspire-servicesources/blob/main/docs/configuration.md) — overriding
  `servicesources.local.json` from higher configuration layers, `ServiceResource`'s native Aspire
  vocabulary, and naming a service's endpoint portably across sources.
- [Backing services: databases, brokers and caches](https://github.com/flojon/aspire-servicesources/blob/main/docs/backing-services.md) — `AddBackingService()`,
  reaching one through a cluster tunnel, and connection-string placeholders.

## Sample

`samples/DemoAppHost` is a minimal working AppHost demonstrating all three easily-runnable
sources: `orders` via a real managed `"local"` git checkout (a small project cloned from
[`dotnet/aspire-samples`](https://github.com/dotnet/aspire-samples)), `inventory` via the
`"url"` source (pointing at [httpbin.org](https://httpbin.org), a live public test API), and
`payments` via the `"container"` source (the `nginxdemos/hello` hello-world image) — run it to
see the whole flow end to end. (`"kubernetes"` isn't demoed here since it needs a real cluster
and `kubectl`; see [its section](https://github.com/flojon/aspire-servicesources/blob/main/docs/other-sources.md#kubernetes-source).)

It also carries a `catalog` service showing `kind: java` — a `"local"` checkout of
[Spring PetClinic](https://github.com/spring-projects/spring-petclinic) run with its own Maven
wrapper; `java` being a built-in kind, no `Program.cs` registration is needed. `AddService("catalog")`
is commented out and the service is left out of `servicesources.local.json.example`, since unlike
the three above it needs a JDK. To run it, do both: uncomment the call and add
`"catalog": { "source": "local" }` to your `servicesources.local.json`. Leaving it out of that file
by default is what keeps the sample from cloning PetClinic on its first run: the sample does not
call `UseDeferredCheckout()`, so the first `AddService` clones every `"local"` entry there that has
no checkout yet, whether or not you add it.

```bash
cd samples/DemoAppHost
cp servicesources.local.json.example servicesources.local.json
aspire run
```

A TypeScript AppHost equivalent — proving `AddService()` is correctly exported and registers with
Aspire's Type System from a guest language, and that a resolved service can be
[configured from TypeScript](https://github.com/flojon/aspire-servicesources/blob/main/docs/configuration.md#from-a-guest-language-apphost) — lives in
`samples/DemoAppHostTypeScript`. Both of its services use the `"container"` source so that
`payments` can `withReference(inventory)`: a `"url"` service runs out of band, and a
container consumer of one is [rejected up front](https://github.com/flojon/aspire-servicesources/blob/main/docs/other-sources.md#url-source). A third resource, the `probe`
executable, hands the same `inventory` handle to Aspire's *own* `withReference()` and to
`getServiceEndpoint()`, and prints what each injected — so it shows as *Exited*, not Running, and
those two log lines are where you see both the native service-discovery path and the
[portable endpoint accessor](https://github.com/flojon/aspire-servicesources/blob/main/docs/configuration.md#naming-a-services-endpoint) working. (**Note:** this
sample needs Aspire CLI 13.5.3 or newer — see the compatibility note below the code block.)

```bash
cd samples/DemoAppHostTypeScript
npm install
cp servicesources.local.json.example servicesources.local.json
aspire restore
aspire run
```

**Requires Aspire CLI 13.5.3+:** the CLI pins its own Aspire version for the host project it
generates, so a CLI older than this package's Aspire floor (13.5.2) fails `aspire restore` with
`NU1605: Detected package downgrade: Aspire.Hosting from 13.5.2 to 13.5.1` before codegen even runs.
13.5.3 is the first release that pins high enough. On it, the generated SDK type-checks clean under
strict `tsc` and the sample runs end-to-end — `withReference()` on the `addService()` result injects
the resolved service's discovery variables into the consuming resource, e.g.
`services__inventory__http__0=http://inventory.dev.internal:80` pointing at the running `inventory`
container.

This sample used to require an unreleased 13.6.0, and that requirement is gone — first because the
ten configuration shims' bare-interface receiver stopped being what carried the codegen wrapper
pair (see the history below), and now because `AddService()` returns `ServiceResource`, a concrete,
sealed class, which was never affected by the bare-interface issue in the first place.

Historically: Aspire's TypeScript codegen did not emit a `*Promise`/`*PromiseImpl` wrapper pair for
a bare Aspire interface (`IResourceBuilder<IResourceWithServiceDiscovery>`, which is what
`AddService` used to return), so the generated SDK referenced an undeclared
`ResourceWithServiceDiscoveryPromise` and failed with six `TS2552` errors — reported as
[microsoft/aspire#19507](https://github.com/microsoft/aspire/issues/19507) and fixed upstream by
[microsoft/aspire#19577](https://github.com/microsoft/aspire/pull/19577) under the 13.6 milestone.
That upstream fix was not what made this work even before `ServiceResource` existed: the generator
emits the wrapper pair when the bare interface appears as an extension-method **receiver** rather
than only as a return type, and the ten (now-retired) `[AspireExport]` configuration shims declared
exactly that receiver — so they carried the wrapper pair for `addService` too. The measurement is
in
[`docs/superpowers/specs/2026-08-30-19507-already-fixed-findings.md`](https://github.com/flojon/aspire-servicesources/blob/main/docs/superpowers/specs/2026-08-30-19507-already-fixed-findings.md).

Switching between CLI builds can leave a stale code generator under `.aspire/`, so remove that
directory before regenerating:
[microsoft/aspire#19603](https://github.com/microsoft/aspire/issues/19603).

## When configuration is wrong

Every problem this package detects — a missing project file, an unregistered `kind`, a checkout it
won't overwrite, a clone it can't authenticate — is raised as a `ServiceSourcesConfigurationException`
whose message names the service, what failed, and what to do about it. Because these are raised
from `AddService()`, they usually reach you as an unhandled exception that takes the AppHost down
before Aspire starts, so that message *is* the error output. It prints as the message plus one
line per underlying cause:

```
Unhandled exception. Service 'reportdata': failed to clone repository 'https://github.com/acme/reportdata' into
'/src/report-service/src/Report.AppHost/.servicesources/checkouts/reportdata' — authentication failed, or the
repository is not visible to the credentials in use. Configure credentials via a git credential helper (`git
credential fill` must resolve them for this host) or the SERVICESOURCES_GIT_USERNAME/SERVICESOURCES_GIT_TOKEN/
SERVICESOURCES_GIT_HOST environment variables.
  caused by: unexpected http status code: 404
  (set SERVICESOURCES_FULL_ERRORS=1 for the full exception detail, including stack traces)
```

The stack frames behind it are this package's own plumbing and don't help with a misconfiguration,
so they're left out — and the last line says how to get them back, because for a failure this
package didn't anticipate they are the diagnosis. When you need them — you suspect a bug in this package rather than in your
configuration, and want to file it — set `SERVICESOURCES_FULL_ERRORS=1` to get the runtime's
complete dump, type names, inner-exception blocks, stack traces and all.

## Status

Early stage, evolving fast. `"local"`, `"kubernetes"`, `"url"`, `"container"` and `"disabled"`
sources are all implemented — see [`docs/superpowers/`](https://github.com/flojon/aspire-servicesources/blob/main/docs/superpowers/) for design and implementation
history, including the phase 2 backlog (repo auto-update, config discovery walk-up,
dependency/infrastructure resolution, and more).

Changes are recorded in [`CHANGELOG.md`](CHANGELOG.md); how a release is cut is in
[`RELEASING.md`](RELEASING.md); the trust model a resolved service runs under is in
[`SECURITY.md`](SECURITY.md).
