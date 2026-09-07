using System.Reflection;
using Aspire.Hosting;
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
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

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

    /// <summary>
    /// The environment the callbacks past <paramref name="alreadyPresent"/> contribute, with every
    /// value provider resolved. Mirrors <c>BackingServiceConsumerTests</c>' own helper, which reaches
    /// past the same limitation: a plain test builder cannot run <c>WithProjectDefaults</c> to
    /// completion, so counting before and after the call under test is what isolates the callbacks
    /// this test added from everything Aspire's own resource setup contributed.
    /// </summary>
    private static async Task<Dictionary<string, string>> MaterializeEnvironmentAsync(
        IResource resource, int alreadyPresent)
    {
        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), resource);

        foreach (var callback in resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Skip(alreadyPresent))
        {
            await callback.Callback(context);
        }

        var materialized = new Dictionary<string, string>();

        foreach (var (key, value) in context.EnvironmentVariables)
        {
            materialized[key] = value switch
            {
                string text => text,
                IValueProvider provider => await provider.GetValueAsync(default) ?? "",
                _ => value.ToString() ?? "",
            };
        }

        return materialized;
    }

    [Fact]
    public void EveryExportedMethodIsNonGeneric()
    {
        // Aspire's TypeScript generator erases a generic method's type parameter to its constraint,
        // so `configure<T>(...)` becomes `configure(obj: Resource)` — dropping the capability being
        // requested, which is the entire content of T. (A generic whose constraint ATS cannot
        // resolve is dropped from the SDK outright instead.) Either way a generic export would
        // reach guest languages broken rather than absent.
        var generic = ExportedMethods().Where(m => m.IsGenericMethodDefinition).Select(m => m.Name).ToArray();

        Assert.Empty(generic);
    }

    [Fact]
    public void NoTwoExportedMethodsShareAName()
    {
        // Exports that project to the same generated name collide, and only one survives codegen —
        // so a shared name would mean a shape that exists in C# but not in TypeScript. Overloads
        // can be projected under distinct names via [AspireExport("id")]/MethodName, but keeping
        // the C# names distinct as well means both languages see the same set of shapes.
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
        Assert.Contains(nameof(ServiceConfigurationExports.WithServiceArg), names);
    }

    [Fact]
    public void WithServiceEnvironment_AppliesToTheRealResource()
    {
        var builder = Builder();

        var service = ConfigurableService(builder).WithServiceEnvironment("DBUSERNAME", "postgres");

        Assert.NotEmpty(service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
    }

    [Fact]
    public async Task WithServiceConnectionString_KeysOnTheSourceResourcesOwnName()
    {
        var builder = Builder();
        var db = builder.AddConnectionString(
            "orders-db", ReferenceExpression.Create($"Host=localhost;Database=orders"));

        var service = ConfigurableService(builder);
        var beforeTheReference = service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count();

        service.WithServiceConnectionString(db);

        var environment = await MaterializeEnvironmentAsync(service.Resource, beforeTheReference);

        Assert.Equal("Host=localhost;Database=orders", environment["ConnectionStrings__orders-db"]);
    }

    /// <summary>
    /// #209: the gap this parameter closes. A guest-language AppHost previously had to rename the
    /// source resource to control the key a consumer's configuration reads it under; this is the
    /// same escape hatch <c>WithReference(source, connectionName)</c> gives a C# AppHost.
    /// </summary>
    [Fact]
    public async Task WithServiceConnectionString_WithConnectionName_OverridesTheKey()
    {
        var builder = Builder();
        var db = builder.AddConnectionString(
            "orders-db", ReferenceExpression.Create($"Host=localhost;Database=orders"));

        var service = ConfigurableService(builder);
        var beforeTheReference = service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Count();

        service.WithServiceConnectionString(db, "OrdersDb");

        var environment = await MaterializeEnvironmentAsync(service.Resource, beforeTheReference);

        Assert.Equal("Host=localhost;Database=orders", environment["ConnectionStrings__OrdersDb"]);
        Assert.DoesNotContain("ConnectionStrings__orders-db", environment.Keys);
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
    public void WithServiceHttpsEndpoint_AppliesToTheRealResource()
    {
        var builder = Builder();

        var service = ConfigurableService(builder).WithServiceHttpsEndpoint();

        Assert.Contains(service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.UriScheme == "https");
    }

    [Fact]
    public void WithServiceHttpEndpoint_AppliesToTheRealResource()
    {
        var builder = Builder();

        var service = ConfigurableService(builder).WithServiceHttpEndpoint();

        Assert.Contains(service.Resource.Annotations.OfType<EndpointAnnotation>(), e => e.UriScheme == "http");
    }

    [Fact]
    public void Exports_InheritConfigureSkipBehaviourForOutOfBandSources()
    {
        var builder = Builder();

        var service = UrlService(builder).WithServiceEnvironment("A", "B");

        Assert.Empty(service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
        Assert.Contains("inventory", Assert.Single(ServiceSourcesWarnings.For(builder).Messages));
    }

    [Fact]
    public void WithServiceHttpsEndpoint_InheritsConfigureSkipBehaviourForOutOfBandSources()
    {
        // The property the ticket names as making the shim safe to write unscoped (#208): a
        // service a developer has switched to "url" is skipped and logged rather than misconfigured.
        // A "url" resource already carries an EndpointAnnotation for its own URL, so the check is
        // that the shim added no *second* one, not that none exists.
        var builder = Builder();
        var url = UrlService(builder);
        var before = url.Resource.Annotations.OfType<EndpointAnnotation>().ToList();

        var service = url.WithServiceHttpsEndpoint();

        Assert.Equal(before, service.Resource.Annotations.OfType<EndpointAnnotation>().ToList());
        Assert.Contains("inventory", Assert.Single(ServiceSourcesWarnings.For(builder).Messages));
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
