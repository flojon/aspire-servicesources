# ServiceSources Phase 2 Deferred/Parallel Resolution Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move `"local"`-source resolution from synchronous, in-order work inside `AddService()` to a `BeforeStartEvent`-subscribed hook that clones/checks-out every pending `"local"` service in parallel via `Task.WhenAll`, before DCP starts anything.

**Architecture:** `LocalProjectSource.Resolve()` now returns an empty `ServiceResource` facade immediately and enqueues a `PendingResolution` into a new per-builder `PendingLocalResolutions` store (keyed the same way `ServiceSourcesConfigCache` is keyed). On first use per builder, that store subscribes once to `BeforeStartEvent`. When the event fires, every pending item's `LocalProjectSource.ResolveProjectPath` (existing, unchanged helper) runs on its own thread-pool thread via `Task.WhenAll`; failures are collected and reported together; on full success, `builder.AddProject(...)` runs sequentially for each item and its `EndpointAnnotation`s are copied onto the already-returned facade. `ServiceResource.CreateFacade` is split into `CreateEmptyFacade` (new) and a `CopyEndpointAnnotations` helper (new) so the copy logic isn't duplicated between the synchronous `ClusterSource` path (untouched) and this new deferred path.

**Tech Stack:** C# / .NET 10 (`net10.0`), Aspire.Hosting 13.4.6, xUnit 2.9.3, LibGit2Sharp 0.32.0. No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-08-15-servicesources-phase2-deferred-resolution-design.md`

## Global Constraints

