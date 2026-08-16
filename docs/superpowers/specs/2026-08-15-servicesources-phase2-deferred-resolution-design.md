# Aspire.Hosting.ServiceSources — Deferred / Parallel Resolution (Phase 2)

**Date:** 2026-08-15
**Status:** Design — ready for implementation planning.
**Resolves:** GitHub issue #2. Companion to the [milestone 1a design](2026-08-09-servicesources-design.md) (read that first for the facade/config/error-handling model this extends) and the [phase 2 future-work doc](2026-08-09-servicesources-phase2-future-work.md) § "Deferred / parallel resolution", which first raised this and flagged both open risks resolved below.

## Motivation

Milestone 1a resolves every `"local"`-sourced service synchronously and in-order inside `AddService()`: each cold service blocks AppHost startup on its own git clone before the next one even starts. This was a deliberate simplicity trade-off for milestone 1a (see the milestone 1a doc's Resolution Flow section), accepted because it's mostly a first-run tax — once cloned, resolution is a fast existence check. But with several cold services declared in the same `Program.cs`, that tax is paid serially instead of in parallel.

This design moves `"local"`-source resolution to a `BeforeStartEvent`-subscribed hook that resolves every pending service in parallel via `Task.WhenAll`, before DCP starts anything.

## Risk resolution

The original issue named two open risks blocking this design. Both are resolved here — not from documentation alone, but confirmed against real Aspire.Hosting 13.4.6 source and an end-to-end spike (a real AppHost, run for real, not a mock):

**Risk 1 — "`GetEndpoint()` needs to return a lazily-resolving value provider."** It doesn't need one. `ResourceExtensions.GetEndpoint(IResourceWithEndpoints, string)` (Aspire's own extension method, unchanged, used as-is) already returns a name-based `EndpointReference` whenever the named `EndpointAnnotation` doesn't exist on the resource yet at call time (confirmed from `ResourceExtensions.cs` / `EndpointReference.cs` in `dotnet/aspire`). That `EndpointReference` caches nothing eagerly — its `GetEndpointAnnotation()` looks the annotation up against `Resource.Annotations` lazily, the first time `GetValueAsync()` is actually invoked. Likewise, `WithReference(IResourceBuilder<IResourceWithServiceDiscovery>)` doesn't snapshot endpoints at call time either — `ApplyEndpoints` stores an `EndpointReferenceAnnotation` holding a reference to the *resource object itself* plus `UseAllEndpoints = true`, and registers an `EnvironmentCallbackAnnotation` that enumerates `Annotations.OfType<EndpointAnnotation>()` only when environment variables are actually computed for the destination resource. `ServiceResource` and its `CreateFacade` helper need **no code change** for this — the facade just needs to exist (stable object identity) before `WithReference()` is called, and receive its real `EndpointAnnotation`s at some point before environment variables are computed.

**Risk 2 — "confirming exactly when Aspire's pipeline reads endpoint values relative to `BeforeStartEvent` completion."** Confirmed on both counts:
  - *Ordering:* `DistributedApplication.StartAsync()` calls `ExecuteBeforeStartHooksAsync()` (which publishes `BeforeStartEvent` and awaits every subscriber) strictly *before* `_host.StartAsync()` — the call that actually starts DCP and begins resource orchestration, including environment-variable computation. This is a plain sequential `await`, not a race, confirmed by reading `DistributedApplication.cs`.
  - *Mutability:* a brand-new resource (`builder.AddProject`/`AddExecutable`, called from *inside* a `BeforeStartEvent` handler, i.e. after `builder.Build()` has already run) is picked up correctly by DCP with no error. Verified with a real throwaway spike: a facade resource was created and referenced via `WithReference()` *before* any endpoint annotation existed on it; a `BeforeStartEvent` handler then registered a brand-new real backing resource and copied its `EndpointAnnotation` onto the pre-existing facade; the consuming resource's actual process environment was inspected once running and contained the exact correct injected value (`services__backend__http__0=http://localhost:23456`). Full round trip, no mocks.

Both risks are eliminated as a result: the milestone-1a facade shape is reused unchanged, and the only change is *when* its `Annotations` collection gets populated.

## Architecture

### `IServiceSource` — unchanged

`Resolve()` keeps its exact synchronous signature:

```csharp
internal interface IServiceSource
{
    IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config);
}
```

