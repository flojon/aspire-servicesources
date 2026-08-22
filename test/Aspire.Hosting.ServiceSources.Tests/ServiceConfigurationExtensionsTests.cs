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
    public void Configure_OnUrlSource_ThrowsExplainingThereIsNothingLocalToConfigure()
    {
        var builder = Builder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => AddUrlService(builder).Configure<IResourceWithEnvironment>(r => r.WithEnvironment("A", "B")));

        Assert.Contains("inventory", ex.Message);
        Assert.Contains("'url'", ex.Message);
        Assert.Contains("servicesources.local.json", ex.Message);
    }

    [Fact]
    public void Configure_OnKubernetesSource_ThrowsExplainingItWouldReachThePortForward()
    {
        var builder = Builder();
        var service = new KubernetesSource(new FixedPortAllocator()).Resolve(
            builder, "orders", KubernetesMetadata,
            new ServiceDeveloperConfig { Source = "kubernetes", Context = "dev" });

        // The port-forward executable does accept environment variables, so this has to be a
        // deliberate refusal rather than a capability check — configuring it would silently
        // configure kubectl rather than the service behind it.
        var ex = Record.Exception(() => service.Configure<IResourceWithEnvironment>(r => r.WithEnvironment("A", "B")));

        Assert.NotNull(ex);
        Assert.Contains("port-forward", ex.Message);
    }
}
