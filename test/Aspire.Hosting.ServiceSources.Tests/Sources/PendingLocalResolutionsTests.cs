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