Deferred resolution is entirely an implementation detail of `LocalProjectSource`. `ClusterSource` is untouched: its `Resolve()` does no blocking I/O today (config validation, a socket-based local port allocation, and in-memory `AddExecutable`/`WithHttpEndpoint` calls — the `kubectl port-forward` process itself is started later by DCP, independent of anything `AddService()` does), so there is nothing for it to gain from deferral. Unifying it onto the deferred path would touch tested, working code for no functional or interface-simplicity benefit, since `IServiceSource` doesn't fork into two shapes either way.

### `ServiceResource` — unchanged

No lazy value-provider wrapper is added, per the risk resolution above. `CreateFacade` keeps its exact current signature and behavior; `LocalProjectSource` just calls it later than it does today (after the real project resource exists), against the *same* facade object it already created and returned up front.

### `LocalProjectSource` — resolution split into two phases

```csharp
internal sealed class LocalProjectSource(IGitClient gitClient) : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var facade = ServiceResource.CreateEmptyFacade(builder, serviceName);

        PendingLocalResolutions.For(builder).Add(new PendingResolution(
            serviceName, metadata, config, facade, gitClient));

        return facade;
    }
}
```

`ServiceResource.CreateFacade` is split into two entry points: `CreateEmptyFacade(builder, name)` (creates the facade with no endpoint annotations, for the deferred case) and the existing `CreateFacade(builder, name, realResource)` (creates-and-immediately-populates, kept for `ClusterSource`'s synchronous case — internally, `CreateFacade` becomes `CreateEmptyFacade` followed by the existing annotation-copy loop, so the copy logic isn't duplicated).

### `PendingLocalResolutions` — new, per-builder pending queue

Keyed the same way `ServiceSourcesConfigCache` already keys its per-builder config cache:

```csharp
internal sealed class PendingLocalResolutions
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, PendingLocalResolutions> Cache = new();

    private readonly List<PendingResolution> _pending = [];

    public static PendingLocalResolutions For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static b =>
        {
            var store = new PendingLocalResolutions();
            b.Eventing.Subscribe<BeforeStartEvent>((_, ct) => store.ResolveAllAsync(b, ct));
            return store;
        });

    public void Add(PendingResolution pending) => _pending.Add(pending);

    private async Task ResolveAllAsync(IDistributedApplicationBuilder builder, CancellationToken cancellationToken)
    {
        // see Resolution Flow below
    }
}
```

The `Eventing.Subscribe` call happens exactly once per builder, the first time any `"local"`-sourced service is added — a direct consequence of `ConditionalWeakTable.GetValue`'s factory running only on first access per key.

`PendingResolution` is a plain data holder carrying what the deferred phase needs per service:

```csharp
internal sealed record PendingResolution(
    string ServiceName, ServiceMetadata Metadata, ServiceDeveloperConfig Config,
    IResourceBuilder<IResourceWithServiceDiscovery> Facade, IGitClient GitClient);
```

## Resolution Flow (`BeforeStartEvent` handler)

1. **Parallel phase.** For every pending item, run `LocalProjectSource.ResolveProjectPath(...)` (existing, unchanged helper — clone-if-missing, checkout ref, resolve project path) on its own thread-pool thread via `Task.Run`, awaited together with `Task.WhenAll`. Each wrapped task catches its own exception internally and returns a result (`ProjectPath` on success, or `ServiceName` + `Exception` on failure) rather than letting the exception propagate — `Task.WhenAll` alone only surfaces the *first* exception, and every failure needs to survive to the aggregation step below. This phase is safe to run concurrently because each service only ever touches its own cache directory; it doesn't touch any Aspire builder state. It assumes `IGitClient`'s underlying implementation (`LibGit2SharpGitClient`) is safe for concurrent calls against *different* repository directories — a reasonable assumption for LibGit2Sharp's per-repository API surface, but one the implementation plan's parallelism test (below) should exercise for real rather than take on faith, since this is the one piece of this design not directly spiked.
2. **Aggregation.** If any result failed, throw one `ServiceSourcesConfigurationException` listing every failed service name and its cause (reusing the existing per-service error messages `ResolveProjectPath` already produces). No `AddProject` call happens for *any* service in this case — matches milestone 1a's "no partial/degraded resource" policy: the whole app fails to start together, not resource-by-resource.
3. **Sequential phase.** Only reached if every resolution succeeded. For each pending item, in order: call `builder.AddProject(serviceName, projectPath)`, then copy the resulting `EndpointAnnotation`s onto that item's already-returned facade (the same loop `ServiceResource.CreateFacade` runs today). This phase is intentionally sequential and runs on the `BeforeStartEvent` handler's own thread — `builder.Resources` and each facade's `Annotations` collection are ordinary, non-thread-safe collections, and this phase is fast (in-memory only), so there's no benefit to parallelizing it and real risk in doing so.

