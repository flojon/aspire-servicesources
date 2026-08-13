# Aspire.Hosting.ServiceSources — Cluster Source Design

**Status:** Approved
**Date:** 2026-08-13
**Scope:** A new `IServiceSource` implementation, `ClusterSource`, that resolves `AddService()` against an already-running service in a Kubernetes dev cluster via `kubectl port-forward`. Builds on the milestone 1a architecture (see [milestone 1a design](2026-08-09-servicesources-design.md)) and closes out the "Cluster source" item from the [phase 2 reference doc](2026-08-09-servicesources-phase2-future-work.md).

## Problem

Milestone 1a only supports a `"local"` source (clone-and-build a project from source). The most-requested next source is consuming a service that's already running in a shared dev cluster, without cloning or building it locally. No official Aspire building block exists for this — `ExternalServiceResource` is sealed and lacks `IResourceWithEndpoints`.

## Architecture

- New `ClusterSource : IServiceSource`, registered in `AddService()`'s internal source lookup under `"cluster"`, alongside `"local"`.
- `ClusterSource.Resolve()` allocates a free local port itself, builds a `kubectl port-forward` argument list, and calls `builder.AddExecutable("<name>-portforward", "kubectl", args: [...])` — a real, already-registered Aspire resource. This mirrors how `LocalProjectSource` delegates to Aspire's own `AddProject` rather than inventing a custom resource type: DCP and the dashboard only ever see a normal `ExecutableResource`, with its process lifecycle (start/stop/restart), logs, and state managed entirely by Aspire.
- The executable gets an `EndpointAnnotation` via `WithHttpEndpoint(port: allocatedPort, targetPort: remotePort)` — v1 assumes HTTP (see Out of Scope).
- The same `ServiceResource` facade from milestone 1a wraps this executable's builder, copying its endpoint annotation exactly as it does for `ProjectResource`. `ServiceResource.CreateFacade` is generalized from `IResourceBuilder<ProjectResource>` to a generic `IResourceBuilder<TResource> where TResource : IResource` parameter so it accepts the `IResourceBuilder<ExecutableResource>` that `AddExecutable` returns — the copy logic itself (`OfType<EndpointAnnotation>()`) is unchanged, and existing `ProjectResource` call sites are unaffected by type inference.
- New `IPortAllocator` seam (same shape as `IGitClient`): wraps a raw-socket free-port lookup (bind `:0`, read the assigned port, close the socket) so `ClusterSource`'s argument-building and precedence logic is unit-testable via a fake, deterministic allocator.

### Why pre-allocate the port ourselves

Letting `kubectl port-forward` auto-pick its own local port (`kubectl port-forward svc/x :8080`) and parsing its stdout for the assigned port would work, but requires readiness detection and log-scraping, and doesn't fit `WithHttpEndpoint(port:)`'s synchronous "port known now" shape — it would reintroduce the kind of lazy-value-provider machinery that the phase-2 doc explicitly defers to a *separate*, later item (deferred/parallel resolution). Pre-allocating the port ourselves keeps `ClusterSource.Resolve()` fully synchronous, consistent with milestone 1a's resolution model, at the cost of a small (accepted) TOCTOU race between our bind-and-release and kubectl's own bind.

## Config Schema

Cluster targeting (`service`, default `port`) is shared metadata like `repository`/`project`, so it lives in the catalog. Context, namespace, and (optionally) a port override are per-developer environment choices, so they live in local config — the same split milestone 1a uses for `repository`/`project` vs. `path`/`ref`.

`servicesources.yaml` (catalog) — adds an optional `cluster` block per service:
```yaml
services:
  orders:
    repository: https://github.com/company/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main
    cluster:
      service: orders-svc
      port: 8080
```

`servicesources.local.json` (per-developer) — `"source": "cluster"` entries:
```json
{
  "services": {
    "orders": {
      "source": "cluster",
      "context": "dev-west",
      "namespace": "orders",
      "port": 8080
    }
  }
}
```

