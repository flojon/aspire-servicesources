using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class CatalogCompositionTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [TestBuilderDefaults.DisableConfigReloadArg],
        });

    [Fact]
    public void CodeOnlyCatalog_NoYamlFile_ResolvesService()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" } } }""");
        var builder = CreateBuilder(dir);

        builder.AddServiceCatalog(c => c.AddService("inventory").WithUrl("https://example.com"));

        var (definition, _) = ServiceSourcesConfigCache.ResolveService(builder, "inventory");

        Assert.Equal("https://example.com", definition.Url!.Url);
    }

    [Fact]
    public void YamlOnlyCatalog_Unchanged()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              inventory:
                url:
                  url: https://example.com
            """);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" } } }""");
        var builder = CreateBuilder(dir);

        var (definition, _) = ServiceSourcesConfigCache.ResolveService(builder, "inventory");

        Assert.Equal("https://example.com", definition.Url!.Url);
    }

    [Fact]
    public void BothDisjoint_BothResolve()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              inventory:
                url:
                  url: https://example.com
            """);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" }, "payments": { "source": "url" } } }""");
        var builder = CreateBuilder(dir);
        builder.AddServiceCatalog(c => c.AddService("payments").WithUrl("https://payments.example"));

        var (yamlDef, _) = ServiceSourcesConfigCache.ResolveService(builder, "inventory");
        var (codeDef, _) = ServiceSourcesConfigCache.ResolveService(builder, "payments");

        Assert.Equal("https://example.com", yamlDef.Url!.Url);
        Assert.Equal("https://payments.example", codeDef.Url!.Url);
    }

    [Fact]
    public void SameNameInBothCatalogs_ThrowsNamingBothSources()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var yamlPath = Path.Combine(dir, "servicesources.yaml");
        File.WriteAllText(yamlPath,
            """
            services:
              payments:
                url:
                  url: https://example.com
            """);
        var builder = CreateBuilder(dir);
        builder.AddServiceCatalog(c => c.AddService("payments").WithUrl("https://other.example"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "payments"));

        Assert.Contains("payments", ex.Message, StringComparison.Ordinal);
        Assert.Contains("code", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(yamlPath, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeAndYamlNamesDifferingOnlyByCase_IsTheDuplicateError_NotAmbiguousCatalogSpelling()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              Payments:
                url:
                  url: https://example.com
            """);
        var builder = CreateBuilder(dir);
        builder.AddServiceCatalog(c => c.AddService("payments").WithUrl("https://other.example"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "payments"));

        Assert.DoesNotContain("declares more than once", ex.Message, StringComparison.Ordinal);
        Assert.Contains("payments", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void YamlDeclaresTwoCaseVariants_StillAmbiguousCatalogSpellingError_NotArgumentException()
    {
        // Design finding 11: the merge must not silently turn this into an ArgumentException by
        // building the merged map as new Dictionary<string, ServiceDefinition>(OrdinalIgnoreCase).
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              orders:
                url:
                  url: https://a.example
              Orders:
                url:
                  url: https://b.example
            """);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "orders": { "source": "url" } } }""");
        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("declares more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddServiceCatalog_LookupAgainstYamlOnly_StaysOrdinal()
    {
        // Design finding 11: the map itself stays Ordinal, so AddService("Orders") against a yaml
        // "orders:" still reports not-found — unchanged from today.
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              orders:
                url:
                  url: https://example.com
            """);
        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "Orders"));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NeitherCatalogDeclaresAnything_ExtendedNoCatalogMessage()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "anything"));

        Assert.Contains("servicesources.yaml", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddServiceCatalog", ex.Message, StringComparison.Ordinal);
    }
}
