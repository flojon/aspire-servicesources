# Aspire.Hosting.ServiceSources — AppHost Source Design

**Status:** Draft
**Date:** 2026-08-16
**Scope:** A new `IServiceSource` implementation, `AppHostSource`, that resolves `AddService()` against a resource inside an external repo's own Aspire AppHost, spawned as a local subprocess. Builds on the milestone 1a architecture (see [milestone 1a design](2026-08-09-servicesources-design.md)) and the [cluster source design](2026-08-13-servicesources-cluster-source-design.md); addresses a scenario not covered by either.

## Problem

`LocalProjectSource` clones a repo and `AddProject`s a single csproj — one project, one resource. Some services aren't a bare project: they're an app that ships its own Aspire AppHost, which composes that service with its own dependencies (infra, other services). There's no way today to reuse that other team's AppHost as the source of truth for how to run their service; a developer would have to hand-translate its resource graph into `servicesources.yaml`/plain `AddProject` calls and keep it in sync by hand.

Aspire has no native nested-AppHost composition — a `DistributedApplication` is a single process with its own DCP session — so "importing" an external AppHost necessarily means running it as an independent process and bridging to it, not merging resource graphs in-process.

## Architecture

- New `AppHostSource : IServiceSource`, registered in `AddService()`'s internal source lookup under `"apphost"`, alongside `"local"` and `"cluster"`.
- Reuses `LocalProjectSource`'s clone/checkout/cache-directory logic unchanged (same `repository`/`ref`/`path` resolution, same cache directory under `ServiceSourcesConfigCache`) to materialize the external repo locally.
- Instead of `AddProject`, locates the AppHost project in the clone (`apphostProject`, catalog-configured — see Config Schema) and spawns it via `builder.AddExecutable("<name>-apphost", "dotnet", args: ["run", "--project", apphostProjectPath])`, mirroring `ClusterSource`'s pattern of delegating process lifecycle entirely to a real, already-registered Aspire resource (`ExecutableResource`) rather than managing the child process by hand.
- The spawned AppHost is given a distinct resource-service port (see Discovery) to avoid colliding with the parent AppHost's own dashboard/resource service.
- After spawning, `AppHostSource` connects to the child AppHost's resource service API (the same API Aspire's own dashboard uses) and waits for the configured `resource` name to report a running endpoint.
- The resolved endpoint is wrapped in the same `ServiceResource` facade used by `LocalProjectSource` and `ClusterSource`, so downstream `WithReference()` call sites are source-agnostic.
- Exact resource-service connection contract (env vars, auth token, gRPC vs. HTTP) is not yet verified against the current Aspire hosting SDK — flagged as an implementation-time research spike, not guessed at here.

## Config Schema

Following the same catalog-vs-local split as milestone 1a and cluster source: which project to run and which resource to consume are stable facts about the service, so they live in the catalog; developer-specific overrides live in local config.

`servicesources.yaml` (catalog):
```yaml
services:
  orders:
    repository: https://github.com/company/orders
    defaultRef: main
    apphost:
      project: AppHost/AppHost.csproj   # relative to repo root
      resource: orders-api               # resource name inside their AppHost graph to consume
```

`servicesources.local.json` (per-developer) — `"source": "apphost"` entries, reusing the existing `path`/`ref` override fields:
```json
{
  "services": {
    "orders": {
      "source": "apphost",
      "path": "/home/dev/src/orders",
      "overrides": {
        "services": {
          "orders-payments": { "ref": "feature/new-payments-api" }
        }
      }
    }
  }
}
```

- `apphost.project` and `apphost.resource` are catalog-only, same rationale as `cluster.service` in the cluster source design: they identify the shape of the external app, not an environment-specific choice.
- `overrides` (optional, local-only) is a nested `DeveloperConfigFile`-shaped block (see Child Config Injection) — only meaningful when the external repo itself uses ServiceSources.

## Child Config Injection

The child AppHost's own dependencies (its `servicesources.yaml`, its `AddPostgres()`/`AddRedis()` calls) are that repo's own concern by default — `AppHostSource` spawns it as-is, changing nothing. The one exception: if the developer configures `overrides` for an apphost-imported service, `AppHostSource` writes a `servicesources.local.json` into the cloned AppHost's own directory (before spawning) using the existing `DeveloperConfigFile` shape verbatim — no new schema. This is safe because the clone lives in ServiceSources' own cache directory, never a developer's real working copy, so there's nothing to clobber.

