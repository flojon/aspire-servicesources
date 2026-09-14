using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class ResolvedServiceTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    [Fact]
    public void Bridge_ReturnsAFacadeDistinctFromTheRealResource()
    {
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");

        var bridged = ResolvedService.Bridge(real, "orders", "container");

        Assert.IsType<ServiceResource>(bridged.Resource);
        Assert.NotSame(real.Resource, bridged.Resource);
        Assert.Equal("orders", bridged.Resource.Name);
    }

    [Fact]
    public void Bridge_CopiesAnnotationsAlreadyOnTheRealResourceOntoTheFacade()
    {
        // The gap this plan's self-review found: an endpoint added before Bridge is called (exactly
        // what ContainerSource/KubernetesSource/AddProject do) must still be visible through the
        // facade's own Annotations, or GetServiceEndpoint()/GetEndpoint() find nothing.
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders"))
            .WithImage("nginx")
            .WithEndpoint(targetPort: 8080, scheme: "http", name: "http");
        var existingEndpoint = Assert.Single(real.Resource.Annotations.OfType<EndpointAnnotation>());

        var bridged = ResolvedService.Bridge(real, "orders", "container");

        Assert.Same(existingEndpoint, Assert.Single(bridged.Resource.Annotations.OfType<EndpointAnnotation>()));
    }

    [Fact]
    public void Bridge_AddsTheSameServiceSourceAnnotationInstanceToBothCollections()
    {
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");

        var bridged = ResolvedService.Bridge(real, "orders", "container");

        var onFacade = Assert.Single(bridged.Resource.Annotations.OfType<ServiceSourceAnnotation>());
        var onReal = Assert.Single(real.Resource.Annotations.OfType<ServiceSourceAnnotation>());
        Assert.Same(onFacade, onReal);
        Assert.Equal("orders", onFacade.ServiceName);
        Assert.Equal("container", onFacade.Source);
    }

    [Fact]
    public void Bridge_NewAnnotationThroughTheReturnedBuilder_DualWrites()
    {
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");

        var bridged = ResolvedService.Bridge(real, "orders", "container")
            .WithAnnotation(new EnvironmentCallbackAnnotation("A", () => "B"));

        Assert.NotEmpty(bridged.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
        Assert.NotEmpty(real.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
    }

    [Fact]
    public void BridgeUnregistered_TagsOnlyTheFacade_NoRealResourceInvolved()
    {
        var builder = Builder();
        var facade = new ServiceResource("inventory");

        var bridged = ResolvedService.BridgeUnregistered(builder, facade, "inventory", "url");

        var annotation = Assert.Single(bridged.Resource.Annotations.OfType<ServiceSourceAnnotation>());
        Assert.Equal("inventory", annotation.ServiceName);
        Assert.Equal("url", annotation.Source);
    }
}
