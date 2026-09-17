using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The post-hoc endpoint-mutation detector: what a guest-language AppHost changes on a <c>url</c> or
/// <c>kubernetes</c> service's endpoint after it resolved is restored at <c>BeforeStartEvent</c> and
/// reported, because no C# shadow can reach those capabilities.
/// </summary>
/// <remarks>
/// Assertions read the facade's own collection. For the <em>added</em> shape that is the only place
/// to look: Aspire's create branch adds straight to <c>builder.Resource.Annotations</c>, so a
/// smuggled endpoint never reaches the real resource and asserting its absence there would pass
/// whether or not the detector ran.
/// </remarks>
public class EndpointMutationDetectorTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);

    private static readonly ServiceDefinition UrlDefinition = new ServiceMetadata
    {
        Url = new UrlMetadata { Url = "https://orders.example.com" },
    }.ToDefinition("servicesources.yaml", "inventory", TestHelpers.EmptyRepositories);

    private static IResourceBuilder<ServiceResource> Url(IDistributedApplicationBuilder builder) =>
        new UrlSource().Resolve(
            builder, "inventory", UrlDefinition, new ServiceDeveloperConfig { Source = "url" });

    private sealed class FakePortAllocator(int port) : IPortAllocator
    {
        public bool IsAvailable(int candidate) => true;

        public int AllocatePort() => port;

        public IReadOnlyList<int> AllocatePorts(int count) => throw new NotSupportedException();
    }

    private static ServiceDefinition KubernetesDefinition() =>
        new ServiceMetadata
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Kubernetes = new KubernetesMetadata { Service = "orders-svc", Port = 8080, Scheme = "https" },
        }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static IResourceBuilder<ServiceResource> Kubernetes(IDistributedApplicationBuilder builder) =>
        new KubernetesSource(new FakePortAllocator(54321)).Resolve(
            builder, "orders", KubernetesDefinition(),
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev-west" } });

    private static IResourceBuilder<ServiceResource> Container(IDistributedApplicationBuilder builder) =>
        ResolvedService.Bridge(
            builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx"),
            "orders", "container");

    private static EndpointAnnotation[] Endpoints(IResourceBuilder<ServiceResource> service) =>
        [.. service.Resource.Annotations.OfType<EndpointAnnotation>()];

    // The harness's own coverage, on a reachable source so nothing is installed and nothing reverts:
    // it proves the reflective call really reaches Aspire's internal generic and mutates the shared
    // annotation. Every later test's premise rests on that.
    [Fact]
    public void GuestLanguageCallback_OnContainerSource_ReachesAspireAndMutatesTheEndpoint()
    {
        var builder = Builder();
        var service = Container(builder);
        service.WithHttpsEndpoint(port: 443, name: "https");

        GuestLanguageEndpointCallbacks.HttpsEndpointCallback(service, "https", ("Port", 9999));

        var endpoint = Assert.Single(Endpoints(service));
        Assert.Equal("https", endpoint.Name);
        Assert.Equal(9999, endpoint.Port);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }
}
