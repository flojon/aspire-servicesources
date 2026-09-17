using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;
using ServiceSourcesOverloadProbe;

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

    [Fact]
    public void WithEndpoint_DefaultNameOnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

        // A partial call omitting every optional argument but port/scheme — the shape that reaches
        // the primary WithEndpoint<T> overload rather than any of its binary-compat shims, all of
        // which require every parameter and so are inapplicable to a call this short.
        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        var result = OverloadProbe.CallWithEndpointPrimary(service, port: 9999, scheme: "https");

        Assert.Same(service, result);
        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
    }

    [Fact]
    public void WithEndpoint_OnReachableSource_AppliesThroughToTheRealEndpoint()
    {
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");
        var service = ResolvedService.Bridge(real, "orders", "container");

        var result = service.WithEndpoint(port: 9999, scheme: "https", name: "probe");

        var endpoint = Assert.Single(
            result.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "probe");
        Assert.Equal(9999, endpoint.Port);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void WithEndpoint_CompatShimWithProtocol_DefaultNameOnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

        // `protocol:` excludes the two no-protocol shims outright (they have no such parameter); of
        // the two candidates left, a plain bool literal is an exact match for this shim's non-nullable
        // isProxied and only an implicit conversion for the nullable primary's -- exact match wins.
        // Routed through OverloadProbe, whose namespace has no ancestor relationship to
        // Aspire.Hosting.ServiceSources or Aspire.Hosting, so this reasoning is checked against the
        // real full candidate set rather than stopping at the first namespace level with any match.
        OverloadProbe.CallWithEndpointCompatShimWithProtocol(
            service, port: 9999, targetPort: 9999, scheme: "https", name: "https", env: null,
            isProxied: true, isExternal: null, protocol: ProtocolType.Tcp);

        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
    }

    [Fact]
    public void WithEndpoint_CompatShimNullableIsProxiedNoProtocol_DefaultNameOnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

        // Every argument but `protocol` supplied, with isProxied typed bool? explicitly -- the shape
        // that reaches this shim rather than its bool-isProxied sibling (not applicable to a bool?
        // argument without an explicit, non-implicit conversion) or the primary overload (which needs
        // its `protocol` default, so loses to any shim applicable without one). Routed through
        // OverloadProbe for the same out-of-namespace reason as the test above.
        bool? isProxied = null;
        OverloadProbe.CallWithEndpointCompatShimNullableIsProxiedNoProtocol(
            service, port: 9999, targetPort: 9999, scheme: "https", name: "https", env: null,
            isProxied: isProxied, isExternal: null);

        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
    }

    [Fact]
    public void WithEndpoint_CompatShimBoolIsProxiedNoProtocol_DefaultNameOnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

        // Every argument but `protocol` supplied, with a plain bool literal for isProxied -- an exact
        // type match beats the bool?-typed sibling shim's implicit-conversion match, so this is the
        // shape that reaches this specific overload. Routed through OverloadProbe for the same
        // out-of-namespace reason as the two tests above.
        OverloadProbe.CallWithEndpointCompatShimBoolIsProxiedNoProtocol(
            service, port: 9999, targetPort: 9999, scheme: "https", name: "https", env: null,
            isProxied: true, isExternal: null);

        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
    }

    [Fact]
    public void WithEndpoint_Callback_DefaultNameOnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = UrlFacadeWithEndpoint(builder, "inventory", "https");
        var callbackInvoked = false;

        // Routed through OverloadProbe — see OverloadResolutionProbe.cs for why.
        var result = OverloadProbe.CallWithEndpointCallback(service, "https", endpoint =>
        {
            callbackInvoked = true;
            endpoint.Port = 9999;
        });

        Assert.Same(service, result);
        Assert.False(callbackInvoked, "the callback must never run when the source is unreachable");
        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
    }

    [Fact]
    public void WithEndpoint_Callback_OnReachableSource_InvokesCallbackAndAppliesThroughToTheRealEndpoint()
    {
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");
        var service = ResolvedService.Bridge(real, "orders", "container");
        // Pre-registered through the already-correct WithHttpsEndpoint add branch, so this test
        // exercises the callback overload's update branch against an instance both collections
        // already share, rather than its add branch.
        service.WithHttpsEndpoint(port: 443, name: "probe");
        var callbackInvoked = false;

        service.WithEndpoint("probe", endpoint =>
        {
            callbackInvoked = true;
            endpoint.Port = 9999;
        });

        Assert.True(callbackInvoked);
        var endpoint = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "probe");
        Assert.Equal(9999, endpoint.Port);
        // The update branch mutates the shared instance in place, so `real` sees the new port with
        // no forwarding at all -- the through-to-the-real-endpoint half this test's name has always
        // claimed and never actually checked.
        Assert.Same(
            endpoint,
            Assert.Single(real.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "probe"));
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void WithHttpEndpoint_CompatShim_DefaultNameOnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = UrlFacadeWithEndpoint(builder, "inventory", "http");

        // Every argument supplied, with a plain bool isProxied literal -- an exact match for this
        // shim's non-nullable isProxied, and only an implicit conversion for the nullable primary's,
        // so this is the shape that binds here rather than to the overload already shadowed above.
        // Routed through OverloadProbe, whose namespace has no ancestor relationship to
        // Aspire.Hosting.ServiceSources or Aspire.Hosting, so this reasoning is checked against the
        // real full candidate set.
        OverloadProbe.CallWithHttpEndpointCompatShim(
            service, port: 9999, targetPort: 9999, name: "http", env: null, isProxied: true);

        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
    }

    [Fact]
    public void WithHttpsEndpoint_CompatShim_DefaultNameOnUrlSource_IsSkippedAndReported()
    {
        var builder = Builder();
        var service = UrlFacadeWithEndpoint(builder, "inventory", "https");

        // The https counterpart of the WithHttpEndpoint compat-shim test above -- same exact-match
        // reasoning, same OverloadProbe routing for the same out-of-namespace reason.
        OverloadProbe.CallWithHttpsEndpointCompatShim(
            service, port: 9999, targetPort: 9999, name: "https", env: null, isProxied: true);

        Assert.Equal(443, service.Resource.Annotations.OfType<EndpointAnnotation>().Single().Port);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint", message);
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
