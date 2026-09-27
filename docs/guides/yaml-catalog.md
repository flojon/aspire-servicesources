# The YAML catalog (advanced)

Everything [`AddServiceCatalog`](catalog-in-code.md) declares in code can be written in
`servicesources.yaml` instead — a plain-text catalog committed next to the AppHost project, with
one block per source and no C# or TypeScript to read or compile. Reach for it when the catalog
should be readable, or diffable in a PR, without opening the AppHost project, or when non-.NET
tooling needs to read it directly.

Throughout these docs, catalog examples have a **YAML** tab beside the **C#** one; pick it once and
every example on the site follows.

## Declaring a service

Add `servicesources.yaml` next to the AppHost project, and commit it:

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main          # optional; branch, tag, or commit SHA
```

`Program.cs` then only declares the dependency — no `AddServiceCatalog` call is needed for a
service the yaml declares:

```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

var orders = builder.AddService("orders");

builder.Build().Run();
```

A developer picks a source in `servicesources.local.json` exactly as in the
[Quickstart](../getting-started/quickstart.md#2-pick-your-own-source).

## Keys and their builder equivalents

| yaml | Builder method |
| --- | --- |
| `repository:`, `defaultRef:` | `WithRepository(url, defaultRef)` |
| `repositoryRef:` + top-level `repositories:` | `WithSharedRepository(catalog.AddRepository(...))` |
| `path:` | `WithPath(path)` |
| `project:` | `WithProject(project)` |
| `kind:` + a block named after the kind | `WithKind(kind, options)`, or `AsJavaScript`/`AsJava` |
| `prepare:` | `WithPrepare(command, windowsCommand, mode)` |
| `url:` | `WithUrl(url)` |
| `container:` | `WithContainer(image, port, defaultTag, scheme)` |
| `kubernetes:` | `WithKubernetes(service, port, scheme)` |
| `defaultSource:` | `WithDefaultSource(source)` |

## `defaultSource`

A catalog entry may declare `defaultSource`, so a service resolves without a
`servicesources.local.json` entry at all:

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main
    defaultSource: repository     # the source a developer gets with no entry of their own
```

!!! warning "`defaultSource: repository` means every developer, and CI, clones and builds that repository by default"
    That includes a machine or pipeline that never explicitly asked for it. If CI should not
    clone, CI must pin its own source (an environment variable, or its own configuration layer)
    rather than relying on the absence of a file. `defaultSource: path` carries no such cost —
    see [the `"path"` source](../sources/path.md).

## Grouping several services under one repository

Declare the repository once under a top-level `repositories:` key and have each service join it
with `repositoryRef` instead of repeating `repository:` — every member then shares one managed
checkout. See
[Several services from one repository](../sources/repository.md#several-services-from-one-repository)
for the full shape and the group-level `ref`/`prepare` override.

## Combining with a code catalog

A service is declared in yaml *or* in code, never both — a service named in both is a
configuration error naming both, not a merge or a silent precedence rule. yaml and
`AddServiceCatalog` calls can coexist in the same AppHost as long as they don't name the same
service — one team's services in yaml, a plugin's own services registered by its own
`AddServiceCatalog` call.

Everything else in these docs — sources, `kind`, `prepare`, configuring a resolved service —
applies the same way regardless of which one declares a given service.