Rules:
- `context` is required for a `"cluster"` source — no cross-environment default makes sense.
- `namespace` is optional, defaulting to `default`.
- `port` is optional in local config. Resolution precedence: local.json `port` → catalog `cluster.port`. Fails fast if neither is set. This exists because a Kubernetes Service's exposed port is not guaranteed identical across every cluster/environment, even though it usually is by convention — same override shape as `ref`/`defaultRef` in milestone 1a.
- `service` (the k8s Service name) is catalog-only — it identifies the service's identity, not an environment-specific choice, so no override is offered.
- Credentials/config: `kubectl` uses the developer's ambient config (`~/.kube/config`, `KUBECONFIG`, any auth plugins already working). ServiceSources passes only `--context`/`--namespace`; it does not manage kubeconfig or credentials itself.

## Resolution Flow

1. Load & cache both config files (existing milestone 1a behavior). Look up developer config for `name`; dispatch to `ClusterSource` when `source` is `"cluster"`.
2. Look up the catalog entry for `name`. If it has no `cluster.service`, fail fast: `"service '{name}' source is 'cluster' but servicesources.yaml has no cluster.service entry"`.
3. Look up dev config for `name`. Require `context` (fail fast if missing). `namespace` defaults to `default`. Resolve `port` as local.json override → catalog `cluster.port`; fail fast if neither is set.
4. Allocate a free local port via `IPortAllocator`.
5. Build kubectl args: `port-forward svc/<service> <localPort>:<port> --context <context> --namespace <namespace>`.
6. `builder.AddExecutable("<name>-portforward", "kubectl", args: [...])`, then `WithHttpEndpoint(port: localPort, targetPort: port)`.
7. Wrap the returned builder in the `ServiceResource` facade (identical mechanism to `LocalProjectSource` step 7), return it.

Resolution stays synchronous within `AddService()`, consistent with milestone 1a — only the port allocation and kubectl-argument construction happen at this point; the actual port-forward process is started and managed by Aspire/DCP afterward, same as `AddProject`'s build-on-run.

## Error Handling

- **Config errors** — missing `cluster.service` in the catalog, missing `context`, or a `port` resolvable from neither local config nor the catalog — fail fast at the `AddService()` call site, naming the service and the missing field. Same philosophy as milestone 1a's config errors.
- **Runtime errors** — `kubectl` not on `PATH`, an invalid context, a Service not found in the given namespace, or a port-forward that drops — are **not** `AddService()`-time exceptions. They surface through the `ExecutableResource`'s own state and logs in the Aspire dashboard, exactly like `-rebuilder` build failures already do for the local source. This falls out of delegating to `AddExecutable` rather than managing the `kubectl` process ourselves — no special-casing needed.

## Testing

- **Config parsing**: unit tests for the new `cluster` catalog block and local-config fields, covering each fail-fast path (missing `cluster.service`, missing `context`, `port` absent from both places).
- **`ClusterSource` orchestration**: unit tests for argument-building and precedence logic (`port` override vs. catalog default, `namespace` default), driven through a fake `IPortAllocator` for deterministic ports. No real socket binding or `kubectl` invocation in unit tests.
- **No automated cluster/integration test in v1** — there's no cheap way to stand up a real Kubernetes cluster in CI. Verification is a manual smoke test via the demo AppHost against a real dev cluster (or local `kind`/`minikube`), matching milestone 1a's "no automated dashboard/DCP runtime test" call.

## Explicitly Out of Scope for This Pass

- Protocol/scheme override — v1 assumes HTTP; TLS or non-HTTP forwarded services need their own design pass.
- Pod-selector targeting — Service name + port only, no label-selector-based forwarding.
- Explicit `kubeconfigPath` configuration — ambient kubectl config only.
- Auto/fallback source selection referencing cluster as a candidate — separate phase-2 item.
- Retry/reconnect logic if a port-forward drops mid-session — left to whatever restart behavior Aspire's executable resources already provide, not custom-built here.
