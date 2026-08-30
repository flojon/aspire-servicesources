using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

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

    private const string OrdersCatalog = """
        services:
          orders:
            repository: https://github.com/company/orders
            project: src/Orders.Api/Orders.Api.csproj
        """;

    /// <summary>
    /// The sources this package inserted on the builder's own configuration — the side effect that
    /// has to happen exactly once however many times the config is asked for.
    /// </summary>
    private static int OurConfigurationSources(IDistributedApplicationBuilder builder) =>
        builder.Configuration.Sources
            .OfType<MemoryConfigurationSource>()
            .Count(source => source.InitialData?.Any(
                entry => entry.Key.StartsWith("ServiceSources:", StringComparison.OrdinalIgnoreCase)) == true);

    /// <remarks>
    /// Loading registers servicesources.local.json on the builder's ConfigurationManager, so it is
    /// not a pure read and must not be repeated: each insert disposes and rebuilds every provider
    /// on that manager, and a second copy of the file would sit under the first for good.
    /// </remarks>
    [Fact]
    public void LoadedFor_CalledRepeatedly_RegistersTheFileSourceOnce()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var first = ServiceSourcesConfigCache.LoadedFor(builder);
        var second = ServiceSourcesConfigCache.LoadedFor(builder);

        Assert.Same(first, second);
        Assert.Equal(1, OurConfigurationSources(builder));
    }

    /// <remarks>
    /// ConditionalWeakTable.GetValue may run its factory concurrently for the same key and keep only
    /// one of the results, so the load cannot live in there — a discarded instance would insert a
    /// second source, from a second thread, into the list the surviving one is mutating.
    /// </remarks>
    [Fact]
    public void LoadedFor_CalledConcurrently_RegistersTheFileSourceOnce()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        // Dedicated threads and a gate they all wait on, rather than pool work items: the point is
        // that they arrive at the load together, which a pool free to run them one at a time would
        // not guarantee.
        const int Callers = 8;
        using var gate = new ManualResetEventSlim(false);
        var loaded = new ServiceSourcesConfigCache.LoadedConfig[Callers];
        var threads = Enumerable.Range(0, Callers)
            .Select(i => new Thread(() =>
            {
                gate.Wait(TimeSpan.FromSeconds(30));
                loaded[i] = ServiceSourcesConfigCache.LoadedFor(builder);
            }))
            .ToArray();

        foreach (var thread in threads)
        {
            thread.Start();
        }

        gate.Set();

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "A concurrent load never finished.");
        }

        Assert.All(loaded, entry => Assert.Same(loaded[0], entry));
        Assert.Equal(1, OurConfigurationSources(builder));
    }
}
