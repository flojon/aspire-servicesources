using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

/// <summary>
/// The developer's source selection is read through the AppHost's <c>IConfiguration</c>, so
/// <c>servicesources.local.json</c> is the lowest layer of the standard provider chain rather than
/// the only place a value can come from.
/// </summary>
public class DeveloperConfigurationTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [],
        });

    private const string OrdersCatalog = """
        services:
          orders:
            repository: https://github.com/company/orders
            project: src/Orders.Api/Orders.Api.csproj
        """;

    private const string EnvOverrideCatalog = """
        services:
          envoverride:
            repository: https://github.com/company/envoverride
            project: src/EnvOverride/EnvOverride.csproj
        """;

    private static string CreateAppHostDirectory(string yaml, string? json = null)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), yaml);
        if (json is not null)
        {
            File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), json);
        }
        return dir;
    }

    /// <remarks>
    /// Environment variables are process-global and xunit runs test classes in parallel, so the
    /// service this test names must be one no other test uses — otherwise the variable is still
    /// set while another class builds its own AppHost and silently configures its service.
    /// </remarks>
    [Fact]
    public void ResolveService_EnvironmentVariableOverridesTheFile()
    {
        var dir = CreateAppHostDirectory(
            EnvOverrideCatalog,
            """{ "services": { "envoverride": { "source": "local" } } }""");

        Environment.SetEnvironmentVariable("ServiceSources__Services__envoverride__Source", "url");
        try
        {
            var builder = CreateBuilder(dir);

            var (_, developerConfig) = ServiceSourcesConfigCache.ResolveService(builder, "envoverride");

            Assert.Equal("url", developerConfig.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceSources__Services__envoverride__Source", null);
        }
    }

    [Fact]
    public void ResolveService_NoDeveloperConfigurationAnywhere_ThrowsNamingTheKeyAndTheSources()
    {
        var dir = CreateAppHostDirectory(OrdersCatalog);

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("ServiceSources:Services", ex.Message);
        Assert.Contains(Path.Combine(dir, "servicesources.local.json"), ex.Message);
        Assert.Contains("environment variable", ex.Message);
    }

    [Fact]
    public void ResolveService_OtherServicesConfiguredButNotThisOne_ThrowsNamingTheServiceKey()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "payments": { "source": "url" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("ServiceSources:Services:orders:source", ex.Message);
        Assert.Contains(Path.Combine(dir, "servicesources.local.json"), ex.Message);
    }

    [Fact]
    public void ResolveService_ReadsEveryFieldFromTheFile()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """
            {
              "services": {
                "orders": {
                  "source": "kubernetes",
                  "path": "/home/dev/code/orders",
                  "ref": "feature/new-checkout",
                  "context": "dev-west",
                  "namespace": "orders-ns",
                  "port": 8080,
                  "url": "https://orders.example",
                  "tag": "v1.4.2"
                }
              }
            }
            """);

        var builder = CreateBuilder(dir);

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("kubernetes", config.Source);
        Assert.Equal("/home/dev/code/orders", config.Path);
        Assert.Equal("feature/new-checkout", config.Ref);
        Assert.Equal("dev-west", config.Context);
        Assert.Equal("orders-ns", config.Namespace);
        Assert.Equal(8080, config.Port);
        Assert.Equal("https://orders.example", config.Url);
        Assert.Equal("v1.4.2", config.Tag);
    }

    [Fact]
    public void ResolveService_FieldsOmittedFromTheFile_AreLeftNull()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("local", config.Source);
        Assert.Null(config.Path);
        Assert.Null(config.Ref);
        Assert.Null(config.Context);
        Assert.Null(config.Namespace);
        Assert.Null(config.Port);
        Assert.Null(config.Url);
        Assert.Null(config.Tag);
    }

    /// <remarks>
    /// The hand-rolled loader needed a null-coercing setter to survive an explicit
    /// <c>"services": null</c>; configuration binding has no such quirk, and all three shapes below
    /// simply produce an empty section.
    /// </remarks>
    [Theory]
    [InlineData("""{ "services": null }""")]
    [InlineData("""{ "services": {} }""")]
    [InlineData("{ }")]
    public void ResolveService_FileConfiguresNoServices_ThrowsTheNothingConfiguredError(string json)
    {
        var dir = CreateAppHostDirectory(OrdersCatalog, json);

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("ServiceSources:Services", ex.Message);
        Assert.Contains("empty in every configuration source", ex.Message);
    }

    /// <remarks>
    /// A real AppHost runs from its own directory, so its <c>appsettings.json</c> is on the chain
    /// without anyone arranging it. A test builder only gets <c>ProjectDirectory</c>, which sets
    /// <c>AppHostDirectory</c> and nothing else, so the content root has to be pointed at the same
    /// place for the standard providers to look where a real run would.
    /// </remarks>
    [Fact]
    public void ResolveService_AppSettingsOverridesTheFile()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "orders": { "source": "local" } } }""");
        File.WriteAllText(
            Path.Combine(dir, "appsettings.json"),
            """{ "ServiceSources": { "Services": { "orders": { "source": "url" } } } }""");

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = dir,
            Args = ["--contentRoot", dir],
        });

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("url", config.Source);
    }

    /// <summary>
    /// The environment-specific layer, which is what makes named profiles fall out of the standard
    /// chain rather than needing a profile mechanism of their own.
    /// </summary>
    [Fact]
    public void ResolveService_EnvironmentSpecificAppSettingsOverridesAppSettings()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "orders": { "source": "local" } } }""");
        File.WriteAllText(
            Path.Combine(dir, "appsettings.json"),
            """{ "ServiceSources": { "Services": { "orders": { "source": "url" } } } }""");
        File.WriteAllText(
            Path.Combine(dir, "appsettings.Cluster.json"),
            """{ "ServiceSources": { "Services": { "orders": { "source": "kubernetes" } } } }""");

        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = dir,
            Args = ["--contentRoot", dir, "--environment", "Cluster"],
        });

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("kubernetes", config.Source);
    }

    /// <remarks>
    /// Configuration keys are case-insensitive everywhere else in .NET, so a service named with
    /// different casing in an environment variable has to reach the same service — otherwise the
    /// override silently does nothing and the service reports itself unconfigured.
    /// </remarks>
    [Fact]
    public void ResolveService_ServiceNameCasingInConfigurationDoesNotMatter()
    {
        var dir = CreateAppHostDirectory(
            EnvOverrideCatalog,
            """{ "services": { "envoverride": { "source": "local" } } }""");

        Environment.SetEnvironmentVariable("ServiceSources__Services__EnvOverride__Source", "url");
        try
        {
            var builder = CreateBuilder(dir);

            var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "envoverride");

            Assert.Equal("url", config.Source);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceSources__Services__EnvOverride__Source", null);
        }
    }
}
