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
| `dotnet:` | `AsDotnet(o => o.WithLaunchProfileName(name))` / `o.ExcludeLaunchProfile()` |
| `defaultSource:` | `WithDefaultSource(source)` |

## `dotnet`: choosing a launch profile

A `dotnet` service runs under the launch profile Aspire selects on its own: the profile the AppHost
was launched under when the project has one by that name, otherwise the first launchable profile in
`Properties/launchSettings.json`. The `dotnet:` block overrides that choice.

```yaml
services:
  orders:
    repository: https://github.com/company/orders
    project: src/Orders.Api/Orders.Api.csproj
    dotnet:
      launchProfileName: http      # run under this profile
```

| Key | Meaning |
| --- | --- |
| `launchProfileName` | Run the project under this profile. Matched exactly against the keys in the project's `Properties/launchSettings.json`: case-sensitive, no trimming. A blank value means "not set" here, whereas the code builder's `WithLaunchProfileName` throws on one. |
| `excludeLaunchProfile` | `true` runs the project with no launch profile at all, so it loses the endpoints and environment variables a profile would supply. `false` is the same as leaving it out. |

A named profile outranks `AppHost:DefaultLaunchProfileName`.

These are configuration errors, reported when the catalog loads or, for the profile check, when the
service resolves:

- `excludeLaunchProfile: true` together with a non-blank `launchProfileName` contradict each other.
- A `dotnet:` block on a service whose `kind` is not `dotnet`.
- A `launchProfileName` that is not in the project's `launchSettings.json`, or a project with no
  `launchSettings.json` at all. Aspire's own failure for these is a generic exception, or none
  when the file is absent, so this names the service, the file and the profiles it has. An unreadable `launchSettings.json` skips this check.

The block is catalog-only: there is no `servicesources.local.json` override. It applies to the
`repository` and `path` sources, and is ignored by the sources that run no project.

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
