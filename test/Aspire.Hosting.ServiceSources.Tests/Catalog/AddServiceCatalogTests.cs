using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class AddServiceCatalogTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [TestBuilderDefaults.DisableConfigReloadArg],
        });

    [Fact]
    public void AddServiceCatalog_CalledTwice_Appends()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "url" }, "payments": { "source": "url" } } }
            """);
        var builder = CreateBuilder(dir);

        builder.AddServiceCatalog(c => c.AddService("orders").WithUrl("https://orders.example"));
        builder.AddServiceCatalog(c => c.AddService("payments").WithUrl("https://payments.example"));

        // Both calls' entries end up in the same composed catalog — a second AddServiceCatalog
        // call appends rather than replacing the first.
        var (orders, _) = ServiceSourcesConfigCache.ResolveService(builder, "orders");
        var (payments, _) = ServiceSourcesConfigCache.ResolveService(builder, "payments");

        Assert.Equal("https://orders.example", orders.Url!.Url);
        Assert.Equal("https://payments.example", payments.Url!.Url);
    }

    [Fact]
    public void AddServiceCatalog_RegistersDeveloperConfigFileSource()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "url" } } }
            """);
        var builder = CreateBuilder(dir);

        builder.AddServiceCatalog(c => c.AddService("orders"));

        // DeveloperConfigFileSource.EnsureRegistered is idempotent and internal, and it inserts a
        // MemoryConfigurationSource seeded from parsing servicesources.local.json rather than a
        // JsonConfigurationSource pointed at the file directly — so assert the observable behavior
        // instead: the file's entry now resolves through the AppHost's own configuration.
        Assert.Equal("url", builder.Configuration["ServiceSources:Services:orders:source"]);
    }

    [Fact]
    public void AddServiceCatalog_CalledAfterFirstAddService_ThrowsOrderingError()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "orders": { "source": "url" } } }""");
        var builder = CreateBuilder(dir);
        builder.AddServiceCatalog(c => c.AddService("orders").WithUrl("https://example.com"));
        ServiceSourcesConfigCache.ResolveService(builder, "orders"); // reads and freezes the catalog

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddServiceCatalog(c => c.AddService("payments")));

        Assert.Contains("already", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddServiceCatalog_NotCalledAfterAddBackingService_NoOrderingError()
    {
        // Design finding 1: AddBackingService does not read the catalog, so it must not trip the
        // ordering check. Guards the wrong rule an earlier draft of the design carried from creeping
        // back in.
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var builder = CreateBuilder(dir);
        builder.AddBackingService("db", () => builder.AddConnectionString("db"));

        // Must not throw.
        builder.AddServiceCatalog(c => c.AddService("orders").WithUrl("https://example.com"));
    }
}
