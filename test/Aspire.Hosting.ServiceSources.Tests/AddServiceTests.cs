using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;
using System.Reflection;

namespace Aspire.Hosting.ServiceSources.Tests;

public class AddServiceTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        TestHelpers.CreateBuilder(appHostDirectory);

    [Fact]
    public void AddService_LocalSourceWithPathOverride_ReturnsTheRealRegisteredProject()
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

        Assert.Contains(builder.Resources, r => r.Name == "orders");
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, service.Resource));
    }

    [Fact]
    public void AddService_RelativePathOverride_ResolvesRelativeToAppHostDirectoryNotProcessCwd()
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

        Assert.Contains(builder.Resources, r => r.Name == "orders");
    }

    /// <summary>
    /// A source <em>value</em> spelled with different casing than the source's own name still
    /// resolves. Configuration keys are case-insensitive everywhere, so a developer who capitalises
    /// a value the way they would capitalise anything else — especially in an environment variable —
    /// has not made a mistake, and used to be told the source was not implemented (#167).
    /// </summary>
    [Fact]
    public void AddService_SourceValueSpelledWithACapital_ResolvesTheSameSource()
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
            { "services": { "orders": { "source": "Local", "path": "{{projectDir.Replace("\\", "\\\\")}}" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        Assert.Contains(builder.Resources, r => ReferenceEquals(r, service.Resource));
    }

    /// <summary>
    /// Casing carries no meaning for any source, not just <c>"local"</c>: the lookup is
    /// case-insensitive, and the field validation that follows runs against the resolved source's
    /// relevant fields exactly as it does for the canonical spelling.
    /// </summary>
    [Fact]
    public void AddService_UppercaseSourceValue_StillValidatesAgainstThatSourcesFields()
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
            { "services": { "orders": { "source": "URL", "port": 8080 } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        // The "not valid for source" complaint, not the unknown-source one: the source resolved,
        // and 'port' simply isn't one of its fields.
        Assert.Contains("'port'", ex.Message);
        Assert.Contains("not valid for source", ex.Message);
    }

    /// <summary>
    /// The message for a source nobody implements has to say that, and only that: with
    /// case-insensitive matching it can no longer fire for a source that exists under another
    /// spelling, so it names the sources that do exist rather than hinting the feature is pending
    /// (#167).
    /// </summary>
    [Fact]
    public void AddService_UnknownSource_NamesTheSourcesThatDoExist()
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

        Assert.Contains("'local'", ex.Message);
        Assert.Contains("'kubernetes'", ex.Message);
        Assert.Contains("'url'", ex.Message);
        Assert.Contains("'container'", ex.Message);
        Assert.DoesNotContain("not implemented yet", ex.Message);
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
    public async Task AddService_KubernetesSource_AddsPortForwardExecutableAndReturnsIt()
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

        // Named for the service, not "orders-portforward": Aspire derives service-discovery keys
        // from the resource name, so a suffix would publish this as "services__orders-portforward"
        // and break a consumer resolving "orders".
        Assert.Equal("orders", service.Resource.Name);
        Assert.DoesNotContain(builder.Resources, r => r.Name.Contains("portforward", StringComparison.Ordinal));
        // No facade any more: the returned builder wraps the registered port-forward executable
        // itself, so DCP creates a Service for it and a container consumer can reference it (#58).
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, service.Resource));

        var endpoint = service.GetEndpoint("http");
        Assert.Equal("http", endpoint.EndpointName);

        var executable = Assert.IsAssignableFrom<ExecutableResource>(
            Assert.Single(builder.Resources, r => r.Name == "orders"));

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
    public void AddService_UrlSource_StaysUnregisteredAndResolvesToConfiguredUrl()
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

        // The one source that still hands back an unregistered resource: there is nothing for
        // Aspire to run, and ExternalServiceResource — which DCP would materialize — is sealed and
        // can't satisfy IResourceWithServiceDiscovery. See UrlSource's remarks and issue #58.
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
    public void AddService_ContainerSource_AddsContainerAndReturnsIt()
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
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, service.Resource));

        var container = Assert.IsAssignableFrom<ContainerResource>(
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

        var container = Assert.IsAssignableFrom<ContainerResource>(
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

    [Fact]
    public void AddService_IsExportedToAts()
    {
        var method = typeof(ServiceSourcesBuilderExtensions).GetMethods()
            .Single(m => m.Name == nameof(ServiceSourcesBuilderExtensions.AddService));

        var exportAttribute = method.GetCustomAttributes(typeof(AspireExportAttribute), inherit: false);
        Assert.Single(exportAttribute);

        var nameParameter = method.GetParameters().Single(p => p.Name == "name");
        var resourceNameAttribute = nameParameter.GetCustomAttributes(typeof(ResourceNameAttribute), inherit: false);
        Assert.Single(resourceNameAttribute);
    }

    [Fact]
    public void AddService_ContainerSourceWithForeignPortField_ThrowsNamingServiceFieldAndSource()
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
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "container", "port": 9090 } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("port", ex.Message);
        Assert.Contains("container", ex.Message);
    }
}
