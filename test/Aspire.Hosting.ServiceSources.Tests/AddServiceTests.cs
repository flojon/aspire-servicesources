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
            { "services": { "orders": { "source": "local", "local": { "path": "{{projectDir.Replace("\\", "\\\\")}}" } } } }
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
            { "services": { "orders": { "source": "local", "local": { "path": "{{relativePath.Replace("\\", "\\\\")}}" } } } }
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
            { "services": { "orders": { "source": "Local", "local": { "path": "{{projectDir.Replace("\\", "\\\\")}}" } } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        Assert.Contains(builder.Resources, r => ReferenceEquals(r, service.Resource));
    }

    /// <summary>
    /// Casing carries no meaning for any source, not just <c>"local"</c>: the lookup is
    /// case-insensitive, and the key validation that follows runs exactly as it does for the
    /// canonical spelling.
    /// </summary>
    [Fact]
    public void AddService_UppercaseSourceValue_StillValidatesTheRestOfTheEntry()
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

        // The shape complaint, not the unknown-source one: the source resolved, so what runs is key
        // validation. (#176 reworded this from "not valid for source" — a block belonging to another
        // source is now ignored rather than rejected, so what is left to reject is a key in the
        // wrong place.)
        Assert.Contains("'port'", ex.Message);
        Assert.Contains("is not a valid key here", ex.Message);
        Assert.DoesNotContain("unknown source", ex.Message);
    }

    /// <summary>
    /// The message for a source nobody implements has to say that, and only that: with
    /// case-insensitive matching it can no longer fire for a source that exists under another
    /// spelling, so it names the sources that do exist rather than hinting the feature is pending
    /// (#167).
    /// </summary>
    /// <remarks>
    /// The kind of report is pinned, not just the names it interpolates. A source name that is
    /// present but unrecognised has to reach the unknown-source complaint and never the "no source
    /// configured" report reserved for a blank or absent source, since the two sit on this same code
    /// path — and a variant of that message that still quoted the service and the source would
    /// satisfy every other assertion here (#168).
    /// </remarks>
    [Fact]
    public void AddService_UnknownSource_ReportsItAsUnknownAndNamesTheSourcesThatDoExist()
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
        Assert.Contains("unknown source", ex.Message);
        Assert.Contains("'local'", ex.Message);
        Assert.Contains("'kubernetes'", ex.Message);
        Assert.Contains("'url'", ex.Message);
        Assert.Contains("'container'", ex.Message);
        Assert.DoesNotContain("not implemented yet", ex.Message);

        // The key as well as the file, since the file is only the lowest layer it can arrive from.
        Assert.Contains("'ServiceSources:Services:orders:source'", ex.Message);
        Assert.Contains("ServiceSources__Services__orders__source", ex.Message);
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
            { "services": { "orders": { "source": "kubernetes", "kubernetes": { "context": "dev-west", "namespace": "orders-ns" } } } }
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
            { "services": { "orders": { "source": "url", "url": { "url": "https://orders.dev.internal" } } } }
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
            { "services": { "orders": { "source": "container", "container": { "tag": "v1.4.2" } } } }
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

    /// <remarks>
    /// An entry that names no source at all, and one whose source a higher layer blanked — the
    /// gesture for dropping a value a layer below set — both bind <c>Source</c> to the empty
    /// string. Neither is a source this package has yet to implement; the service simply has no
    /// source, which is the condition <c>NotConfiguredError</c> describes and names the fix for.
    /// </remarks>
    [Theory]
    [InlineData("""{ "services": { "orders": { "local": { "ref": "main" } } } }""")]
    [InlineData("""{ "services": { "orders": { "source": "" } } }""")]
    public void AddService_EntryWithNoSource_ReportsItAsNotConfigured(string developerConfig)
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), developerConfig);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("'orders' has no source configured", ex.Message);
        Assert.DoesNotContain("not implemented", ex.Message);
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
    public void AddService_PortInsideContainerBlock_ThrowsNamingTheBlockAndItsValidKeys()
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
            { "services": { "orders": { "source": "container", "container": { "port": 9090 } } } }
            """);

        var builder = CreateBuilder(appHostDir);

        // The image decides the port it serves, so there is nothing per-developer to override and
        // the container block has no 'port'. Written inside the block, that is an unknown key there
        // rather than a field belonging to another source.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("port", ex.Message);
        Assert.Contains("container", ex.Message);
    }

    [Fact]
    public void AddService_KubernetesSourceWithHttpsSchemeInCatalog_ExposesAnHttpsEndpoint()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
                kubernetes:
                  service: orders-svc
                  port: 8443
                  scheme: https
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "kubernetes", "kubernetes": { "context": "dev-west" } } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        // The whole point of #160: a consumer's GetEndpoint("https") now resolves for a
        // kubernetes-sourced service too, and the URL it hands out says https.
        Assert.Equal("https", service.GetEndpoint("https").EndpointName);
        Assert.Equal("https", service.GetServiceEndpoint().EndpointName);
    }

    [Fact]
    public void AddService_KubernetesSourceWithSchemeOverrideInDeveloperConfig_TakesPrecedence()
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
            { "services": { "orders": { "source": "kubernetes",
                "kubernetes": { "context": "dev-west", "port": 8443, "scheme": "https" } } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        // Scheme travels with port: a developer forwarding a different port is the one who knows
        // what that port speaks, so the override lives in the same block as the port override.
        Assert.Equal("https", service.GetServiceEndpoint().EndpointName);
    }

    [Fact]
    public void AddService_SchemeInsideLocalBlock_ThrowsNamingTheBlockAndItsValidKeys()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "local",
                "local": { "path": "/tmp/orders", "scheme": "https" } } } }
            """);

        var builder = CreateBuilder(appHostDir);

        // Scheme is a kubernetes port-forward's concern; a local checkout's endpoint comes from the
        // project's own launch profile. The block it was written in is the one named.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("scheme", ex.Message);
        Assert.Contains("local", ex.Message);
    }

    [Fact]
    public void AddService_SchemeInsideContainerBlock_ThrowsNamingTheBlockAndItsValidKeys()
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
            { "services": { "orders": { "source": "container", "container": { "scheme": "https" } } } }
            """);

        var builder = CreateBuilder(appHostDir);

        // Scheme is catalog-only for "container", exactly as port is: the image decides what it
        // serves, so there is nothing per-developer to override.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("scheme", ex.Message);
        Assert.Contains("container", ex.Message);
    }

    [Fact]
    public void AddService_MisspelledSchemeInCatalogKubernetesBlock_ThrowsNamingTheUnknownProperty()
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
                  schema: https
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "kubernetes", "kubernetes": { "context": "dev-west" } } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("schema", ex.Message);
        Assert.Contains("scheme", ex.Message);
    }

    /// <remarks>
    /// The service name, the block names and the field names in an entry are all matched the way
    /// IConfiguration compares keys. The source is matched by a dictionary lookup instead, and is
    /// the value most likely to be typed by hand into an environment variable, so an ordinal match
    /// would answer 'Url' by naming a missing feature rather than the capital U.
    /// </remarks>
    [Theory]
    [InlineData("url")]
    [InlineData("Url")]
    [InlineData("URL")]
    public void AddService_SourceValueInAnyCasing_ResolvesTheSameSource(string source)
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              inventory:
                url:
                  url: https://inventory.invalid
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), $$"""
            { "services": { "inventory": { "source": "{{source}}" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("inventory");

        Assert.Equal("inventory", service.Resource.Name);
    }
}
