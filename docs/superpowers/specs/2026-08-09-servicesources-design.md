# Aspire.Hosting.ServiceSources — Milestone 1a Design

**Status:** Approved
**Date:** 2026-08-09
**Scope:** `AddService()` backed by the local-project source only. Cluster/container/external-endpoint sources, deferred/parallel resolution, and everything else in the original brief are out of scope — see the companion [phase 2 doc](2026-08-09-servicesources-phase2-future-work.md).

## Problem

.NET Aspire's `AddProject<T>()` assumes a service lives in the AppHost's own solution (a source-generated `Projects.*` type backed by a `<ProjectReference>`). In a real microservice environment, services live in separate repositories, and different developers want to run different subsets of them locally versus consuming a shared source. The AppHost should describe *what* the application depends on (`builder.AddService("orders")`); developer-local configuration should decide *where* that dependency comes from, without ever touching the AppHost's `.csproj`/`.sln`.

## Prior research (see project memory for full detail)

- `AddProject(string name, string projectPath)` is a **public**, non-generic API — it builds the same `ProjectResource` as the generic overload, purely from a path. No `.csproj`/`.sln` changes required. This is the foundation the local source builds on.
- No official Aspire building block exists for "point at an already-running external service" — `ExternalServiceResource` is sealed and lacks `IResourceWithEndpoints` (open upstream bug). Not needed for this milestone (local-only), but relevant to phase 2.
- Path-based `AddProject` targets outside the AppHost's own MSBuild graph are **not** auto-built by DCP (`dotnet run --no-build` assumes the AppHost's own build already built them) — this package must run its own `dotnet build` step before handing off.
- Two community prior-art packages (`Aspire.PolyRepo`, `Aspire.ExternalProject`) were reviewed in depth. Neither solves the actual problem this package exists to solve (both resolve the backend at AppHost-authoring time, not at runtime from developer config), so no dependency is taken on either — their approaches inform this design (delegate to Aspire's own `AddProject`/`AddExecutable`, use `ResourceNotificationService` rather than reflection for any future PID needs, avoid PolyRepo's destructive hard-reset-on-update behavior).

## Architecture

### `ServiceResource`
A thin facade class implementing `IResourceWithEndpoints` (and the minimal `IResource` surface). **It is never registered with Aspire's resource model** (no `AddResource` call) — it only ever wraps a builder for a real, already-registered Aspire resource (a `ProjectResource` in this milestone). Its `GetEndpoint(name)` delegates directly to the wrapped resource's `GetEndpoint(name)`.

This keeps the package entirely on Aspire's supported orchestration path: DCP and the dashboard only ever see the real `ProjectResource`, never a custom type they don't know how to run. The facade exists purely so `AddService()` has one stable C# return type today, and can keep returning that same type once a cluster/container source exists later without callers needing to change.

**Open implementation risk to spike first:** this depends on being able to wrap a resource in an `IResourceBuilder<T>` without adding it to `builder.Resources` (e.g. a `CreateResourceBuilder`-style API distinct from `AddResource`). Not confirmed from source research alone — first thing to verify once the project is scaffolded against a real Aspire SDK. If it turns out no such API exists, the fallback is registering the facade normally and forwarding `With*` calls to the backing resource instead of the other way around — a larger change, flag immediately if the spike fails.

### `IServiceSource` (internal)
```csharp
internal interface IServiceSource
{
    IResourceBuilder<IResourceWithEndpoints> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config);
}
```
The extensibility seam the package is named after. Milestone 1a registers exactly one implementation, `LocalProjectSource`, in an internal `source string -> IServiceSource` lookup. Any `source` value other than `"local"` throws a clear "source '{value}' is not implemented yet" error — no silent fallback.

### `AddService(this IDistributedApplicationBuilder builder, string name)`
Public entry point. Loads and caches both config files (once per `IDistributedApplicationBuilder` instance), looks up `name`, dispatches to the matching `IServiceSource`, wraps the result in a `ServiceResource` facade.

### `LocalProjectSource`
Does the real work — see Resolution Flow below.

## Config Schema

Two files, both discovered by checking the AppHost project's own directory only (no walk-up in v1).

**`servicesources.yaml`** — committed, shared service metadata/catalog:
```yaml
services:
  orders:
    repository: https://github.com/company/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main          # optional; branch, tag, or commit SHA
```

