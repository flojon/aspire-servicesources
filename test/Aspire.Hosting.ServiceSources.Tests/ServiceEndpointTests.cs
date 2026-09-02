using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// <c>GetServiceEndpoint()</c>, the portable spelling of "the endpoint this service exposes".
/// Issue #160: the endpoint <i>name</i> a resolved service exposes is decided by whichever source
/// resolved it, so a consumer that hardcodes <c>GetEndpoint("https")</c> breaks the moment a
/// developer switches that service to a source producing an <c>http</c> endpoint.
/// </summary>
public class ServiceEndpointTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

    private sealed class FakePortAllocator : IPortAllocator
    {
        public int AllocatePort() => 54321;
    }

    private static IResourceBuilder<IResourceWithServiceDiscovery> ContainerService(
        IDistributedApplicationBuilder builder, string? scheme = null) =>
        new ContainerSource().Resolve(
            builder,
            "orders",
            new ServiceMetadata { Container = new ContainerMetadata { Image = "ghcr.io/company/orders", Port = 8080, Scheme = scheme } },
            new ServiceDeveloperConfig { Source = "container" });

    private static IResourceBuilder<IResourceWithServiceDiscovery> KubernetesService(
        IDistributedApplicationBuilder builder, string? scheme = null) =>
        new KubernetesSource(new FakePortAllocator()).Resolve(
            builder,
            "orders",
            new ServiceMetadata { Kubernetes = new KubernetesMetadata { Service = "orders-svc", Port = 8080, Scheme = scheme } },
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev-west" } });

    /// <summary>
    /// Drops the endpoint a source gave the resource, so a test can put an oddly-named one in its
    /// place. <c>Annotations</c> is a <c>Collection&lt;T&gt;</c>, so there is no RemoveAll to use.
    /// </summary>
    private static void ClearEndpoints(IResource resource)
    {
        foreach (var endpoint in resource.Annotations.OfType<EndpointAnnotation>().ToList())
        {
            resource.Annotations.Remove(endpoint);
        }
    }

    [Fact]
    public void GetServiceEndpoint_IsExportedToAts()
    {
        // A guest-language AppHost has the same problem #160 describes and no other portable
        // spelling of it, so the export is part of the fix rather than a bonus. Codegen emits
        // `getServiceEndpoint(): EndpointReferencePromise`; the typecheck-typescript CI job
        // compiles the sample that calls it.
        var method = typeof(ServiceEndpointExtensions).GetMethods()
            .Single(m => m.Name == nameof(ServiceEndpointExtensions.GetServiceEndpoint));

        Assert.Single(method.GetCustomAttributes(typeof(AspireExportAttribute), inherit: false));
        Assert.False(method.IsGenericMethodDefinition);
    }

    [Fact]
    public void GetServiceEndpoint_HttpOnlyService_ReturnsTheHttpEndpoint()
    {
        var service = KubernetesService(Builder());

        Assert.Equal("http", service.GetServiceEndpoint().EndpointName);
    }

    [Fact]
    public void GetServiceEndpoint_HttpsSchemedService_ReturnsTheHttpsEndpoint()
    {
        var service = KubernetesService(Builder(), scheme: "https");

        Assert.Equal("https", service.GetServiceEndpoint().EndpointName);
    }

    [Fact]
    public void GetServiceEndpoint_ServiceExposingBothSchemes_PrefersHttps()
    {
        // What a "local" dotnet service looks like when its launch profile declares both. Aspire's
        // own service discovery resolves "https+http://" in the same order, so preferring https
        // here hands a consumer the same endpoint it would have picked itself.
        var service = ContainerService(Builder());
        service.Configure<IResourceWithEndpoints>(r => r.WithHttpsEndpoint(targetPort: 8443));

        Assert.Equal("https", service.GetServiceEndpoint().EndpointName);
    }

    [Fact]
    public void GetServiceEndpoint_UrlSource_ReturnsTheEndpointNamedForTheUrlScheme()
    {
        var service = new UrlSource().Resolve(
            Builder(),
            "orders",
            new ServiceMetadata { Url = new UrlMetadata { Url = "https://orders.example.com" } },
            new ServiceDeveloperConfig { Source = "url" });

        var endpoint = service.GetServiceEndpoint();

        Assert.Equal("https", endpoint.EndpointName);
        Assert.Equal("https://orders.example.com:443", endpoint.Url);
    }

    [Fact]
    public void GetServiceEndpoint_SingleEndpointNamedNeitherHttpNorHttps_ReturnsIt()
    {
        var service = ContainerService(Builder());
        ClearEndpoints(service.Resource);
        service.Configure<IResourceWithEndpoints>(r => r.WithEndpoint(targetPort: 9000, scheme: "http", name: "grpc"));

        Assert.Equal("grpc", service.GetServiceEndpoint().EndpointName);
    }

    [Fact]
    public void GetServiceEndpoint_NoEndpoints_ThrowsNamingTheServiceAndItsSource()
    {
        var service = ContainerService(Builder());
        ClearEndpoints(service.Resource);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => service.GetServiceEndpoint());

        Assert.Contains("orders", ex.Message);
        Assert.Contains("container", ex.Message);
    }

    [Fact]
    public void GetServiceEndpoint_SeveralEndpointsAndNoneHttpOrHttps_ThrowsListingThemAndNamingGetEndpoint()
    {
        var service = ContainerService(Builder());
        ClearEndpoints(service.Resource);
        service.Configure<IResourceWithEndpoints>(r => r
            .WithEndpoint(targetPort: 9000, scheme: "http", name: "grpc")
            .WithEndpoint(targetPort: 9001, scheme: "http", name: "metrics"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => service.GetServiceEndpoint());

        Assert.Contains("orders", ex.Message);
        Assert.Contains("grpc", ex.Message);
        Assert.Contains("metrics", ex.Message);
        Assert.Contains("GetEndpoint", ex.Message);
    }
}
