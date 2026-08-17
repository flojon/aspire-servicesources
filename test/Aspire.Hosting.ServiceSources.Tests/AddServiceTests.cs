using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests;

public class AddServiceTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        TestHelpers.CreateBuilder(appHostDirectory);

    private static Task PublishBeforeStartEventAsync(IDistributedApplicationBuilder builder) =>
        TestHelpers.PublishBeforeStartEventAsync(builder);

    [Fact]
    public async Task AddService_LocalSourceWithPathOverride_ReturnsFacadeWrappingRealProject()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(projectDir, "Orders.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), $$"""
            { "services": { "orders": { "source": "local", "path": "{{projectDir.Replace("\\", "\\\\")}}" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");
        await PublishBeforeStartEventAsync(builder);

        Assert.Contains(builder.Resources, r => r.Name == "orders");
        Assert.DoesNotContain(builder.Resources, r => ReferenceEquals(r, service.Resource));
    }

    [Fact]
    public async Task AddService_RelativePathOverride_ResolvesRelativeToAppHostDirectoryNotProcessCwd()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(projectDir, "Orders.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        // A relative path override that is only valid when resolved against appHostDir, not
        // against the test process's current working directory.
        var relativePath = Path.GetRelativePath(appHostDir, projectDir);
        Assert.NotEqual(Path.GetFullPath(relativePath), projectDir);

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), $$"""
            { "services": { "orders": { "source": "local", "path": "{{relativePath.Replace("\\", "\\\\")}}" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");
        await PublishBeforeStartEventAsync(builder);

        Assert.Contains(builder.Resources, r => r.Name == "orders");
    }

    [Fact]
    public void AddService_UnknownSource_ThrowsNamingServiceAndSource()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "docker" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("docker", ex.Message);
    }

    [Fact]
    public async Task AddService_KubernetesSource_AddsPortForwardExecutableAndReturnsFacade()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
                kubernetes:
                  service: orders-svc
                  port: 8080
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "kubernetes", "context": "dev-west", "namespace": "orders-ns" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        Assert.Contains(builder.Resources, r => r.Name == "orders-portforward");
        Assert.DoesNotContain(builder.Resources, r => ReferenceEquals(r, service.Resource));

        var endpoint = service.GetEndpoint("http");
        Assert.Equal("http", endpoint.EndpointName);

        var executable = Assert.IsType<ExecutableResource>(
            Assert.Single(builder.Resources, r => r.Name == "orders-portforward"));

        Assert.Equal("kubectl", executable.Command);

        var argsCallback = executable.Annotations.OfType<CommandLineArgsCallbackAnnotation>().Single();
        var argsContext = new CommandLineArgsCallbackContext(new List<object>());
        await argsCallback.Callback(argsContext);
        var args = argsContext.Args.Cast<string>().ToList();

        Assert.Contains("port-forward", args);
        Assert.Contains("svc/orders-svc", args);
        Assert.Contains("--context", args);
        Assert.Contains("dev-west", args);
        Assert.Contains("--namespace", args);
        Assert.Contains("orders-ns", args);
        Assert.Contains(args, a => System.Text.RegularExpressions.Regex.IsMatch(a, @"^\d+:8080$"));

        var endpointAnnotation = executable.Annotations.OfType<EndpointAnnotation>().Single();
        Assert.Equal(endpointAnnotation.TargetPort, endpointAnnotation.Port);
        Assert.False(endpointAnnotation.IsProxied);
    }

    [Fact]
    public void AddService_UrlSource_ReturnsFacadeResolvingToConfiguredUrl()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
                url:
                  url: https://orders.example.com
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "url" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        Assert.DoesNotContain(builder.Resources, r => ReferenceEquals(r, service.Resource));

        var endpoint = service.GetEndpoint("https");
        Assert.True(endpoint.IsAllocated);
        Assert.Equal("https://orders.example.com:443", endpoint.Url);
    }

    [Fact]
    public void AddService_UrlSourceLocalOverride_TakesPrecedenceOverCatalogUrl()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
                url:
                  url: https://orders.example.com
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "url", "url": "https://orders.dev.internal" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        var endpoint = service.GetEndpoint("https");
        Assert.Equal("https://orders.dev.internal:443", endpoint.Url);
    }

    [Fact]
    public void AddService_UrlSourceMissingUrl_ThrowsNamingServiceAndUrl()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "url" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("url", ex.Message);
    }

    [Fact]
    public void AddService_KubernetesSourceMissingContext_ThrowsNamingServiceAndContext()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
                kubernetes:
                  service: orders-svc
                  port: 8080
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "kubernetes" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("context", ex.Message);
    }

    [Fact]
    public void AddService_ContainerSource_AddsContainerAndReturnsFacade()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
                container:
                  image: ghcr.io/company/orders
                  port: 8080
                  defaultTag: latest
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "container" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        Assert.Contains(builder.Resources, r => r.Name == "orders");
        Assert.DoesNotContain(builder.Resources, r => ReferenceEquals(r, service.Resource));

        var container = Assert.IsType<ContainerResource>(
            Assert.Single(builder.Resources, r => r.Name == "orders"));
        var imageAnnotation = container.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("ghcr.io/company/orders", imageAnnotation.Image);
        Assert.Equal("latest", imageAnnotation.Tag);

        var endpoint = service.GetEndpoint("http");
        Assert.Equal("http", endpoint.EndpointName);

        var endpointAnnotation = container.Annotations.OfType<EndpointAnnotation>().Single();
        Assert.Equal(8080, endpointAnnotation.TargetPort);
        Assert.Null(endpointAnnotation.Port);
        Assert.True(endpointAnnotation.IsProxied);
    }

    [Fact]
    public void AddService_ContainerSourceLocalTagOverride_TakesPrecedenceOverCatalogDefaultTag()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
                container:
                  image: ghcr.io/company/orders
                  port: 8080
                  defaultTag: latest
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "container", "tag": "v1.4.2" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        var container = Assert.IsType<ContainerResource>(
            Assert.Single(builder.Resources, r => r.Name == "orders"));
        var imageAnnotation = container.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.Equal("v1.4.2", imageAnnotation.Tag);
    }

    [Fact]
    public void AddService_ContainerSourceMissingImage_ThrowsNamingServiceAndImage()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "container" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("container.image", ex.Message);
    }
}
