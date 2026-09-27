# Sources overview

A *source* is how one developer resolves one service. The AppHost calls `AddService("orders")` the
same way whichever source applies; the catalog (`servicesources.yaml`, or
[in code](../guides/catalog-in-code.md)) describes how each source *would* resolve the service, and
each developer's `servicesources.local.json` picks the one that actually does.

| `source` | What runs | Needs | Page |
| --- | --- | --- | --- |
| `"repository"` | A managed git checkout of the service, cloned and reconciled onto a ref, run by Aspire | `git` 2.7+ | [Repository](repository.md) |
| `"path"` | A directory already checked out beside the AppHost, such as a monorepo sibling | — | [Path](path.md) |
| `"kubernetes"` | Nothing locally; a `kubectl port-forward` to an instance in a dev cluster | `kubectl` | [Kubernetes](kubernetes.md) |
| `"url"` | Nothing; the service resolves to a fixed, already-running URL | — | [URL](url.md) |
| `"container"` | A published container image, run by Aspire's container integration | A container runtime | [Container](container.md) |
| `"disabled"` | Nothing at all; the service is turned off | — | [Disabled](disabled.md) |

`"repository"` and `"path"` services are .NET projects by default; set a `kind` to run one in
another language — see [Non-.NET services](../guides/non-dotnet-services.md).

## Combining sources on one catalog entry

A single `servicesources.yaml` entry can carry blocks for every source at once — the catalog
just describes *how* each source would resolve the service; each developer's
`servicesources.local.json` picks which one actually applies to them:

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    path: services/orders
    kubernetes:
      service: orders-svc
      port: 8080
    url:
      url: https://orders.example.com
    container:
      image: ghcr.io/example/orders
      port: 8080
      defaultTag: latest
```

A developer editing the service picks `"repository"` to clone it, or `"path"` if they already
have it checked out beside the AppHost; one debugging against a shared dev cluster picks
`"kubernetes"`; one who just needs it reachable picks `"url"` or `"container"` — same catalog
entry, same `AddService("orders")` call in the AppHost, no code changes either way. Each
developer's own `servicesources.local.json` just names which source applies to them — cloning
`orders` locally:

```json
{ "services": { "orders": { "source": "repository" } } }
```

or using the copy already checked out beside the AppHost:

```json
{ "services": { "orders": { "source": "path" } } }
```

debugging against a shared dev cluster:

```json
{ "services": { "orders": { "source": "kubernetes", "kubernetes": { "context": "dev-west", "namespace": "orders", "port": 8080 } } } }
```

or just needing it reachable, not caring how:

```json
{ "services": { "orders": { "source": "url" } } }
```

or turning it off entirely — no block needed for this one:

```json
{ "services": { "orders": { "source": "disabled" } } }
```

The `source` value is matched without regard to case, so `"repository"`, `"Repository"` and
`"REPOSITORY"` all name the same source. A name none of the six has is refused at composition
time, naming the ones that exist. (The `kind` names in `servicesources.yaml` are the exception —
those *are* case-sensitive, because anything may register one and two registrations must not be
able to collide by spelling.)

