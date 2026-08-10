using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

public class ServiceSourcesConfigCacheTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [],
        });

    private static string CreateAppHostDirectory(string yaml, string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), yaml);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), json);
        return dir;
    }

    [Fact]
    public void ResolveService_ReturnsMetadataAndDeveloperConfig_WhenPresentInBothFiles()
    {
        var dir = CreateAppHostDirectory(
            """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
            """,
            """{ "services": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var (metadata, developerConfig) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("https://github.com/company/orders", metadata.Repository);
        Assert.Equal("local", developerConfig.Source);
    }

    [Fact]
    public void ResolveService_ServiceMissingFromCatalog_ThrowsNamingService()
    {
        var dir = CreateAppHostDirectory(
            "services: {}",
            """{ "services": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("servicesources.yaml", ex.Message);
    }

    [Fact]
    public void ResolveService_ServiceMissingFromDeveloperConfig_ThrowsNamingService()
    {
        var dir = CreateAppHostDirectory(
            """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
            """,
            """{ "services": {} }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("servicesources.local.json", ex.Message);
    }

    [Fact]
    public void GetCacheDirectory_ExpandsTildeToHomeDirectory()
    {
        var dir = CreateAppHostDirectory(
            "services: {}",
            """{ "cacheDirectory": "~/.servicesources/repos", "services": {} }""");

        var builder = CreateBuilder(dir);

        var cacheDirectory = ServiceSourcesConfigCache.GetCacheDirectory(builder);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".servicesources/repos"), cacheDirectory);
    }

    [Fact]
    public void GetCacheDirectory_DefaultsWhenNotConfigured()
    {
        var dir = CreateAppHostDirectory("services: {}", """{ "services": {} }""");
        var builder = CreateBuilder(dir);

        var cacheDirectory = ServiceSourcesConfigCache.GetCacheDirectory(builder);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".servicesources/repos"), cacheDirectory);
    }

    [Fact]
    public void GetCacheDirectory_RelativePath_AnchorsToAppHostDirectoryNotProcessCwd()
    {
        var dir = CreateAppHostDirectory(
            "services: {}",
            """{ "cacheDirectory": "relative-cache", "services": {} }""");

        var builder = CreateBuilder(dir);

        var cacheDirectory = ServiceSourcesConfigCache.GetCacheDirectory(builder);

        Assert.Equal(Path.Combine(dir, "relative-cache"), cacheDirectory);
        Assert.NotEqual(Path.Combine(Environment.CurrentDirectory, "relative-cache"), cacheDirectory);
    }
}