This only lets the parent redirect the child's *other ServiceSources-managed services* (e.g. pin one of the child's own dependencies to a branch). It does **not** let the parent redirect the child's raw infra resources (`AddPostgres()`, `AddRedis()`, etc.) — there's no override shape for that today; it's the same gap as the still-undesigned "Database/queue source switching" item in the [phase 2 backlog](2026-08-09-servicesources-phase2-future-work.md). Duplicate infra between parent and child remains an explicit, known gap, not solved here.

**Validation:** only checked when `overrides` is actually configured for that service — the common case (no overrides) never touches this and works whether or not the child uses ServiceSources at all. When `overrides` *is* configured, `AppHostSource` fails fast if the cloned repo has no `servicesources.yaml` in the AppHost project's directory, since the override file would otherwise be written but silently never read:

> `Service 'orders': developer overrides configured for apphost-imported service, but the cloned repo at '<path>' has no servicesources.yaml — overrides would be silently ignored.`

## Resolution Flow

1. Load & cache both config files (existing milestone 1a behavior). Look up developer config for `name`; dispatch to `AppHostSource` when `source` is `"apphost"`.
2. Look up the catalog entry for `name`. If it has no `apphost.project` or `apphost.resource`, fail fast.
3. Resolve the repo root exactly as `LocalProjectSource` does (`path` override → cache-and-clone).
4. Locate `apphost.project` under the repo root; fail fast if the project file doesn't exist (same check as `LocalProjectSource.ResolveProjectPath`'s project-file-not-found case).
5. If local config has `overrides` for this service: verify a `servicesources.yaml` exists in the AppHost project's directory (fail fast otherwise), then write a `servicesources.local.json` there containing the `overrides` block.
6. `builder.AddExecutable("<name>-apphost", "dotnet", args: ["run", "--project", apphostProjectPath])` with a distinct resource-service port assigned.
7. Connect to the child's resource service API; wait for `apphost.resource` to report a running endpoint.
8. Wrap the resolved endpoint in the `ServiceResource` facade (same mechanism as the other two sources), return it.

## Error Handling

- **Config errors** — missing `apphost.project`/`apphost.resource` in the catalog, `overrides` configured against a repo with no `servicesources.yaml` — fail fast at the `AddService()` call site, same philosophy as the other two sources.
- **Clone/checkout errors** — identical to `LocalProjectSource` (clone failure, checkout failure, repo-URL mismatch against an existing cache entry).
- **Runtime errors** — the child AppHost process failing to start, crashing, or its resource service never reporting the target resource as running — surface through the `ExecutableResource`'s own state/logs in the dashboard where possible; a bounded wait-for-resource timeout is needed at resolve time (exact duration TBD during planning) since `AddService()` resolution is otherwise synchronous.

## Testing

- **Config parsing**: unit tests for the new `apphost` catalog block, `overrides` local-config field, and each fail-fast path.
- **Path/validation logic**: unit tests for AppHost-project-not-found and the overrides-without-catalog-file check, following the same pure-function style as `LocalProjectSource.ResolveProjectPath`.
- **Process spawn + resource-service discovery**: integration-shaped, not unit-testable without a real Aspire resource service. Needs a smoketest script addition (precedent: `scripts/smoketest-cluster-source.sh`) rather than automated coverage in v1.

## Explicitly Out of Scope for This Pass

- Infra-level dedup between parent and child AppHost graphs (Postgres/Redis run twice) — same deferred item as the phase-2 backlog's "Database/queue source switching."
- Consuming more than one resource from a single spawned child AppHost.
- Auto-detection/auto-selection of `apphost` as a source when a repo happens to contain an AppHost project — explicit opt-in only (`source: apphost` in config); a repo may have an AppHost for unrelated reasons (CI, their own local dev) that isn't meant for external consumption.
- Non-HTTP resource-service transport edge cases, TLS between parent and child resource service.
- Retry/reconnect if the child AppHost process dies mid-session.
