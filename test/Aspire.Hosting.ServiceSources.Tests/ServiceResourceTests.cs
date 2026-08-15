using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.Tests;

public class ServiceResourceTests
{
    [Fact]
    public void CreateFacade_IsNotRegisteredInBuilderResources()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var realProject = builder.AddProject("orders", CreateFakeCsproj());
        var resourcesBeforeFacade = builder.Resources.Count;

        ServiceResource.CreateFacade(builder, "orders", realProject);

        Assert.Equal(resourcesBeforeFacade, builder.Resources.Count);
    }

    [Fact]
    public void CreateFacade_CopiesEndpointAnnotationsFromRealResource()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var realProject = builder.AddProject("orders", CreateFakeCsproj())
            .WithHttpEndpoint(name: "http", port: 5001);

        var facade = ServiceResource.CreateFacade(builder, "orders", realProject);

        var realEndpoint = realProject.Resource.Annotations.OfType<EndpointAnnotation>().Single(a => a.Name == "http");
        var facadeEndpoint = facade.Resource.Annotations.OfType<EndpointAnnotation>().Single(a => a.Name == "http");
        Assert.Same(realEndpoint, facadeEndpoint);
    }

    [Fact]
    public void CreateFacade_CanBeUsedWithWithReference()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var realProject = builder.AddProject("orders", CreateFakeCsproj())
            .WithHttpEndpoint(name: "http", port: 5001);
        var facade = ServiceResource.CreateFacade(builder, "orders", realProject);

        var consumer = builder.AddProject("api", CreateFakeCsproj());
        consumer.WithReference(facade);

        var facadeEndpointViaBuilder = facade.GetEndpoint("http");
        Assert.Equal("http", facadeEndpointViaBuilder.EndpointName);
    }

    [Fact]
    public void CreateFacadeForUri_IsNotRegisteredInBuilderResources()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var resourcesBeforeFacade = builder.Resources.Count;

        ServiceResource.CreateFacadeForUri(builder, "orders", new Uri("https://orders.example.com"));

        Assert.Equal(resourcesBeforeFacade, builder.Resources.Count);
    }

    [Fact]
    public void CreateFacadeForUri_EndpointResolvesToGivenUriWithoutRunningApp()
    {
        var builder = DistributedApplication.CreateBuilder([]);

        var facade = ServiceResource.CreateFacadeForUri(builder, "orders", new Uri("https://orders.example.com:8443/"));

        var endpoint = facade.GetEndpoint("https");
        Assert.True(endpoint.IsAllocated);
        Assert.Equal("https://orders.example.com:8443", endpoint.Url);
    }

    private static string CreateFakeCsproj()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "Fake.csproj");
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        return path;
    }
}
