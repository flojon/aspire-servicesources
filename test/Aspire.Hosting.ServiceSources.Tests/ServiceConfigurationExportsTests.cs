using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The guest-language shims. #51 exported <c>AddService</c> so a TypeScript AppHost could resolve a
/// service; without these it could resolve one but never configure it — the exact failure #53
/// describes.
/// </summary>
public class ServiceConfigurationExportsTests
{
    private static readonly ServiceMetadata ContainerMetadata = new()
    {
        Container = new ContainerMetadata { Image = "nginxdemos/hello", Port = 8080 },
    };

    private static readonly ServiceMetadata UrlMetadata = new()
    {
        Url = new UrlMetadata { Url = "https://orders.example.com" },
    };

    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

    private static IResourceBuilder<IResourceWithServiceDiscovery> ConfigurableService(
        IDistributedApplicationBuilder builder) =>
        new ContainerSource().Resolve(
            builder, "payments", ContainerMetadata, new ServiceDeveloperConfig { Source = "container" });

    private static IResourceBuilder<IResourceWithServiceDiscovery> UrlService(
        IDistributedApplicationBuilder builder) =>
        new UrlSource().Resolve(builder, "inventory", UrlMetadata, new ServiceDeveloperConfig { Source = "url" });

    private static IEnumerable<MethodInfo> ExportedMethods() =>
        typeof(ServiceConfigurationExports)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttributes(typeof(AspireExportAttribute), inherit: false).Length > 0);

    [Fact]
    public void EveryExportedMethodIsNonGeneric()
    {
        // Aspire's TypeScript generator projects a generic method with its type parameter erased —
        // `configure<T>(...)` becomes `configure(...)`, dropping the capability being requested. A
        // generic export would therefore reach guest languages broken rather than absent.
        var generic = ExportedMethods().Where(m => m.IsGenericMethodDefinition).Select(m => m.Name).ToArray();

        Assert.Empty(generic);
    }

    [Fact]
    public void NoTwoExportedMethodsShareAName()
    {
        // Only the first overload of a name survives codegen; the rest are dropped silently, so a
        // shared name would mean a shape that exists in C# but not in TypeScript.
        var duplicated = ExportedMethods()
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicated);
    }

    [Fact]
    public void ExportsCoverTheConfigurationTheIssueDescribes()
    {
        // #53's motivating AppHost needed all of these on one service.
        var names = ExportedMethods().Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(nameof(ServiceConfigurationExports.WithServiceEnvironment), names);
        Assert.Contains(nameof(ServiceConfigurationExports.WithServiceEnvironmentFromParameter), names);
        Assert.Contains(nameof(ServiceConfigurationExports.WithServiceEnvironmentFromEndpoint), names);
        Assert.Contains(nameof(ServiceConfigurationExports.WithServiceReference), names);
        Assert.Contains(nameof(ServiceConfigurationExports.WithServiceConnectionString), names);
        Assert.Contains(nameof(ServiceConfigurationExports.WaitForService), names);
        Assert.Contains(nameof(ServiceConfigurationExports.WaitForServiceCompletion), names);
    }

    [Fact]
    public void WithServiceEnvironment_AppliesToTheRealResource()
    {
        var builder = Builder();

        var service = ConfigurableService(builder).WithServiceEnvironment("DBUSERNAME", "postgres");

        Assert.NotEmpty(service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
    }

    [Fact]
    public void WithServiceEnvironmentFromParameter_AppliesToTheRealResource()
    {
        var builder = Builder();
        var parameter = builder.AddParameter("EncryptionKey", "s3cret", secret: true);

        var service = ConfigurableService(builder).WithServiceEnvironmentFromParameter("ENCRYPTIONKEY", parameter);

        Assert.NotEmpty(service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
    }

    [Fact]
    public void WaitForService_AppliesToTheRealResource()
    {
        var builder = Builder();
        var dependency = builder.AddResource(new ServiceContainerResource("redis")).WithImage("redis");

        var service = ConfigurableService(builder).WaitForService(dependency);

        Assert.NotEmpty(service.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public void WithServiceArg_AppliesToTheRealResource()
    {
        var builder = Builder();

        var service = ConfigurableService(builder).WithServiceArg("--verbose");

        Assert.NotEmpty(service.Resource.Annotations.OfType<CommandLineArgsCallbackAnnotation>());
    }

    [Fact]
    public void Exports_InheritConfigureSkipBehaviourForOutOfBandSources()
    {
        var builder = Builder();

        var service = UrlService(builder).WithServiceEnvironment("A", "B");

        Assert.Empty(service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
        Assert.Contains("inventory", Assert.Single(ServiceConfigurationWarnings.For(builder).Messages));
    }

    [Fact]
    public void Exports_Chain()
    {
        var builder = Builder();
        var other = UrlService(builder);

        var service = ConfigurableService(builder)
            .WithServiceEnvironment("A", "B")
            .WithServiceReference(other)
            .WithServiceArg("--verbose");

        Assert.Equal("payments", service.Resource.Name);
    }
}
