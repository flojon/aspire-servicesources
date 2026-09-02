using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Sources;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Covers issue #53: the AppHost applying its own configuration to a resolved service. Each test
/// drives a real source so the resource under test is the one an AppHost would actually get.
/// </summary>
public class ServiceConfigurationExtensionsTests
{
    private static readonly ServiceMetadata ContainerMetadata = new()
    {
        Container = new ContainerMetadata { Image = "nginxdemos/hello", Port = 8080 },
    };

    private static readonly ServiceMetadata KubernetesMetadata = new()
    {
        Kubernetes = new KubernetesMetadata { Service = "orders", Port = 8080 },
    };

    private static readonly ServiceMetadata UrlMetadata = new()
    {
        Url = new UrlMetadata { Url = "https://orders.example.com" },
    };

    private sealed class FixedPortAllocator : IPortAllocator
    {
        public int AllocatePort() => 51234;
    }

    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

    private static IResourceBuilder<IResourceWithServiceDiscovery> AddContainerService(
        IDistributedApplicationBuilder builder) =>
        new ContainerSource().Resolve(builder, "payments", ContainerMetadata, new ServiceDeveloperConfig { Source = "container" });

    private static IResourceBuilder<IResourceWithServiceDiscovery> AddUrlService(IDistributedApplicationBuilder builder) =>
        new UrlSource().Resolve(builder, "inventory", UrlMetadata, new ServiceDeveloperConfig { Source = "url" });

