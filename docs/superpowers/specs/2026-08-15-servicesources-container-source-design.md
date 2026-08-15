# Aspire.Hosting.ServiceSources — Container Source Design

**Status:** Approved
**Date:** 2026-08-15
**Scope:** A new `IServiceSource` implementation, `ContainerSource`, that resolves `AddService()` against a published container image instead of a local checkout or a cluster port-forward. Builds on the milestone 1a architecture (see [milestone 1a design](2026-08-09-servicesources-design.md)) and the [cluster source design](2026-08-13-servicesources-cluster-source-design.md), and closes out the "Container and external-endpoint sources" item (container half) from the [phase 2 reference doc](2026-08-09-servicesources-phase2-future-work.md) and issue #5.

## Problem

Milestone 1a and the cluster source cover "clone and build from source" and "consume an already-running cluster instance." Neither covers the third common case: run a specific published image of a service locally, without cloning its repository or standing up a cluster connection — useful when a developer needs a dependency to exist locally but has no reason to touch its source (e.g. a peer team's service, or pinning a known-good build to reproduce a bug).

## Architecture

- New `ContainerSource : IServiceSource`, registered in `AddService()`'s internal source lookup under `"container"`, alongside `"local"` and `"cluster"`.
- `ContainerSource.Resolve()` calls Aspire's own `builder.AddContainer(name, image, tag)` — a real, already-registered `ContainerResource`. This mirrors both existing sources' delegation pattern (`LocalProjectSource` → `AddProject`, `ClusterSource` → `AddExecutable`): DCP and the dashboard only ever see a normal Aspire container resource, with image pull, process lifecycle, and logs managed entirely by Aspire's own container-runtime integration (Docker or Podman, whichever DCP detects — this package makes no runtime choice of its own).
- The container gets an `EndpointAnnotation` via `WithHttpEndpoint(targetPort: containerPort)` — v1 assumes HTTP (see Out of Scope). Unlike `ClusterSource`, **no host `port:` is passed**: Aspire/DCP auto-assigns a free host port and proxies it (`isProxied` defaults to `true`), the same mechanism `AddRedis`/`AddPostgres` and every other stock Aspire container resource already rely on. This means `ContainerSource` needs no `IPortAllocator`-style seam — the TOCTOU/manual-allocation concern that motivated pre-allocating a port for `kubectl port-forward` doesn't apply here, because Aspire itself owns the container's port publishing.
- The same `ServiceResource` facade wraps the returned builder, copying its endpoint annotation exactly as it does for `ProjectResource` and `ExecutableResource` — `CreateFacade`'s existing `IResourceBuilder<TResource> where TResource : IResource` signature already accepts `IResourceBuilder<ContainerResource>` without changes.

### Why no `IPortAllocator`

`ClusterSource` pre-allocates a local port itself because `kubectl port-forward` is an external process that needs an explicit local port argument upfront, and Aspire has no visibility into what port it bound. A container's port publishing, by contrast, is something Aspire/DCP already manages end-to-end — `WithHttpEndpoint(targetPort:)` with no `port:` value is the documented, standard way every other Aspire container resource (built-in or custom) gets a dynamically-assigned, proxied host port. Reusing `IPortAllocator` here would duplicate a mechanism Aspire already provides for exactly this case.

## Config Schema

Image identity (`image`, default tag) is shared metadata like `repository`/`project`, so it lives in the catalog. A per-developer tag override is an environment/testing choice, so it lives in local config — the same split milestone 1a uses for `repository`/`project` vs. `path`/`ref`, and the cluster source uses for `cluster.service`/`cluster.port` vs. `context`/`namespace`.

`servicesources.yaml` (catalog) — adds an optional `container` block per service:
```yaml
services:
  orders:
    repository: https://github.com/company/orders
    project: src/Orders.Api/Orders.Api.csproj
    container:
      image: ghcr.io/company/orders
      port: 8080
      defaultTag: latest
```

`servicesources.local.json` (per-developer) — `"source": "container"` entries:
```json
{
  "services": {
    "orders": {
      "source": "container",
      "tag": "v1.4.2"
    }
  }
}
```

Rules:
- `image` is required for a `"container"` source. It is a full image reference and may itself include a registry host (e.g. `ghcr.io/company/orders`) — Docker image references already encode the registry, so there is no separate `registry` config field.
- `port` (the container's internal listen port, used as `targetPort`) is required in the catalog. Unlike the cluster source's remote port, a container image's listen port is a fixed property of the image itself, not something that varies per developer or per environment, so no local-config override is offered.
- `tag` is optional in local config. Resolution precedence: local.json `tag` → catalog `container.defaultTag` → Aspire's own `AddContainer` default (`"latest"`) when neither is set. This mirrors `ref`/`defaultRef`'s override shape from milestone 1a, letting a developer pin a specific build for reproduction or testing without editing the shared catalog.
- Credentials/registry auth: the container runtime (Docker/Podman) uses the developer's ambient login state (`docker login`, credential helpers already configured). ServiceSources does not manage registry credentials itself — same posture as the cluster source's ambient `kubectl` config.

## Resolution Flow

1. Load & cache both config files (existing milestone 1a behavior). Look up developer config for `name`; dispatch to `ContainerSource` when `source` is `"container"`.
2. Look up the catalog entry for `name`. If it has no `container.image` or `container.port`, fail fast, naming the missing field.
3. Resolve `tag` as local.json override → catalog `defaultTag` → `null` (meaning: let `AddContainer` default to `"latest"`).
4. `builder.AddContainer(name, image, tag)` when a tag was resolved, otherwise the 2-arg `builder.AddContainer(name, image)` overload.
5. `WithHttpEndpoint(targetPort: containerPort)` — no host `port:`, so Aspire assigns and proxies it.
6. Wrap the returned builder in the `ServiceResource` facade (identical mechanism to the other two sources), return it.

Resolution stays synchronous within `AddService()`, consistent with milestone 1a — only image/tag/port resolution happens at this point; the actual image pull and container start are managed by Aspire/DCP afterward, same as the other sources' "resolve now, run later" split.

## Error Handling

- **Config errors** — missing `container.image` or `container.port` in the catalog — fail fast at the `AddService()` call site, naming the service and the missing field. Same philosophy as the other two sources.
- **Runtime errors** — no container runtime available, image not found/not pullable, invalid tag, registry auth failure, or a container that exits immediately — are **not** `AddService()`-time exceptions. They surface through the `ContainerResource`'s own state and logs in the Aspire dashboard, exactly like the cluster source's port-forward failures and the local source's build failures. Falls out of delegating to `AddContainer` rather than managing the container ourselves.

## Testing

- **Config parsing**: unit tests for the new `container` catalog block and the local-config `tag` field, covering each fail-fast path (missing `container.image`, missing `container.port`) and tag precedence (local override vs. catalog default vs. neither set).
- **`ContainerSource` orchestration**: unit tests for image/tag resolution logic — which `AddContainer` overload gets called and with what arguments — using the same fake-builder-inspection approach as `ClusterSourceTests`. No real container runtime invocation in unit tests.
- **No automated container/integration test in v1** — matches the existing sources' posture (no real `kubectl`, no real git clone in unit tests). Verification is a manual smoke test via the demo AppHost against a real local Docker daemon.

## Explicitly Out of Scope for This Pass

- Non-HTTP endpoints (`WithEndpoint` instead of `WithHttpEndpoint`) — v1 assumes HTTP, same scoping as the cluster source.
- Registry authentication beyond ambient Docker/Podman credentials — no `registry`/credential config surface.
- Image digest pinning (`WithImageSHA256`) — tag-based resolution only.
- `WithImagePullPolicy` / `WithLifetime` overrides — Aspire's own defaults apply.
- Environment-variable or dependency wiring into the container (e.g. connection strings, `WithReference`) — consistent with today's `LocalProjectSource`/`ClusterSource`, neither of which wire env vars either. The parallel, still-*Draft* [database source design](2026-08-15-servicesources-database-source-design.md)'s `AddService(local:)` callback would be the natural future integration point for this; not attempted here, and this design does not depend on that draft landing.
- Auto/fallback source selection referencing container as a candidate — separate phase-2 item.
