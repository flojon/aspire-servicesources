# Installation

Published on nuget.org as [`KoalaSoft.Aspire.Hosting.ServiceSources`](https://www.nuget.org/packages/KoalaSoft.Aspire.Hosting.ServiceSources).
If every service your AppHost declares is a .NET project, this is the only package you need:

```bash
dotnet add package KoalaSoft.Aspire.Hosting.ServiceSources
```

## Requirements

- .NET 8 or later — net8.0, net9.0 and net10.0 are all supported.
- An AppHost project using the `Aspire.AppHost.Sdk` (`aspire new` / `aspire restore` sets this up).
- Aspire 13.5.2 or later — see below.
- Whatever the sources you use need on `PATH`: `git` 2.7+ for [`"repository"`](../sources/repository.md),
  `kubectl` for [`"kubernetes"`](../sources/kubernetes.md).

## Aspire version

!!! warning "This package floors Aspire at 13.5.2, so an AppHost still on 13.4.x gets a mixed Aspire family."
    NuGet takes the highest floor, so `Aspire.Hosting` is lifted to 13.5.2 while your
    `Aspire.AppHost.Sdk`, `Aspire.Hosting.AppHost` and the DCP and dashboard packages the SDK
    pins to it stay where they are. Nothing warns about it at restore. Move your AppHost's own
    Aspire version to 13.5.2 or later at the same time:

    ```xml
    <Sdk Name="Aspire.AppHost.Sdk" Version="13.5.2" />
    ```

## Non-.NET services

Services that aren't .NET projects need Aspire's hosting package for their language,
referenced by your AppHost alongside this one — see
[Non-.NET services](../guides/non-dotnet-services.md):

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

## Upgrading

Every release is listed in the
[changelog](https://github.com/flojon/aspire-servicesources/blob/main/CHANGELOG.md), which
is where breaking changes and their migrations are recorded. Check it before upgrading —
while the version is below `1.0.0`, a breaking change can ship in a minor release.

Next: [Quickstart](quickstart.md).
