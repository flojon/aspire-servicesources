using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Covers the callback <c>WithEndpoint</c> overload's <em>add</em> branch on reachable sources:
/// Aspire adds the brand-new <see cref="EndpointAnnotation"/> straight to the facade's collection
/// rather than through <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/>, so without
/// the identity-diff forward it never reaches the resource DCP actually runs. Every assertion here
/// reads the real resource's own collection, not the facade's — the failure mode is silent, and a
/// facade-only assertion passes against the bug.
/// </summary>
public class EndpointCallbackDualWriteTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    private static IResourceBuilder<ServiceResource> ContainerService(
        IDistributedApplicationBuilder builder, string name) =>
        ResolvedService.Bridge(
            builder.AddResource(new ServiceContainerResource(name)).WithImage("nginx"), name, "container");

    // ServiceResourceBuilder.Real is internal; the test assembly reaches it through InternalsVisibleTo
    // (src/Aspire.Hosting.ServiceSources/AssemblyInfo.cs), exactly as ServiceResourceBuilderTests does.
    private static ResourceAnnotationCollection RealAnnotations(IResourceBuilder<ServiceResource> service) =>
        ((ServiceResourceBuilder)service).Real!.Resource.Annotations;

    [Fact]
    public void CallbackAddBranch_OnContainerSource_RegistersTheNewEndpointOnTheRealResource()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");
        // Pre-registered through the numeric overload, which dual-writes correctly today: the
        // pre-call snapshot must recognise this instance and leave it alone rather than forwarding
        // it a second time.
        service.WithHttpEndpoint(port: 8080, name: "http");

        service.WithEndpoint("admin", endpoint => endpoint.Port = 9200);

        var onFacade = Assert.Single(
            service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "admin");
        var onReal = Assert.Single(
            RealAnnotations(service).OfType<EndpointAnnotation>(), e => e.Name == "admin");

        Assert.Same(onFacade, onReal);
        Assert.Equal(9200, onFacade.Port);
        // Exactly two endpoints per side: Assert.Single above already rules out a duplicate "admin",
        // and these counts rule out a duplicated "http" from a re-forwarded pre-existing instance.
        Assert.Equal(2, service.Resource.Annotations.OfType<EndpointAnnotation>().Count());
        Assert.Equal(2, RealAnnotations(service).OfType<EndpointAnnotation>().Count());
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void CallbackAddBranch_OnLocalSource_RegistersTheNewEndpointOnTheRealResource()
    {
        var builder = Builder();
        var real = builder.AddResource(new ProjectResource("orders"));
        var service = ResolvedService.Bridge(real, "orders", "local");

        service.WithEndpoint("admin", endpoint => endpoint.Port = 9200);

        var onFacade = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var onReal = Assert.Single(real.Resource.Annotations.OfType<EndpointAnnotation>());

        Assert.Same(onFacade, onReal);
        Assert.Equal("admin", onReal.Name);
        Assert.Equal(9200, onReal.Port);
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void CallbackUpdateAfterACallbackAdd_OnContainerSource_IsVisibleOnTheRealResource()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");

        service.WithEndpoint("admin", endpoint => endpoint.Port = 9200);
        service.WithEndpoint("admin", endpoint => endpoint.Port = 9500);

        var onFacade = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        var onReal = Assert.Single(RealAnnotations(service).OfType<EndpointAnnotation>());

        // Once the first call forwards the instance, the two collections share it, so the second
        // call's in-place update needs no forwarding of its own to be visible on both.
        Assert.Same(onFacade, onReal);
        Assert.Equal(9500, onReal.Port);
    }

    [Fact]
    public void CallbackWithCreateIfNotExistsFalse_OnContainerSource_AddsNothingAndNeverInvokesTheCallback()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");
        var callbackInvoked = false;

        service.WithEndpoint("nope", _ => callbackInvoked = true, createIfNotExists: false);

        Assert.False(callbackInvoked);
        Assert.Empty(service.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Empty(RealAnnotations(service).OfType<EndpointAnnotation>());
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    // The drift detector for scoping the forward to one call site: the numeric overloads are left
    // unwrapped because their add branch ends in builder.WithAnnotation and so dual-writes already.
    // If this fails against a newer Aspire, that stopped being true and the numeric shadows need the
    // same wrap -- which is safe, since the identity guard makes it a no-op when it is not needed.
    [Fact]
    public void NumericAddBranch_OnContainerSource_StillReachesTheRealResource()
    {
        var builder = Builder();
        var service = ContainerService(builder, "orders");

        service.WithEndpoint(port: 9400, name: "metrics", scheme: "http");

        var onFacade = Assert.Single(
            service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.Name == "metrics");
        var onReal = Assert.Single(
            RealAnnotations(service).OfType<EndpointAnnotation>(), e => e.Name == "metrics");

        Assert.Same(onFacade, onReal);
        Assert.Equal(9400, onReal.Port);
    }
}
