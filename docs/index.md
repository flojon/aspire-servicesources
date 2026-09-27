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

## How it fits together

| File | Owner | Says |
| --- | --- | --- |
| `Program.cs` | The AppHost | *Which* services it depends on: `builder.AddService("orders")`. |
| `servicesources.yaml` (committed) | The team | *How* each source would resolve each service: its repository, project, image, cluster service, URL. |
| `servicesources.local.json` (gitignored) | Each developer | *Which* source actually applies to them. |

## Where to go next

**Getting started**

- [Installation](getting-started/installation.md) — the package, the Aspire version it needs, and
  the extra package for each non-.NET language.
- [Quickstart](getting-started/quickstart.md) — declare, catalog and run one service.
- [Samples](getting-started/samples.md) — runnable C# and TypeScript AppHosts.

**Sources**

- [Overview](sources/index.md) — all six sources side by side, and combining them on one catalog
  entry.
- [`"repository"`](sources/repository.md) — managed git checkouts, the `prepare` bootstrap step,
  deferred first-run cloning, and grouping several services under one repository.
- [`"path"`](sources/path.md) — a directory already checked out beside the AppHost.
- [`"kubernetes"`](sources/kubernetes.md), [`"url"`](sources/url.md),
  [`"container"`](sources/container.md), [`"disabled"`](sources/disabled.md).

**Guides**

- [Authoring the catalog in code](guides/catalog-in-code.md) — declare the catalog in C# or
  TypeScript instead of yaml, via `AddServiceCatalog`.
- [Non-.NET services (`kind`)](guides/non-dotnet-services.md) — the built-in `javascript` and
  `java` kinds, and how to implement your own.
- [Configuring and consuming a resolved service](guides/configuration.md) — overriding
  `servicesources.local.json` from higher configuration layers, `ServiceResource`'s native Aspire
  vocabulary, and naming a service's endpoint portably across sources.
- [Backing services](guides/backing-services.md) — `AddBackingService()` for databases, brokers
  and caches, reaching one through a cluster tunnel, and connection-string placeholders.

**Reference**

- [Troubleshooting](troubleshooting.md) — reading the errors this package raises.
- [Changelog](https://github.com/flojon/aspire-servicesources/blob/main/CHANGELOG.md) — every
  release, with breaking changes and their migrations.
- [Security](https://github.com/flojon/aspire-servicesources/blob/main/SECURITY.md) — the trust
  model a resolved service runs under.

## Status

Early stage, evolving fast. While the version is below `1.0.0`, a breaking change can ship in a
minor release — check the changelog before upgrading.

Use the version selector in the corner to switch between **latest** (tracks `main`, may
contain unreleased changes) and **stable**/numbered releases.
