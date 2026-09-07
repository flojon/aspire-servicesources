using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;

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
        var builder = CreateBuilder(dir);

        builder.AddServiceCatalog(c => c.AddService("orders"));
        builder.AddServiceCatalog(c => c.AddService("payments"));

        // Task 10 wires this into LoadedConfig; until then, assert indirectly is not possible —
        // this test is completed in Task 10 once ResolveService can see code-declared entries.
        // For now it asserts only that two calls do not throw.
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
}
