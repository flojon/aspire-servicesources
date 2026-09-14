using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class ServiceResourceBuilderTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    private static (ServiceResource Facade, IResourceBuilder<ServiceContainerResource> Real,
        ServiceResourceBuilder Wrapper) ContainerCase(IDistributedApplicationBuilder builder)
    {
        var facade = new ServiceResource("orders");
        var real = builder.AddResource(new ServiceContainerResource("orders")).WithImage("nginx");
        var wrapper = new ServiceResourceBuilder(builder, facade, real, "container");
        return (facade, real, wrapper);
    }

    [Fact]
    public void Real_ExposesTheRealBuilderPassedToTheConstructor()
    {
        var builder = Builder();
        var (_, real, wrapper) = ContainerCase(builder);

        // Task 8's Unwrap<T> depends on this — it is the only way to reach the real, source-specific
        // resource, since Resource above is always the facade.
        Assert.Same(real, wrapper.Real);
    }

    [Fact]
    public void WithAnnotation_Append_AddsTheSameInstanceToBothCollections()
    {
        var builder = Builder();
        var (facade, real, wrapper) = ContainerCase(builder);
        // EnvironmentAnnotation itself is internal to Aspire.Hosting.dll; EnvironmentCallbackAnnotation
        // (its public base, and what WithEnvironment's callback overloads construct directly) is what
        // this package's own code can name — WithAnnotation<TAnnotation> is generic over either.
        var annotation = new EnvironmentCallbackAnnotation("A", () => "B");

        wrapper.WithAnnotation(annotation);

        Assert.Same(annotation, Assert.Single(facade.Annotations.OfType<EnvironmentCallbackAnnotation>()));
        Assert.Same(annotation, Assert.Single(real.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>()));
    }

    [Fact]
    public void WithAnnotation_Replace_RemovesTheFacadesExistingOneFirst()
    {
        var builder = Builder();
        var (facade, real, wrapper) = ContainerCase(builder);
        var first = new EnvironmentCallbackAnnotation("A", () => "1");
        var second = new EnvironmentCallbackAnnotation("A", () => "2");

        wrapper.WithAnnotation(first);
        wrapper.WithAnnotation(second, ResourceAnnotationMutationBehavior.Replace);

        Assert.Same(second, Assert.Single(facade.Annotations.OfType<EnvironmentCallbackAnnotation>()));
    }

    [Fact]
    public void WithAnnotation_UrlSource_SkipsEveryAnnotationAndWarns()
    {
        var builder = Builder();
        var facade = new ServiceResource("inventory");
        var wrapper = new ServiceResourceBuilder(builder, facade, real: null, "url");

        wrapper.WithAnnotation(new EnvironmentCallbackAnnotation("A", () => "B"));

        Assert.Empty(facade.Annotations.OfType<EnvironmentCallbackAnnotation>());
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("inventory", message);
        Assert.Contains("'url'", message);
    }

    [Fact]
    public void WithAnnotation_KubernetesSource_SkipsEnvironmentButAppliesWait()
    {
        var builder = Builder();
        var facade = new ServiceResource("orders");
        var real = builder.AddResource(
                new ServiceExecutableResource("orders", "kubectl", builder.AppHostDirectory))
            .WithArgs("port-forward");
        var wrapper = new ServiceResourceBuilder(builder, facade, real, "kubernetes");

        wrapper.WithAnnotation(new EnvironmentCallbackAnnotation("A", () => "B"));
        Assert.Empty(facade.Annotations.OfType<EnvironmentCallbackAnnotation>());

        var wait = new WaitAnnotation(real.Resource, WaitType.WaitUntilHealthy);
        wrapper.WithAnnotation(wait);
        Assert.Same(wait, Assert.Single(facade.Annotations.OfType<WaitAnnotation>()));
        Assert.Same(wait, Assert.Single(real.Resource.Annotations.OfType<WaitAnnotation>()));
    }
}
