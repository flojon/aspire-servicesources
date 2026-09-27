# The yaml catalog

[← Back to README](https://github.com/flojon/aspire-servicesources/blob/main/README.md)

Everything [`AddServiceCatalog`](authoring-in-code.md) declares in code can be written in
`servicesources.yaml` instead — a plain-text catalog committed next to the AppHost
project, with one block per source and no C#/TypeScript to read or compile. Reach for it
when the catalog should be readable (or diffable in a PR) without opening the AppHost
project, or when non-.NET tooling needs to read it directly.

**1. Add `servicesources.yaml` next to the AppHost project (commit this file):**

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main          # optional; branch, tag, or commit SHA
```

(A service that isn't a .NET project also takes a `kind` — see
[Non-.NET local services](kinds.md).)

**2. Declare the service in `Program.cs`, exactly as with a code-declared catalog:**

```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

var orders = builder.AddService("orders");             // "orders" lives in a separate repository
var api = builder.AddProject<Projects.Api>("api")       // "api" is part of *this* AppHost's own solution
    .WithReference(orders);

builder.Build().Run();
```

`AddProject<Projects.Api>()` only works for a project `ProjectReference`d from the AppHost's own
`.csproj` — an external service like `orders` has no such reference to generate `Projects.Orders`
from, which is exactly what `AddService()` is for.

**3. Add your own `servicesources.local.json` next to it (gitignore this file — it's
per-developer):**

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

## `defaultSource`

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

**`defaultSource: repository` means every developer, and CI, clones and builds that repository
by default** — including on a machine or pipeline that never explicitly asked for it. If CI
should not clone, CI must pin its own source (an environment variable, or its own
configuration layer) rather than relying on the absence of a file. `defaultSource: path`
carries no such caveat — see
[`defaultSource: path` is close to free](local-source.md#path-source).

The code-authoring equivalent is `WithDefaultSource(source)` — see
[Authoring the catalog in code](authoring-in-code.md).

## Grouping several services under one repository

Declare the repository once under a top-level `repositories:` key and have each service
join it by `repositoryRef` instead of repeating `repository:` — every member then shares
one managed checkout. See
[Several services from one repository](local-source.md#several-services-from-one-repository)
for the full shape, the group-level `ref`/`prepare` override, and how this differs from the
`"path"` source, which has nothing to group.

## Combining with a code catalog

A service is declared in yaml *or* in code, never both — a service named in both is a
configuration error naming both sources, not a merge or a silent precedence rule. yaml and
`AddServiceCatalog` calls can coexist in the same AppHost as long as they don't name the
same service — one team's services in yaml, a plugin's own services registered by its own
`AddServiceCatalog` call.

Everything else in these docs — sources, `kind`, `prepare`, configuring a resolved
service — applies the same way regardless of which one declares a given service.