- `IServiceSource.Resolve()` keeps its exact synchronous signature — deferral is entirely an implementation detail of `LocalProjectSource`.
- `ClusterSource` is **not touched** by this plan — its `Resolve()` has no blocking I/O to defer (per spec's Architecture section).
- No lazy value-provider wrapper is added to `ServiceResource` — `CreateFacade`'s existing signature and behavior are preserved exactly; only its internals are split into two reusable pieces.
- Fail-fast, no partial/degraded resource: if any pending `"local"` service fails to resolve, `AddProject` must not run for **any** pending service in that batch.
- All pending resolutions must run to completion before any failure is reported — a batch with two independently-broken services must report both in one exception, not just the first.
- The parallel phase (`ResolveProjectPath` calls) must not touch any Aspire builder state (`builder.Resources`, `builder.AddProject`, any facade's `Annotations`) — only the sequential phase after aggregation may do that.
- No new manual smoke test is planned for this feature (per spec's Testing section) — automated coverage only.

---

## File Structure

- `src/Aspire.Hosting.ServiceSources/ServiceResource.cs` — modify. Split `CreateFacade` into `CreateEmptyFacade(builder, name)` + `CopyEndpointAnnotations(facade, realResource)`; `CreateFacade` becomes a thin composition of both, unchanged from the caller's perspective.
- `src/Aspire.Hosting.ServiceSources/Sources/PendingLocalResolutions.cs` — new. `PendingResolution` record and `PendingLocalResolutions` per-builder pending queue, including the `BeforeStartEvent` handler (`ResolveAllAsync`) that runs the parallel phase, aggregates failures, and runs the sequential `AddProject`/copy phase.
- `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs` — modify. `Resolve()` creates an empty facade and enqueues a `PendingResolution` instead of resolving synchronously. `ResolveProjectPath` and its private helpers are unchanged — they're now called from `PendingLocalResolutions` instead of from `Resolve()` directly.
- `test/Aspire.Hosting.ServiceSources.Tests/ServiceResourceTests.cs` — modify. Add tests for `CreateEmptyFacade` and `CopyEndpointAnnotations`.
- `test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs` — modify. Add a real (not faked) concurrent-clone test against two different on-disk repos, using the same `LibGit2SharpGitClient` instance — this is the one assumption in the design ("safe for concurrent calls against different repository directories") the spec calls out as not directly spiked and asks the implementation plan to exercise for real.
- `test/Aspire.Hosting.ServiceSources.Tests/Sources/PendingLocalResolutionsTests.cs` — new. Subscription-sharing, per-builder isolation, aggregate-failure-formatting, and orchestration-level parallelism (timing) tests, using a fake `IGitClient`.
- `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs` — modify. Add a test that `Resolve()` no longer clones/registers synchronously.
- `test/Aspire.Hosting.ServiceSources.Tests/AddServiceIntegrationTests.cs` — modify. The existing end-to-end test must publish `BeforeStartEvent` before its assertions now hold; also assert nothing happens before that publish.
- `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs` — modify. Two existing `"local"`-source tests (`AddService_LocalSourceWithPathOverride_ReturnsFacadeWrappingRealProject`, `AddService_RelativePathOverride_ResolvesRelativeToAppHostDirectoryNotProcessCwd`) assert `builder.Resources` contains `"orders"` immediately after `AddService()` — true today, false after Task 4 defers registration to `BeforeStartEvent`. Both must publish `BeforeStartEvent` before that assertion. (The other two tests in this file — `AddService_UnknownSource_...` and the `"cluster"`-source tests — are unaffected: `ClusterSource` stays synchronous.)

---

## Task 1: `ServiceResource` — split `CreateFacade` into `CreateEmptyFacade` + `CopyEndpointAnnotations`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceResource.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/ServiceResourceTests.cs`

**Interfaces:**
- Produces: `ServiceResource.CreateEmptyFacade(IDistributedApplicationBuilder builder, string name) : IResourceBuilder<IResourceWithServiceDiscovery>` — creates the facade, registers no annotations, does not add it to `builder.Resources`.
- Produces: `ServiceResource.CopyEndpointAnnotations<TResource>(IResourceBuilder<IResourceWithServiceDiscovery> facade, IResourceBuilder<TResource> realResource) where TResource : IResource` — copies every `EndpointAnnotation` from `realResource` onto `facade`.
- `ServiceResource.CreateFacade<TResource>(...)` keeps its exact existing signature and behavior (now implemented via the two methods above).

- [ ] **Step 1: Write the failing tests**

Add to `test/Aspire.Hosting.ServiceSources.Tests/ServiceResourceTests.cs` (after the existing `CreateFacade_CanBeUsedWithWithReference` test, before the `CreateFakeCsproj` helper):

```csharp
    [Fact]
    public void CreateEmptyFacade_IsNotRegisteredInBuilderResources()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var resourcesBeforeFacade = builder.Resources.Count;

        ServiceResource.CreateEmptyFacade(builder, "orders");

        Assert.Equal(resourcesBeforeFacade, builder.Resources.Count);
    }

    [Fact]
    public void CreateEmptyFacade_HasNoEndpointAnnotations()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var facade = ServiceResource.CreateEmptyFacade(builder, "orders");

        Assert.Empty(facade.Resource.Annotations.OfType<EndpointAnnotation>());
    }

    [Fact]
    public void CopyEndpointAnnotations_CopiesFromRealResourceOntoExistingFacade()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var facade = ServiceResource.CreateEmptyFacade(builder, "orders");
        var realProject = builder.AddProject("orders-real", CreateFakeCsproj())
            .WithHttpEndpoint(name: "http", port: 5001);

        ServiceResource.CopyEndpointAnnotations(facade, realProject);

        var realEndpoint = realProject.Resource.Annotations.OfType<EndpointAnnotation>().Single(a => a.Name == "http");
        var facadeEndpoint = facade.Resource.Annotations.OfType<EndpointAnnotation>().Single(a => a.Name == "http");
        Assert.Same(realEndpoint, facadeEndpoint);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceResourceTests"`
Expected: FAIL — `CreateEmptyFacade`/`CopyEndpointAnnotations` do not exist yet (compile error).

- [ ] **Step 3: Implement the split**

Replace the body of `src/Aspire.Hosting.ServiceSources/ServiceResource.cs` from the `CreateFacade` method onward with:

```csharp
    internal static IResourceBuilder<IResourceWithServiceDiscovery> CreateEmptyFacade(
        IDistributedApplicationBuilder builder, string name) =>
        builder.CreateResourceBuilder(new ServiceResource(name));

    internal static IResourceBuilder<IResourceWithServiceDiscovery> CreateFacade<TResource>(
        IDistributedApplicationBuilder builder, string name, IResourceBuilder<TResource> realResource)
        where TResource : IResource
    {
        var facade = CreateEmptyFacade(builder, name);
        CopyEndpointAnnotations(facade, realResource);
        return facade;
    }

    internal static void CopyEndpointAnnotations<TResource>(
        IResourceBuilder<IResourceWithServiceDiscovery> facade, IResourceBuilder<TResource> realResource)
        where TResource : IResource
    {
        foreach (var endpoint in realResource.Resource.Annotations.OfType<EndpointAnnotation>())
        {
            facade.Resource.Annotations.Add(endpoint);
        }
    }
}
```

(The class's opening brace, constructor, and doc comments above `CreateFacade` are unchanged — only the body from the old `CreateFacade` method to the closing brace is replaced.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceResourceTests"`
Expected: PASS — all `ServiceResourceTests` tests (existing and new) pass.

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceResource.cs test/Aspire.Hosting.ServiceSources.Tests/ServiceResourceTests.cs
git commit -m "Split ServiceResource.CreateFacade into CreateEmptyFacade + CopyEndpointAnnotations"
```

---

## Task 2: Real concurrent-clone safety test for `LibGit2SharpGitClient`

**Files:**
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs`

**Interfaces:**
- Consumes: `LibGit2SharpGitClient.Clone(string repositoryUrl, string destinationPath)` (existing, unchanged).
- Consumes: the existing private `CreateOriginRepo()` test helper in this file (creates a real, ephemeral on-disk git repo with a `main` commit containing `file.txt` = `"main content"`).

This task has no production-code change — it validates, with a real `LibGit2Sharp`-backed clone, the one assumption the design spec calls out as "not directly spiked": that `LibGit2SharpGitClient` is safe to call concurrently against two different repository directories. This must land before Task 3 relies on that assumption.

- [ ] **Step 1: Write the test**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs` (after the last existing test method, before the closing brace of the class):

```csharp
    [Fact]
    public async Task Clone_TwoDifferentRepositoriesConcurrently_BothSucceedWithoutCorruption()
    {
        var originA = CreateOriginRepo();
        var originB = CreateOriginRepo();
        var destinationA = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone-a");
        var destinationB = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone-b");
        var client = new LibGit2SharpGitClient();

        await Task.WhenAll(
            Task.Run(() => client.Clone(originA, destinationA)),
            Task.Run(() => client.Clone(originB, destinationB)));

        Assert.Equal("main content", File.ReadAllText(Path.Combine(destinationA, "file.txt")));
        Assert.Equal("main content", File.ReadAllText(Path.Combine(destinationB, "file.txt")));
    }
```

- [ ] **Step 2: Run the test**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LibGit2SharpGitClientTests.Clone_TwoDifferentRepositoriesConcurrently_BothSucceedWithoutCorruption"`
Expected: PASS. There is no separate red/green cycle here since no production code changes — this test exercises existing `LibGit2SharpGitClient` code as-is. If it fails, `LibGit2SharpGitClient` is **not** safe for concurrent use against different repos and this design's parallel-clone premise is broken — stop and escalate rather than proceeding to Task 3.

- [ ] **Step 3: N/A — no production code change in this task.**

- [ ] **Step 4: Run the full file's tests to confirm no regressions**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LibGit2SharpGitClientTests"`
Expected: PASS — all tests in the file, including the new one.

- [ ] **Step 5: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs
git commit -m "Verify LibGit2SharpGitClient is safe for concurrent clones of different repos"
```

---

## Task 3: `PendingResolution` + `PendingLocalResolutions` — the deferred orchestration

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Sources/PendingLocalResolutions.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/PendingLocalResolutionsTests.cs`

**Interfaces:**
- Consumes: `ServiceResource.CreateEmptyFacade` / `CopyEndpointAnnotations` (Task 1).
- Consumes: `LocalProjectSource.ResolveProjectPath(string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config, string cacheDirectory, string appHostDirectory, IGitClient gitClient) : string` (existing, unchanged).
- Consumes: `ServiceSourcesConfigCache.GetCacheDirectory(IDistributedApplicationBuilder builder) : string` (existing, unchanged).
- Produces: `internal sealed record PendingResolution(string ServiceName, ServiceMetadata Metadata, ServiceDeveloperConfig Config, IResourceBuilder<IResourceWithServiceDiscovery> Facade, IGitClient GitClient)`.
- Produces: `internal sealed class PendingLocalResolutions` with `static PendingLocalResolutions For(IDistributedApplicationBuilder builder)` and `void Add(PendingResolution pending)` — consumed by `LocalProjectSource` in Task 4.

- [ ] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.Tests/Sources/PendingLocalResolutionsTests.cs`:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class PendingLocalResolutionsTests
{
    private sealed class FakeGitClient : IGitClient
    {
        public TimeSpan CloneDelay { get; set; } = TimeSpan.Zero;

        public Exception? CloneException { get; set; }

        public void Clone(string repositoryUrl, string destinationPath)
        {
            if (CloneDelay > TimeSpan.Zero)
            {
                Thread.Sleep(CloneDelay);
            }

            if (CloneException is not null)
            {
                throw CloneException;
            }

            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "Service.csproj"), "<Project />");
        }

        public void Checkout(string repositoryPath, string reference)
        {
        }

        public string? GetOriginUrl(string repositoryPath) => null;
    }

    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [],
        });

    private static string CreateAppHostDirectory()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), "services: {}");
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), $$"""
            { "cacheDirectory": "{{cacheDirectory.Replace("\\", "\\\\")}}", "services": {} }
            """);
        return dir;
    }

    private static ServiceMetadata Metadata(string repository) =>
        new() { Repository = repository, Project = "Service.csproj" };

    private static ServiceDeveloperConfig DevConfig() => new() { Source = "local" };

    private static Task PublishBeforeStartEventAsync(IDistributedApplicationBuilder builder) =>
        builder.Eventing.PublishAsync(new BeforeStartEvent(
            builder.Services.BuildServiceProvider(), new DistributedApplicationModel(builder.Resources)));

    [Fact]
    public async Task Add_TwoCallsSameBuilder_ShareOneSubscription_BothResolveExactlyOnce()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        var ordersFacade = ServiceResource.CreateEmptyFacade(builder, "orders");
        var billingFacade = ServiceResource.CreateEmptyFacade(builder, "billing");
        // Two independent `For(builder)` calls, as LocalProjectSource.Resolve() will make one per
        // service — must resolve to the SAME instance so both Adds land in one pending queue with
        // exactly one BeforeStartEvent subscription.
        PendingLocalResolutions.For(builder).Add(new PendingResolution("orders", Metadata("https://fake/orders"), DevConfig(), ordersFacade, new FakeGitClient()));
        PendingLocalResolutions.For(builder).Add(new PendingResolution("billing", Metadata("https://fake/billing"), DevConfig(), billingFacade, new FakeGitClient()));

        await PublishBeforeStartEventAsync(builder);

        // If `For` subscribed twice instead of sharing one instance, both subscriptions would fire
        // on this single publish, each processing the full shared pending list — so each service
        // would be added twice (the second AddProject call for an already-added name is the
        // observable symptom of a broken share).
        Assert.Single(builder.Resources, r => r.Name == "orders");
        Assert.Single(builder.Resources, r => r.Name == "billing");
    }

    [Fact]
    public async Task For_TwoDifferentBuilders_GetIndependentQueues()
    {
        var builderA = CreateBuilder(CreateAppHostDirectory());
        var builderB = CreateBuilder(CreateAppHostDirectory());
        var facadeA = ServiceResource.CreateEmptyFacade(builderA, "orders");
        PendingLocalResolutions.For(builderA).Add(
            new PendingResolution("orders", Metadata("https://fake/orders"), DevConfig(), facadeA, new FakeGitClient()));

        await PublishBeforeStartEventAsync(builderB);

        Assert.DoesNotContain(builderB.Resources, r => r.Name == "orders");
    }

    [Fact]
    public async Task ResolveAllAsync_TwoBrokenPendingResolutions_ThrowsNamingBothServicesAndCauses()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        var ordersFacade = ServiceResource.CreateEmptyFacade(builder, "orders");
        var billingFacade = ServiceResource.CreateEmptyFacade(builder, "billing");
        var pending = PendingLocalResolutions.For(builder);
        pending.Add(new PendingResolution(
            "orders", Metadata("https://fake/orders"), DevConfig(), ordersFacade,
            new FakeGitClient { CloneException = new InvalidOperationException("orders network unreachable") }));
        pending.Add(new PendingResolution(
            "billing", Metadata("https://fake/billing"), DevConfig(), billingFacade,
            new FakeGitClient { CloneException = new InvalidOperationException("billing network unreachable") }));

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("orders network unreachable", ex.Message);
        Assert.Contains("billing", ex.Message);
        Assert.Contains("billing network unreachable", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "orders");
        Assert.DoesNotContain(builder.Resources, r => r.Name == "billing");
    }

    [Fact]
    public async Task ResolveAllAsync_TwoSlowPendingResolutions_RunsThemInParallel()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        var delay = TimeSpan.FromMilliseconds(300);
        var facadeA = ServiceResource.CreateEmptyFacade(builder, "orders");
        var facadeB = ServiceResource.CreateEmptyFacade(builder, "billing");
        var pending = PendingLocalResolutions.For(builder);
        pending.Add(new PendingResolution("orders", Metadata("https://fake/orders"), DevConfig(), facadeA, new FakeGitClient { CloneDelay = delay }));
        pending.Add(new PendingResolution("billing", Metadata("https://fake/billing"), DevConfig(), facadeB, new FakeGitClient { CloneDelay = delay }));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await PublishBeforeStartEventAsync(builder);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < delay * 2, $"Expected parallel resolution to take less than {delay * 2}, took {stopwatch.Elapsed}.");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~PendingLocalResolutionsTests"`
Expected: FAIL — `PendingResolution`/`PendingLocalResolutions` don't exist yet (compile error).

- [ ] **Step 3: Implement `PendingLocalResolutions`**

Create `src/Aspire.Hosting.ServiceSources/Sources/PendingLocalResolutions.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed record PendingResolution(
    string ServiceName,
    ServiceMetadata Metadata,
    ServiceDeveloperConfig Config,
    IResourceBuilder<IResourceWithServiceDiscovery> Facade,
    IGitClient GitClient);

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
        var cacheDirectory = ServiceSourcesConfigCache.GetCacheDirectory(builder);

        var results = await Task.WhenAll(_pending.Select(pending =>
            Task.Run(() => ResolveOne(pending, cacheDirectory, builder.AppHostDirectory), cancellationToken)));

        var failures = results.Where(r => r.Exception is not null).ToArray();
        if (failures.Length > 0)
        {
            throw AggregateFailures(failures);
        }

        foreach (var result in results)
        {
            var projectBuilder = builder.AddProject(result.Pending.ServiceName, result.ProjectPath!);
            ServiceResource.CopyEndpointAnnotations(result.Pending.Facade, projectBuilder);
        }
    }

    private static ResolutionResult ResolveOne(PendingResolution pending, string cacheDirectory, string appHostDirectory)
    {
        try
        {
            var projectPath = LocalProjectSource.ResolveProjectPath(
                pending.ServiceName, pending.Metadata, pending.Config, cacheDirectory, appHostDirectory, pending.GitClient);
            return new ResolutionResult(pending, projectPath, null);
        }
        catch (Exception ex)
        {
            return new ResolutionResult(pending, null, ex);
        }
    }

    private static ServiceSourcesConfigurationException AggregateFailures(IReadOnlyCollection<ResolutionResult> failures)
    {
        var lines = failures.Select(f => f.Exception!.InnerException is not null
            ? $"  - {f.Exception.Message} ({f.Exception.InnerException.Message})"
            : $"  - {f.Exception.Message}");
        var message = "Failed to resolve one or more 'local'-sourced services:" + Environment.NewLine +
            string.Join(Environment.NewLine, lines);
        return new ServiceSourcesConfigurationException(message, failures.First().Exception!);
    }

    private readonly record struct ResolutionResult(PendingResolution Pending, string? ProjectPath, Exception? Exception);
}
```

Note: `AggregateFailures` relies on each per-service failure message already naming its service (every message `ResolveProjectPath` throws is of the form `"Service '{serviceName}': ..."`), so it doesn't re-prefix the service name — it just joins the existing messages. `ResolveProjectPath`'s clone/checkout failures wrap an inner exception (e.g. the underlying `InvalidOperationException` from a broken `IGitClient`); its `.Message` is appended in parentheses so the aggregated exception's message contains both the per-service context and the underlying cause text (this is what `ResolveAllAsync_TwoBrokenPendingResolutions_ThrowsNamingBothServicesAndCauses` in Task 3 asserts on: both the wrapping message and `"orders network unreachable"`/`"billing network unreachable"` must appear).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~PendingLocalResolutionsTests"`
Expected: PASS — all four tests pass. If `ResolveAllAsync_TwoSlowPendingResolutions_RunsThemInParallel` is flaky under load, re-run once; if it fails consistently, the parallel dispatch (`Task.Run` inside `Task.WhenAll`) is not actually running concurrently — check that `_pending.Select(...)` isn't being materialized lazily in a way that serializes `Task.Run` calls (it shouldn't be, since `Task.WhenAll` eagerly enumerates its `IEnumerable<Task>` argument).

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/PendingLocalResolutions.cs test/Aspire.Hosting.ServiceSources.Tests/Sources/PendingLocalResolutionsTests.cs
git commit -m "Add PendingLocalResolutions: parallel BeforeStartEvent resolution for local sources"
```

---

## Task 4: `LocalProjectSource.Resolve()` — defer to `PendingLocalResolutions`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`

**Interfaces:**
- Consumes: `ServiceResource.CreateEmptyFacade` (Task 1), `PendingLocalResolutions.For(builder).Add(...)` (Task 3).
- `LocalProjectSource.Resolve(...)` keeps its exact `IServiceSource.Resolve` signature — only its body changes.
- `LocalProjectSource.ResolveProjectPath(...)` and its private helpers (`RepositoryUrlsMatch`, `NormalizeRepositoryUrl`, `GetRepositoryName`) are unchanged — still `internal static`, now called from `PendingLocalResolutions` instead of from `Resolve()`.

- [ ] **Step 1: Write the failing test**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`. First add these two usings at the top of the file (it currently has no `Aspire.Hosting` or `Aspire.Hosting.ApplicationModel` usings):

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
```

Then add this test (after the last existing test method, before the closing brace of the class):

```csharp
    [Fact]
    public void Resolve_DoesNotCloneOrRegisterSynchronously_DefersUntilBeforeStartEvent()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDir,
            Args = [],
        });
        var gitClient = new FakeGitClient();
        var source = new LocalProjectSource(gitClient);

        var facade = source.Resolve(builder, ServiceName, Metadata(), DevConfig());

        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
        Assert.Empty(facade.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.DoesNotContain(builder.Resources, r => r.Name == ServiceName);
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LocalProjectSourceTests.Resolve_DoesNotCloneOrRegisterSynchronously_DefersUntilBeforeStartEvent"`
Expected: FAIL — today's `Resolve()` clones synchronously and calls `builder.AddProject`, so `gitClient.ClonedRepos` is non-empty and `builder.Resources` contains `"orders"`.

- [ ] **Step 3: Rewire `Resolve()`**

In `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`, replace the `Resolve` method body:

```csharp
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var facade = ServiceResource.CreateEmptyFacade(builder, serviceName);

        PendingLocalResolutions.For(builder).Add(new PendingResolution(serviceName, metadata, config, facade, gitClient));

        return facade;
    }
```

(This removes the old body's `cacheDirectory`/`ResolveProjectPath`/`builder.AddProject`/`ServiceResource.CreateFacade` calls — those now happen inside `PendingLocalResolutions.ResolveAllAsync`. `ResolveProjectPath` and the private helpers below it in the file are untouched.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LocalProjectSourceTests"`
Expected: PASS — all `LocalProjectSourceTests` (the existing `ResolveProjectPath`-focused tests, unaffected since that method didn't change, plus the new `Resolve()` test).

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs
git commit -m "LocalProjectSource.Resolve() defers to PendingLocalResolutions instead of resolving synchronously"
```

---

## Task 5: Update existing tests broken by deferred timing

**Files:**
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/AddServiceIntegrationTests.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`

**Interfaces:**
- Consumes: `IDistributedApplicationBuilder.Eventing.PublishAsync<BeforeStartEvent>(...)`, `new BeforeStartEvent(IServiceProvider, DistributedApplicationModel)`, `new DistributedApplicationModel(IResourceCollection)` — all existing Aspire.Hosting 13.4.6 APIs (confirmed via decompilation of `Aspire.Hosting.dll`), used here to simulate the AppHost startup hook without needing a real DCP run.

This is the last task — after Task 4, `AddService("orders")` for a `"local"` source no longer clones or registers a resource synchronously, so two existing test files (which asserted those things immediately after `AddService`) are now failing:
- `AddServiceIntegrationTests.cs`'s real-repo end-to-end test — fixed up to match the new timing, per the spec's Testing section ("extend the existing real-`AddService()`-against-a-throwaway-git-repo integration test to cover the deferred path").
- `AddServiceTests.cs`'s two `"local"`-source unit tests (`AddService_LocalSourceWithPathOverride_ReturnsFacadeWrappingRealProject`, `AddService_RelativePathOverride_ResolvesRelativeToAppHostDirectoryNotProcessCwd`) — both assert `builder.Resources` contains `"orders"` right after `AddService()`. Confirmed by actually running the suite against Tasks 1-4's changes: both fail with `Assert.Contains() Failure: Filter not matched in collection` (`Collection: []`) since nothing is registered until `BeforeStartEvent` fires. Fixed the same way: publish `BeforeStartEvent` before the assertion. The file's other two tests (`AddService_UnknownSource_ThrowsNamingServiceAndSource`, the `"cluster"`-source tests) are unaffected — `ClusterSource` stays synchronous — and need no change.

- [ ] **Step 1: Update the test**

Replace the full contents of `test/Aspire.Hosting.ServiceSources.Tests/AddServiceIntegrationTests.cs`:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests;

public class AddServiceIntegrationTests
{
    private static string FixtureRepoPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-service.git");

    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [],
        });

    private static Task PublishBeforeStartEventAsync(IDistributedApplicationBuilder builder) =>
        builder.Eventing.PublishAsync(new BeforeStartEvent(
            builder.Services.BuildServiceProvider(), new DistributedApplicationModel(builder.Resources)));

    [Fact]
    public async Task AddService_ManagedClone_ClonesRealRepoAndChecksOutFeatureRef()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var appHostDir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), $$"""
            services:
              orders:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
                defaultRef: main
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), $$"""
            {
              "cacheDirectory": "{{cacheDirectory.Replace("\\", "\\\\")}}",
              "services": { "orders": { "source": "local", "ref": "feature/v2" } }
            }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        // Deferred resolution: nothing is cloned or registered until BeforeStartEvent fires.
        var clonedProjectPath = Path.Combine(cacheDirectory, "sample-service", "SampleProj", "SampleProj.csproj");
        Assert.False(File.Exists(clonedProjectPath));
        Assert.DoesNotContain(builder.Resources, r => r.Name == "orders");

        await PublishBeforeStartEventAsync(builder);

        Assert.True(File.Exists(clonedProjectPath));

        var endpoint = service.GetEndpoint("http");
        Assert.Equal("http", endpoint.EndpointName);

        var realResource = Assert.Single(builder.Resources, r => r.Name == "orders");
        var endpointAnnotation = Assert.Single(
            ((IResource)realResource).Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(5002, endpointAnnotation.Port);
    }
}
```

- [ ] **Step 2: Run the test**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~AddServiceIntegrationTests"`
Expected: PASS. (No separate red/green cycle: this is the fixup of a test that Task 4 broke, applied in one step.)

- [ ] **Step 3: Update `AddServiceTests.cs`**

In `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`, add these two usings at the top of the file (it currently has no `Aspire.Hosting.ApplicationModel` or `Microsoft.Extensions.DependencyInjection` usings):

```csharp
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
```

Add this private helper to the `AddServiceTests` class (same shape as the one in `AddServiceIntegrationTests.cs` and `PendingLocalResolutionsTests.cs`):

```csharp
    private static Task PublishBeforeStartEventAsync(IDistributedApplicationBuilder builder) =>
        builder.Eventing.PublishAsync(new BeforeStartEvent(
            builder.Services.BuildServiceProvider(), new DistributedApplicationModel(builder.Resources)));
```

Change `AddService_LocalSourceWithPathOverride_ReturnsFacadeWrappingRealProject` from:

```csharp
        var service = builder.AddService("orders");

        Assert.Contains(builder.Resources, r => r.Name == "orders");
        Assert.DoesNotContain(builder.Resources, r => ReferenceEquals(r, service.Resource));
    }
```

to:

```csharp
        var service = builder.AddService("orders");
        await PublishBeforeStartEventAsync(builder);

        Assert.Contains(builder.Resources, r => r.Name == "orders");
        Assert.DoesNotContain(builder.Resources, r => ReferenceEquals(r, service.Resource));
    }
```

(and change the method's signature from `public void AddService_LocalSourceWithPathOverride_ReturnsFacadeWrappingRealProject()` to `public async Task AddService_LocalSourceWithPathOverride_ReturnsFacadeWrappingRealProject()`, since it now awaits.)

Apply the same two changes — add `await PublishBeforeStartEventAsync(builder);` before the `Assert.Contains` and change `void` to `async Task` — to `AddService_RelativePathOverride_ResolvesRelativeToAppHostDirectoryNotProcessCwd`.

Leave `AddService_UnknownSource_ThrowsNamingServiceAndSource` and the two `"cluster"`-source tests untouched.

- [ ] **Step 4: Run `AddServiceTests.cs`**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~AddServiceTests"`
Expected: PASS — all tests in the file, including the two fixed-up ones.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS — every test project builds and all tests pass, confirming Tasks 1-5 compose correctly (in particular that `ClusterSource`'s synchronous path, which still calls `ServiceResource.CreateFacade`, is unaffected by Task 1's split).

- [ ] **Step 6: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/AddServiceIntegrationTests.cs test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs
git commit -m "Update tests for deferred local-source resolution timing"
```