    [Fact]
    public void Configure_OnContainerSource_AppliesEnvironmentToTheRealResource()
    {
        var builder = Builder();

        var service = AddContainerService(builder)
            .Configure<IResourceWithEnvironment>(r => r.WithEnvironment("DBUSERNAME", "postgres"));

        Assert.NotEmpty(service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
    }

    [Fact]
    public void Configure_ReturnsTheSameBuilder_SoCapabilitiesCanBeChained()
    {
        var builder = Builder();
        var service = AddContainerService(builder);

        var returned = service
            .Configure<IResourceWithEnvironment>(r => r.WithEnvironment("A", "B"))
            .Configure<IResourceWithArgs>(r => r.WithArgs("--verbose"));

        Assert.Same(service.Resource, returned.Resource);
    }

    [Fact]
    public void Configure_WaitFor_AppliesToTheRealResource()
    {
        var builder = Builder();
        var dependency = builder.AddResource(new ServiceContainerResource("redis")).WithImage("redis");

        var service = AddContainerService(builder)
            .Configure<IResourceWithWaitSupport>(r => r.WaitFor(dependency));

        Assert.NotEmpty(service.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public void As_ReturnsATypedBuilderForTheUnderlyingResource()
    {
        var builder = Builder();

        var typed = AddContainerService(builder).As<ContainerResource>();

        Assert.Equal("payments", typed.Resource.Name);
    }

    [Fact]
    public void As_MismatchedType_ThrowsNamingTheService()
    {
        var builder = Builder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => AddContainerService(builder).As<ProjectResource>());

        Assert.Contains("payments", ex.Message);
        Assert.Contains("container", ex.Message);
    }

    [Fact]
    public void Configure_OnUrlSource_SkipsWithoutThrowing_SoSourceSwitchingKeepsWorking()
    {
        var builder = Builder();
        var callbackRan = false;

        // A developer switching this service to "url" in their own servicesources.local.json must
        // not break a Program.cs they don't own.
        var service = AddUrlService(builder)
            .Configure<IResourceWithEnvironment>(_ => callbackRan = true);

        Assert.False(callbackRan);
        Assert.NotNull(service);
    }

    [Fact]
    public void Configure_OnUrlSource_ReportsTheSkip()
    {
        var builder = Builder();

        AddUrlService(builder).Configure<IResourceWithEnvironment>(r => r.WithEnvironment("A", "B"));

        var message = Assert.Single(ServiceConfigurationWarnings.For(builder).Messages);
        Assert.Contains("inventory", message);
        Assert.Contains("'url'", message);
        Assert.Contains("servicesources.local.json", message);
    }

    [Fact]
    public void Configure_ManyCallsOnOneService_ReportOneAggregatedSkip()
    {
        var builder = Builder();
        var service = AddUrlService(builder);

        // The shape this package is built for: one service carrying a lot of AppHost configuration.
        // Before these were grouped, switching it to an out-of-band source emitted one near-identical
        // warning per call.
        for (var i = 0; i < 25; i++)
        {
            service.Configure<IResourceWithEnvironment>(r => r.WithEnvironment("A", "B"));
        }

        service.Configure<IResourceWithWaitSupport>(_ => { });

        var message = Assert.Single(ServiceConfigurationWarnings.For(builder).Messages);
        // "calls" rather than "Configure calls": the same message also stands for a consumer's
        // dropped WaitFor, so the tally names each call and the summary only counts them.
        Assert.Contains("26 calls", message);
        Assert.Contains("Configure<IResourceWithEnvironment> ×25", message);
        Assert.Contains("Configure<IResourceWithWaitSupport>", message);
    }

    [Fact]
    public void Configure_OnTwoDifferentServices_ReportsThemSeparately()
    {
        var builder = Builder();

        AddUrlService(builder).Configure<IResourceWithEnvironment>(r => r.WithEnvironment("A", "B"));
        new UrlSource()
            .Resolve(builder, "billing", UrlMetadata, new ServiceDeveloperConfig { Source = "url" })
            .Configure<IResourceWithEnvironment>(r => r.WithEnvironment("A", "B"));

        // Grouping is per service, not global — each service names itself and its own remedy.
        Assert.Equal(2, ServiceConfigurationWarnings.For(builder).Messages.Count);
    }

    [Fact]
    public void Configure_OnKubernetesSource_SkipsRatherThanConfiguringThePortForward()
    {
        var builder = Builder();
        var service = new KubernetesSource(new FixedPortAllocator()).Resolve(
            builder, "orders", KubernetesMetadata,
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev" } });

        // The port-forward executable would accept environment variables happily, so skipping has to
        // be driven by the source rather than by a capability check.
        service.Configure<IResourceWithEnvironment>(r => r.WithEnvironment("A", "B"));

        Assert.Empty(service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
        Assert.Contains("port-forward", Assert.Single(ServiceConfigurationWarnings.For(builder).Messages));
    }

    [Fact]
    public void Configure_WaitOnKubernetesSource_StillApplies_BecauseOrderingThePortForwardIsCorrect()
    {
        var builder = Builder();
        var migrations = builder.AddResource(new ServiceContainerResource("migrations")).WithImage("migrate");
        var service = new KubernetesSource(new FixedPortAllocator()).Resolve(
            builder, "orders", KubernetesMetadata,
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev" } });

        // Unlike environment variables, start ordering is not "configuring the wrong process": the
        // port-forward is a real registered executable, and holding it back until migrations finish is
        // exactly what the AppHost asked for. Skipping it lost the ordering silently the moment
        // someone switched a service to "kubernetes".
        service.Configure<IResourceWithWaitSupport>(r => r.WaitForCompletion(migrations));

        Assert.NotEmpty(service.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Empty(ServiceConfigurationWarnings.For(builder).Messages);
    }

    [Fact]
    public void Configure_WaitOnUrlSource_StillSkips_BecauseNothingIsRegisteredToOrder()
    {
        var builder = Builder();
        var migrations = builder.AddResource(new ServiceContainerResource("migrations")).WithImage("migrate");

        var service = AddUrlService(builder)
            .Configure<IResourceWithWaitSupport>(r => r.WaitForCompletion(migrations));

        // A "url" service's resource is never registered, so there is no process to hold back.
        Assert.Empty(service.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Single(ServiceConfigurationWarnings.For(builder).Messages);
    }

    [Fact]
    public async Task SkippedConfiguration_IsLoggedAtStartup()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              inventory:
                url:
                  url: https://orders.example.com
            """);
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" } } }""");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);

        builder.AddService("inventory").Configure<IResourceWithEnvironment>(r => r.WithEnvironment("A", "B"));

        // Buffered during composition — there is no logger yet — and flushed here.
        var ex = await Record.ExceptionAsync(() => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Null(ex);
        Assert.Single(ServiceConfigurationWarnings.For(builder).Messages);
    }

    [Fact]
    public void As_OnUrlSource_StillThrows_BecauseItMustReturnABuilder()
    {
        var builder = Builder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => AddUrlService(builder).As<IResourceWithEnvironment>());

        Assert.Contains("inventory", ex.Message);
        Assert.Contains("'url'", ex.Message);
    }
}
