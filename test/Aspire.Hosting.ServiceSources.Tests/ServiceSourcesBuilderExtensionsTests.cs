using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests;

public class ServiceSourcesBuilderExtensionsTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    // Mirrors UrlSource.Resolve's own EndpointAnnotation construction exactly, then bridges it
    // unregistered — the same shape UrlSource itself produces for a "url"-sourced service, so the
    // shadow's gate sees precisely what it sees in production.
    private static IResourceBuilder<ServiceResource> UrlFacadeWithEndpoint(
        IDistributedApplicationBuilder builder, string serviceName, string scheme)
    {
        var facade = new ServiceResource(serviceName);
        facade.Annotations.Add(new EndpointAnnotation(
            ProtocolType.Tcp, uriScheme: scheme, name: scheme, transport: "http", port: 443, targetPort: 443));
        return ResolvedService.BridgeUnregistered(builder, facade, serviceName, "url");
    }

    [Fact]
    public void WithHttpsEndpoint_DefaultNameOnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

        var result = service.WithHttpsEndpoint(port: 9999);

        Assert.Same(service, result);
        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithHttpEndpoint/WithHttpsEndpoint", message);
    }

    [Fact]
    public void WithHttpEndpoint_DefaultNameOnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = UrlFacadeWithEndpoint(builder, "inventory", "http");

        service.WithHttpEndpoint(port: 8888);

        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithHttpEndpoint/WithHttpsEndpoint", message);
    }

    [Fact]
    public void WithHttpsEndpoint_OnReachableSource_AppliesThroughToTheRealEndpoint()
    {
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");
        var service = ResolvedService.Bridge(real, "orders", "container");

        var result = service.WithHttpsEndpoint(port: 9999, name: "probe");

        var endpoint = Assert.Single(
            result.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "probe");
        Assert.Equal(9999, endpoint.Port);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    // GateEndpointCall's fallback: a hypothetical IResourceBuilder<ServiceResource> that is not a
    // ServiceResourceBuilder has no closure-captured Source, so the gate re-derives it from the
    // facade's own ServiceSourceAnnotation instead. No such builder reaches AddService today (see
    // the method's own doc comment), but the branch exists and should behave identically to the
    // primary one — skip and warn, mutate nothing — rather than silently falling through un-gated.
    private sealed class FakeNonServiceResourceBuilder(
        IDistributedApplicationBuilder applicationBuilder, ServiceResource resource)
        : IResourceBuilder<ServiceResource>
    {
        public IDistributedApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;

        public ServiceResource Resource { get; } = resource;

        public IResourceBuilder<ServiceResource> WithAnnotation<TAnnotation>(
            TAnnotation annotation, ResourceAnnotationMutationBehavior behavior = ResourceAnnotationMutationBehavior.Append)
            where TAnnotation : IResourceAnnotation
        {
            Resource.Annotations.Add(annotation);
            return this;
        }
    }

    [Fact]
    public void WithHttpsEndpoint_OnNonServiceResourceBuilder_FallsBackToAnnotationScanAndStillGates()
    {
        var builder = Builder();
        var facade = new ServiceResource("inventory");
        facade.Annotations.Add(new EndpointAnnotation(
            ProtocolType.Tcp, uriScheme: "https", name: "https", transport: "http", port: 443, targetPort: 443));
        facade.Annotations.Add(new ServiceSourceAnnotation("inventory", "url"));
        var service = new FakeNonServiceResourceBuilder(builder, facade);

        service.WithHttpsEndpoint(port: 9999);

        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithHttpEndpoint/WithHttpsEndpoint", message);
    }
}
