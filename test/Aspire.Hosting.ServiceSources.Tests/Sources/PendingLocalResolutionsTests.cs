using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
using Aspire.Hosting.ServiceSources.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class PendingLocalResolutionsTests
{
    private sealed class FakeGitClient : IGitClient
    {
        public TimeSpan CloneDelay { get; set; } = TimeSpan.Zero;

        public Exception? CloneException { get; set; }

        public Barrier? StartBarrier { get; set; }

        public bool CloneCalled { get; private set; }

        public void Clone(string repositoryUrl, string destinationPath)
        {
            CloneCalled = true;

            // Rendezvous with the other clone(s) before proceeding: if resolution were sequential
            // rather than parallel, only one participant would ever reach this point at a time and
            // the wait below would time out, deterministically failing the test regardless of
            // machine speed or thread-pool warm-up latency.
            if (StartBarrier is not null && !StartBarrier.SignalAndWait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Timed out waiting for the other clone to start concurrently.");
            }

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

        public void Fetch(string repositoryPath)
        {
        }

        public bool HasUncommittedChanges(string repositoryPath) => false;

        public bool IsRefCheckedOut(string repositoryPath, string reference) => false;

        public string? GetOriginUrl(string repositoryPath) => null;
    }

    private sealed class FakeKindResource(string name) : Resource(name), IResourceWithServiceDiscovery;

    private sealed class FakeLocalResourceKind : ILocalResourceKind
    {
        public List<(string ServiceName, string RepoRoot, object? RawConfig)> Calls { get; } = [];

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
        {
            Calls.Add((serviceName, repoRoot, rawConfig));

            // A real, registered resource with an endpoint — deliberately NOT
            // ServiceResource.CreateEmptyFacade, which is documented as never entering the app
            // model and so can't show that a handler's resource actually reaches it.
            return builder.AddResource(new FakeKindResource(serviceName))
                .WithHttpEndpoint(port: 5555, name: "http");
        }
    }

    /// <summary>
    /// Stands in for a real handler that parses its options block: rejects a bad block from
    /// <see cref="Validate"/> (the supported way) or, when <paramref name="fromResolve"/> is set,
    /// from <see cref="Resolve"/> — the unsupported way that reaches the app model mid-creation.
    /// </summary>
    private sealed class RejectingLocalResourceKind(bool fromResolve = false) : ILocalResourceKind
    {
        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
        {
            if (fromResolve)
            {
                throw new ServiceSourcesConfigurationException($"Service '{serviceName}': unknown property 'runScrip'.");
            }

            return builder.AddResource(new FakeKindResource(serviceName));
        }

        public void Validate(string serviceName, object? rawConfig)
        {
            if (!fromResolve)
            {
                throw new ServiceSourcesConfigurationException($"Service '{serviceName}': unknown property 'runScrip'.");
            }
        }
    }

    private sealed class NullReturningLocalResourceKind : ILocalResourceKind
    {
        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) => null!;
    }

    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        TestHelpers.CreateBuilder(appHostDirectory);

    private static string CreateAppHostDirectory()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), "services: {}");
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), """{ "services": {} }""");
        return dir;
    }

    private static ServiceMetadata Metadata(string repository) =>
        new() { Repository = repository, Project = "Service.csproj" };

    private static ServiceMetadata MetadataWithKind(string repository, string kind, object? kindConfig = null) =>
        new() { Repository = repository, Kind = kind, KindConfig = kindConfig };

    private static ServiceDeveloperConfig DevConfig() => new() { Source = "local" };

    private static Task PublishBeforeStartEventAsync(IDistributedApplicationBuilder builder) =>
        TestHelpers.PublishBeforeStartEventAsync(builder);

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
        var facadeA = ServiceResource.CreateEmptyFacade(builder, "orders");
        var facadeB = ServiceResource.CreateEmptyFacade(builder, "billing");
        var pending = PendingLocalResolutions.For(builder);
        // Both clones rendezvous on this barrier before either is allowed to proceed, so
        // completion below is only possible if the two resolutions actually ran concurrently.
        var startBarrier = new Barrier(2);
        pending.Add(new PendingResolution("orders", Metadata("https://fake/orders"), DevConfig(), facadeA, new FakeGitClient { StartBarrier = startBarrier }));
        pending.Add(new PendingResolution("billing", Metadata("https://fake/billing"), DevConfig(), facadeB, new FakeGitClient { StartBarrier = startBarrier }));

        await PublishBeforeStartEventAsync(builder);
    }

    [Fact]
    public async Task ResolveAllAsync_RegisteredNonDotnetKind_DispatchesToHandlerWithResolvedRepoRoot()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        var handler = new FakeLocalResourceKind();
        builder.AddLocalKind("javascript", handler);
        var facade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        var kindConfig = new Dictionary<object, object> { ["appDirectory"] = "." };
        PendingLocalResolutions.For(builder).Add(new PendingResolution(
            "frontend", MetadataWithKind("https://fake/frontend", "javascript", kindConfig), DevConfig(), facade, new FakeGitClient()));

        await PublishBeforeStartEventAsync(builder);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("frontend", call.ServiceName);
        Assert.EndsWith(Path.Combine(".servicesources", "checkouts", "frontend"), call.RepoRoot);
        Assert.Same(kindConfig, call.RawConfig);
    }

    [Fact]
    public async Task ResolveAllAsync_UnregisteredNonDotnetKind_ThrowsNamingServiceAndKind()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        var facade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        PendingLocalResolutions.For(builder).Add(new PendingResolution(
            "frontend", MetadataWithKind("https://fake/frontend", "javascript"), DevConfig(), facade, new FakeGitClient()));

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("javascript", ex.Message);
    }

    [Fact]
    public async Task ResolveAllAsync_TwoServicesMissingProjectFile_AggregatesBothAndAddsNeither()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        var ordersFacade = ServiceResource.CreateEmptyFacade(builder, "orders");
        var billingFacade = ServiceResource.CreateEmptyFacade(builder, "billing");
        var pending = PendingLocalResolutions.For(builder);
        pending.Add(new PendingResolution(
            "orders", new ServiceMetadata { Repository = "https://fake/orders", Project = "Missing.csproj" }, DevConfig(), ordersFacade, new FakeGitClient()));
        pending.Add(new PendingResolution(
            "billing", new ServiceMetadata { Repository = "https://fake/billing", Project = "Missing.csproj" }, DevConfig(), billingFacade, new FakeGitClient()));

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("billing", ex.Message);
        Assert.Contains("Missing.csproj", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "orders");
        Assert.DoesNotContain(builder.Resources, r => r.Name == "billing");
    }

    [Fact]
    public async Task ResolveAllAsync_OneUnregisteredKindAndOneValidDotnetService_ThrowsWithoutAddingValidService()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        var ordersFacade = ServiceResource.CreateEmptyFacade(builder, "orders");
        var frontendFacade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        var pending = PendingLocalResolutions.For(builder);
        pending.Add(new PendingResolution("orders", Metadata("https://fake/orders"), DevConfig(), ordersFacade, new FakeGitClient()));
        pending.Add(new PendingResolution(
            "frontend", MetadataWithKind("https://fake/frontend", "javascript"), DevConfig(), frontendFacade, new FakeGitClient()));

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("javascript", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "orders");
    }

    [Fact]
    public async Task ResolveAllAsync_UnregisteredKind_ThrowsWithoutCloningAnyService()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        var frontendFacade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        var gitClient = new FakeGitClient();
        PendingLocalResolutions.For(builder).Add(new PendingResolution(
            "frontend", MetadataWithKind("https://fake/frontend", "javascript"), DevConfig(), frontendFacade, gitClient));

        await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        Assert.False(gitClient.CloneCalled);
    }

    [Fact]
    public async Task ResolveAllAsync_RegisteredKindStillResolvesAfterPreflightPasses()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        var handler = new FakeLocalResourceKind();
        builder.AddLocalKind("javascript", handler);
        var facade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        PendingLocalResolutions.For(builder).Add(new PendingResolution(
            "frontend", MetadataWithKind("https://fake/frontend", "javascript"), DevConfig(), facade, new FakeGitClient()));

        await PublishBeforeStartEventAsync(builder);

        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ResolveAllAsync_RegisteredNonDotnetKind_AddsHandlerResourceToAppModelAndCopiesEndpoints()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        builder.AddLocalKind("javascript", new FakeLocalResourceKind());
        var facade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        PendingLocalResolutions.For(builder).Add(new PendingResolution(
            "frontend", MetadataWithKind("https://fake/frontend", "javascript"), DevConfig(), facade, new FakeGitClient()));

        await PublishBeforeStartEventAsync(builder);

        // Dispatching to the handler isn't enough: the resource it returns has to land in the app
        // model, and its endpoints have to reach the facade consumers hold.
        Assert.Single(builder.Resources, r => r.Name == "frontend");
        var endpoint = Assert.Single(facade.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(5555, endpoint.Port);
    }

    [Fact]
    public async Task ResolveAllAsync_KindDifferingOnlyByCase_ErrorPointsAtTheCasing()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        builder.AddLocalKind("javascript", new FakeLocalResourceKind());
        var facade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        PendingLocalResolutions.For(builder).Add(new PendingResolution(
            "frontend", MetadataWithKind("https://fake/frontend", "JavaScript"), DevConfig(), facade, new FakeGitClient()));

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        // Without the hint this reads as a missing satellite package rather than a casing slip.
        Assert.Contains("case-sensitive", ex.Message);
        Assert.Contains("'javascript'", ex.Message);
    }

    [Fact]
    public async Task ResolveAllAsync_HandlerRejectsConfigFromValidate_AggregatesAndAddsNoResource()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        builder.AddLocalKind("javascript", new RejectingLocalResourceKind());
        builder.AddLocalKind("java", new RejectingLocalResourceKind());
        var ordersFacade = ServiceResource.CreateEmptyFacade(builder, "orders");
        var frontendFacade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        var apiFacade = ServiceResource.CreateEmptyFacade(builder, "api");
        var pending = PendingLocalResolutions.For(builder);
        pending.Add(new PendingResolution("orders", Metadata("https://fake/orders"), DevConfig(), ordersFacade, new FakeGitClient()));
        pending.Add(new PendingResolution("frontend", MetadataWithKind("https://fake/frontend", "javascript"), DevConfig(), frontendFacade, new FakeGitClient()));
        pending.Add(new PendingResolution("api", MetadataWithKind("https://fake/api", "java"), DevConfig(), apiFacade, new FakeGitClient()));

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        // Both bad services are reported together, and the good one that sorts before them never
        // reached the app model — the half-populated state a Resolve-time throw would leave behind.
        Assert.Contains("frontend", ex.Message);
        Assert.Contains("api", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "orders");
        Assert.DoesNotContain(builder.Resources, r => r.Name == "frontend");
    }

    [Fact]
    public async Task ResolveAllAsync_HandlerThrowsFromResolve_ErrorNamesServiceAndPointsAtValidate()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        builder.AddLocalKind("javascript", new RejectingLocalResourceKind(fromResolve: true));
        var facade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        PendingLocalResolutions.For(builder).Add(new PendingResolution(
            "frontend", MetadataWithKind("https://fake/frontend", "javascript"), DevConfig(), facade, new FakeGitClient()));

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains(nameof(ILocalResourceKind.Validate), ex.Message);
        Assert.Contains("runScrip", ex.InnerException!.Message);
    }

    [Fact]
    public async Task ResolveAllAsync_HandlerReturnsNull_ThrowsNamingServiceInsteadOfNullReference()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        builder.AddLocalKind("javascript", new NullReturningLocalResourceKind());
        var facade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        PendingLocalResolutions.For(builder).Add(new PendingResolution(
            "frontend", MetadataWithKind("https://fake/frontend", "javascript"), DevConfig(), facade, new FakeGitClient()));

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("javascript", ex.Message);
    }
}