## Concurrency and Ordering Notes

- **Cache-directory thread safety.** The parallel phase's only shared-state touch point outside `IGitClient` itself is `ServiceSourcesConfigCache.GetCacheDirectory(builder)`, called once per pending item. This is already safe to call concurrently: `ConditionalWeakTable<>.GetValue` is documented thread-safe, and once `LoadedConfig` is loaded, `GetCacheDirectory` does only pure path-string computation (`ExpandHome`, `Path.GetFullPath`) over that config — no shared mutable state is written. No additional locking is needed; called out here so it doesn't need re-deriving later.
- **`BeforeStartEvent` subscriber ordering.** This design's actual guarantee — every `BeforeStartEvent` handler completes before `_host.StartAsync()` starts DCP and computes environment variables (Risk 2 above) — doesn't require ServiceSources' handler to run before or after any *other* independent `BeforeStartEvent` subscriber the consuming AppHost or another package registers. If other AppHost code also hooks `BeforeStartEvent`, it must not assume a `"local"`-sourced service's facade is already populated at that point; the safe, guaranteed integration point for reading a resolved endpoint is the environment-callback path used by `WithReference()` (which runs after all `BeforeStartEvent` handlers), not another `BeforeStartEvent` handler.

## Error Handling

Unchanged in spirit from milestone 1a — fail fast and loud, no silent fallback, no partial/degraded resource — but the mechanics shift with resolution timing:

- All pending resolutions run to completion before any failure is reported, so a run with two independently-broken services (e.g. two bad `ref`s) reports both in the same exception instead of only the first, requiring a second re-run to discover the next one.
- The exception's C# call site is now the `BeforeStartEvent` handler rather than the original `AddService()` line in `Program.cs`, since the actual failure is only discovered later. Each failure's message still names its service explicitly (unchanged from milestone 1a's per-service error strings), so the cause remains just as identifiable — only the stack-trace attribution changes. Worth calling out to anyone debugging a startup failure by stack trace instead of by message.
- If zero services fail, no behavior changes from milestone 1a's error contract at all.

## Testing

- **`PendingLocalResolutions`**: unit tests that two `Add()` calls against the same builder share one subscription (only one `BeforeStartEvent` handler fires), and that two different builders (as in separate unit tests / `ServiceSourcesConfigCache`'s own test pattern) get independent queues.
- **Aggregate failure formatting**: unit test with two deliberately-broken pending resolutions (mocked `IGitClient` throwing for both) asserting the resulting exception names both services and both underlying causes.
- **Parallelism**: an integration test with two cold fixture repos (extending the existing bare-repo fixture pattern from `AddServiceIntegrationTests.cs`) whose clones are individually slow enough (an artificial delay in a test-only `IGitClient` wrapper) to make serial-vs-parallel timing observably different — asserting wall-clock time is closer to a single clone's duration than to the sum of both, rather than asserting an exact parallelism guarantee.
- **End-to-end**: extend the existing real-`AddService()`-against-a-throwaway-git-repo integration test to cover the deferred path — assert the resource reaches `Running` and its endpoint resolves correctly, the same outcome milestone 1a's own end-to-end test already checks, just via the new timing.
- No new manual smoke test is planned beyond the existing `AddServiceIntegrationTests.cs` coverage — unlike `ClusterSource` (which needed a real cluster to smoke-test against), this change has no external dependency the automated test suite can't already exercise.

## Explicitly Out of Scope for This Pass

`ClusterSource` and any future non-`"local"` sources (container, external-endpoint — see the phase 2 future-work doc) are not touched by this design; each decides independently, per `IServiceSource` implementation, whether it has anything worth deferring. Config file walk-up, repo auto-update, and all other phase-2-and-beyond items remain exactly as scoped in the phase 2 future-work doc.
