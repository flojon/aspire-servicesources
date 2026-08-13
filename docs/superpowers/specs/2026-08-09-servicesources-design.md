# Aspire.Hosting.ServiceSources — Milestone 1a Design

**Status:** Approved
**Date:** 2026-08-09
**Scope:** `AddService()` backed by the local-project source only. Cluster/container/external-endpoint sources, deferred/parallel resolution, and everything else in the original brief are out of scope — see the companion [phase 2 doc](2026-08-09-servicesources-phase2-future-work.md).

## Problem

.NET Aspire's `AddProject<T>()` assumes a service lives in the AppHost's own solution (a source-generated `Projects.*` type backed by a `<ProjectReference>`). In a real microservice environment, services live in separate repositories, and different developers want to run different subsets of them locally versus consuming a shared source. The AppHost should describe *what* the application depends on (`builder.AddService("orders")`); developer-local configuration should decide *where* that dependency comes from, without ever touching the AppHost's `.csproj`/`.sln`.

## Prior research (see project memory for full detail)

- `AddProject(string name, string projectPath)` is a **public**, non-generic API — it builds the same `ProjectResource` as the generic overload, purely from a path. No `.csproj`/`.sln` changes required. This is the foundation the local source builds on.
- No official Aspire building block exists for "point at an already-running external service" — `ExternalServiceResource` is sealed and lacks `IResourceWithEndpoints` (open upstream bug). Not needed for this milestone (local-only), but relevant to phase 2.
- ~~Path-based `AddProject` targets outside the AppHost's own MSBuild graph are not auto-built by DCP~~ — **superseded by end-to-end spike (2026-08-10):** this was true per upstream issues microsoft/aspire#2154/#10920 at the time of the initial research, but empirically, against Aspire.Hosting 13.4.6, `AddProject(name, path)` on an out-of-graph project **now builds and runs correctly with no explicit build step from this package at all**. `AddProject` registers a companion `<name>-rebuilder` resource alongside the real one, and a from-scratch test (bin/obj deleted, no manual `dotnet build` call) reached `Running` and served correctly. The upstream centralized-build proposal appears to have shipped. This removes an entire planned component (our own build step + build-serialization lock) from the design — see Resolution Flow below.

  **Correction (verified 2026-08-13, real `aspire run` against 13.4.6, `--include-hidden` resource listing):** the core conclusion above still holds — no build step needed from this package, the out-of-graph project builds and runs correctly from clean `bin`/`obj`. But the mechanism attribution is wrong: it is *not* the `<name>-rebuilder` companion that performs this build. `-rebuilder` is a hidden resource (per `Aspire.Hosting.dll`'s own doc comments: "runs 'dotnet build' **on demand** via the rebuild command") wired to the dashboard's manual "Rebuild" command, and it stays `NotStarted` through a normal run, including from clean `bin`/`obj`. What actually builds the project is the primary resource's own process launch — `dotnet run --project <path>`, which does its own implicit restore+build. Any future design/plan text should not claim `-rebuilder` reaches `Running` at startup.
- Two community prior-art packages (`Aspire.PolyRepo`, `Aspire.ExternalProject`) were reviewed in depth. Neither solves the actual problem this package exists to solve (both resolve the backend at AppHost-authoring time, not at runtime from developer config), so no dependency is taken on either — their approaches inform this design (delegate to Aspire's own `AddProject`/`AddExecutable`, use `ResourceNotificationService` rather than reflection for any future PID needs, avoid PolyRepo's destructive hard-reset-on-update behavior).

## Architecture

### `ServiceResource`
A thin facade class implementing `IResourceWithServiceDiscovery` (a pure marker interface extending `IResourceWithEndpoints`/`IResource` with no extra members — required so it can be passed directly into `WithReference()`, confirmed below). **It is never registered with Aspire's resource model** (no `AddResource` call) — it only ever wraps a builder for a real, already-registered Aspire resource (a `ProjectResource` in this milestone). Its endpoint annotations are copied/shared from the wrapped resource's own `EndpointAnnotation`, so `GetEndpoint(name)` resolves to the exact same endpoint identity as the real resource.

This keeps the package entirely on Aspire's supported orchestration path: DCP and the dashboard only ever see the real `ProjectResource`, never a custom type they don't know how to run. The facade exists purely so `AddService()` has one stable C# return type today, and can keep returning that same type once a cluster/container source exists later without callers needing to change.

**Confirmed via spike (2026-08-09), against Aspire.Hosting 13.4.6 on .NET 10:** `IDistributedApplicationBuilder.CreateResourceBuilder<T>(T resource)` is a real public API distinct from `AddResource<T>`, and it does **not** add the resource to `builder.Resources` — verified empirically (resource count stayed at 0 after calling it). A facade built this way was passed directly into `consumer.WithReference(facadeBuilder)` and it succeeded without throwing, and the facade never appeared in the final resource list. Copying the real resource's `EndpointAnnotation` instance onto the facade made `facade.GetEndpoint(name)` resolve to the identical endpoint. The one correction from the original design: `WithReference(IResourceBuilder<TDestination>)` requires `TDestination : IResourceWithServiceDiscovery`, not `IResourceWithEndpoints` as first assumed — trivial to satisfy since it adds no members. This was the single biggest open risk in the design and it's now fully validated; no fallback needed. Spike code lives outside the repo (throwaway), not committed.

### `IServiceSource` (internal)
```csharp
internal interface IServiceSource
{
    IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
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
7. Call Aspire's real `AddProject(name, resolvedPath)`, wrap the returned builder in the `ServiceResource` facade, return it. **No explicit build step is needed** — confirmed via spike, `AddProject` on an out-of-graph path builds it correctly on its own via its companion `-rebuilder` resource, even from a completely clean `bin`/`obj`.

Resolution happens **synchronously**, inline within the `AddService()` call, in the order services are declared in `Program.cs`. This was chosen deliberately over deferring resolution to a `BeforeStartEvent`-driven parallel phase: synchronous resolution means the facade can hold a direct reference to the already-registered real resource (trivial delegation, no lazy value-provider machinery), and any failure surfaces immediately at the exact `AddService()` call site with a clear cause. The cost is that AppHost startup blocks on cloning local-sourced projects on a genuinely cold cache (first clone of a repo); once cloned, "already cloned" is a fast existence check, and the actual build now happens inside Aspire's own orchestration rather than blocking `AddService()` at all. Parallel/deferred resolution across multiple cold services is an explicit phase 2 item.

**Confirmed via end-to-end spike (2026-08-10), against Aspire.Hosting 13.4.6 on .NET 10, using the Aspire CLI-provisioned DCP orchestrator:** the full flow above was run for real against a throwaway git repo with a default branch, a tag, and a feature branch. LibGit2Sharp cloned it, checked out the non-default `feature/v2-marker` ref, and `AddProject` (with no manual build step) was wired through the `ServiceResource` facade exactly as designed. The AppHost reached `Running` state and an HTTP request against the facade's resolved endpoint returned the feature branch's content, proving ref resolution, the facade's endpoint delegation, and Aspire's own build-on-run all work correctly together, end to end. One environment note for whoever runs this next: a plain `dotnet build`/`dotnet run` on a hand-written AppHost `.csproj` is not enough on its own — the project must import the `Aspire.AppHost.Sdk` MSBuild SDK (`<Sdk Name="Aspire.AppHost.Sdk" Version="..." />`) and set `<IsAspireHost>true</IsAspireHost>`, or DCP never actually starts and the app hangs with no resource ever reaching `Running`. The Aspire CLI (`aspire restore`) is the easiest way to get this right; it was used to provision/verify the setup before the direct `dotnet run` test above was run.

## Error Handling

Fail fast and loud, at the `AddService()` call site, with an exception that names the service and the exact step that failed: missing config entry, clone failure, missing ref, or missing project file. (Build failures now surface through Aspire's own resource-state/dashboard machinery for the `-rebuilder` resource, not as an `AddService()`-time exception, since building is no longer this package's responsibility.) No silent fallback to another source, no partial/degraded resource — matches the brief's explicit deferral of automatic fallback to a later milestone.

## Testing

- **Config parsing** (`ServiceMetadata`/`ServiceDeveloperConfig` loaders): unit tests against sample YAML/JSON fixtures, including error paths (missing service, invalid `source` value).
- **`LocalProjectSource` orchestration**: git operations sit behind a small `IGitClient` interface so cache-path computation, ref precedence, and error wrapping are unit-testable without real git/network calls. (No process-execution abstraction is needed — building is Aspire's responsibility now, not this package's.)
- **One real integration test**: end-to-end `AddService()` against a throwaway fixture git repo (a local bare repo checked into the test project, cloned via `file://`), verifying the resulting `IResourceBuilder<ServiceResource>` produces a working endpoint reference. This is the one test requiring a real `dotnet` SDK in CI.
- No automated dashboard/DCP runtime test in v1 — covered instead by manually running a sample demo AppHost, which should be built alongside this package as a smoke test.

## Explicitly Out of Scope for This Milestone

Per the original brief: automatic dependency discovery, imported AppHost composition, multiple infrastructure instances, automatic source fallback, CLI configuration UI, central service registry, cluster/container/external-endpoint sources, config file walk-up discovery, deferred/parallel resolution, and any repo auto-update command. See the phase 2 doc for what's been captured about these for future design passes.
