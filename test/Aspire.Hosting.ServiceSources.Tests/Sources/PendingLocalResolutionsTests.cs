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

        public void Clone(string repositoryUrl, string destinationPath)
        {
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
}