**`servicesources.local.json`** — gitignored, per-developer source choice:
```json
{
  "cacheDirectory": "~/.servicesources/repos",
  "services": {
    "orders": { "source": "local" },
    "payments": {
      "source": "local",
      "path": "/home/dev/code/payments",
      "ref": "feature/new-checkout"
    }
  }
}
```

Rules:
- `source` is required per service; only `"local"` is accepted in this milestone.
- `path` (optional): if present, that directory is used as-is and never touched by git (developer-managed checkout — no clone, no checkout, no update, ever). If absent, the repo is managed under `cacheDirectory` (default `~/.servicesources/repos/<repo-name>/`).
- `ref` (optional, ignored when `path` is set): overrides the catalog's `defaultRef`. If neither is set, whatever `git clone` gives you as the default branch is used.
- A service referenced by `AddService()` but missing from either config file is a fail-fast error naming the service and the missing file.

## Resolution Flow (managed-clone case)

1. Load & cache both config files from the AppHost directory (fail fast if `name` isn't present in both).
2. Look up developer config for `name`. If `source` isn't `"local"`, throw "not implemented yet."
3. If `path` is set, use it as-is (skip steps 4–5 entirely — no git operations touch developer-managed checkouts).
4. Otherwise compute the cache path from `repository`. If it doesn't exist, clone via LibGit2Sharp. If it already exists, leave it alone — **no automatic pull/update in v1** (this was a deliberate choice to avoid the destructive hard-reset-on-update behavior seen in prior art; a manual update command is phase 2, not silent background mutation).
5. Checkout the resolved `ref` (dev-config `ref` → catalog `defaultRef` → repo default branch) via LibGit2Sharp; fail clearly if the ref doesn't exist.
6. Resolve `project` (relative path from metadata) against the clone/checkout root; fail clearly if the `.csproj` isn't there.
7. Acquire a shared, package-wide `SemaphoreSlim` build lock, run `dotnet build` on that path, release. Non-zero exit throws with captured build output attached. (This serializes ServiceSources' own builds against each other; it does **not** coordinate with the AppHost's own `ProjectReference`-graph builds — that's an acknowledged, documented upstream Aspire gap, not something this package attempts to fix in v1.)
8. Call Aspire's real `AddProject(name, resolvedPath)`, wrap the returned builder in the `ServiceResource` facade, return it.

Resolution happens **synchronously**, inline within the `AddService()` call, in the order services are declared in `Program.cs`. This was chosen deliberately over deferring resolution to a `BeforeStartEvent`-driven parallel phase: synchronous resolution means the facade can hold a direct reference to the already-built real resource (trivial delegation, no lazy value-provider machinery), and any failure surfaces immediately at the exact `AddService()` call site with a clear cause. The cost is that AppHost startup blocks while local-sourced projects are cloned/built — expensive only on a genuinely cold cache (first clone of a repo, or a build after source changes); once cloned, "already cloned" is a fast existence check and `dotnet build` is incremental. Parallel/deferred resolution across multiple cold services is an explicit phase 2 item.

## Error Handling

Fail fast and loud, at the `AddService()` call site, with an exception that names the service and the exact step that failed: missing config entry, clone failure, missing ref, missing project file, or build failure (with captured build output). No silent fallback to another source, no partial/degraded resource — matches the brief's explicit deferral of automatic fallback to a later milestone.

## Testing

- **Config parsing** (`ServiceMetadata`/`ServiceDeveloperConfig` loaders): unit tests against sample YAML/JSON fixtures, including error paths (missing service, invalid `source` value).
- **`LocalProjectSource` orchestration**: git and process execution sit behind small interfaces (`IGitClient`, `IProcessRunner`) so cache-path computation, ref precedence, build-lock serialization, and error wrapping are unit-testable without real git/network/dotnet calls.
- **One real integration test**: end-to-end `AddService()` against a throwaway fixture git repo (a local bare repo checked into the test project, cloned via `file://`), verifying the resulting `IResourceBuilder<ServiceResource>` produces a working endpoint reference. This is the one test requiring a real `dotnet` SDK in CI.
- No automated dashboard/DCP runtime test in v1 — covered instead by manually running a sample demo AppHost, which should be built alongside this package as a smoke test.

## Explicitly Out of Scope for This Milestone

Per the original brief: automatic dependency discovery, imported AppHost composition, multiple infrastructure instances, automatic source fallback, CLI configuration UI, central service registry, cluster/container/external-endpoint sources, config file walk-up discovery, deferred/parallel resolution, and any repo auto-update command. See the phase 2 doc for what's been captured about these for future design passes.
