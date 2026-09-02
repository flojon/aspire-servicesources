using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Configuration;

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
        Assert.Contains("under \"services\" in", ex.Message);
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
                  "local": {
                    "path": "/home/dev/code/orders",
                    "ref": "feature/new-checkout"
                  },
                  "kubernetes": {
                    "context": "dev-west",
                    "namespace": "orders-ns",
                    "port": 8080
                  },
                  "url": {
                    "url": "https://orders.example"
                  },
                  "container": {
                    "tag": "v1.4.2"
                  }
                }
              }
            }
            """);

        var builder = CreateBuilder(dir);

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("kubernetes", config.Source);
        Assert.Equal("/home/dev/code/orders", config.Local.Path);
        Assert.Equal("feature/new-checkout", config.Local.Ref);
        Assert.Equal("dev-west", config.Kubernetes.Context);
        Assert.Equal("orders-ns", config.Kubernetes.Namespace);
        Assert.Equal(8080, config.Kubernetes.Port);
        Assert.Equal("https://orders.example", config.Url.Url);
        Assert.Equal("v1.4.2", config.Container.Tag);
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
        Assert.Null(config.Local.Path);
        Assert.Null(config.Local.Ref);
        Assert.Null(config.Kubernetes.Context);
        Assert.Null(config.Kubernetes.Namespace);
        Assert.Null(config.Kubernetes.Port);
        Assert.Null(config.Url.Url);
        Assert.Null(config.Container.Tag);
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

    /// <remarks>
    /// Resolving the service is only half of it. The entry is also enumerated against the catalog by
    /// key — by the checkout prefetch, which matches names ordinally — so the key itself has to be
    /// the catalog's spelling and not whichever one the winning provider happened to use.
    /// </remarks>
    [Fact]
    public void ReadFrom_ConfigurationSpellsTheServiceDifferently_KeysTheEntryByTheCatalogSpelling()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "Orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var loaded = ServiceSourcesConfigCache.LoadedFor(builder);

        Assert.Equal(["orders"], loaded.DeveloperConfig.Services.Keys.ToArray());
    }

    /// <remarks>
    /// Two catalog names differing only by case have no configuration key that reaches one and not
    /// the other, so an entry naming them would silently give both the same source while only one
    /// of the two spellings is the key anything enumerating the catalog matches on. The catalog is
    /// what has to change, and nothing else in the pipeline is in a position to say so.
    /// </remarks>
    [Fact]
    public void ReadFrom_CatalogSpellsTwoServicesTheSameWayApartFromCase_ReportsTheCatalog()
    {
        var dir = CreateAppHostDirectory(
            """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
              Orders:
                repository: https://github.com/company/orders-two
                project: src/Orders.Two/Orders.Two.csproj
            """,
            """{ "services": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.LoadedFor(builder));

        Assert.Contains("'orders'", ex.Message);
        Assert.Contains("'Orders'", ex.Message);
        Assert.Contains("servicesources.yaml", ex.Message);
    }

    /// <remarks>
    /// The ambiguity is only a problem for a name someone configures. A catalog nobody has selected
    /// a source for still loads, so the report above stays about the entry that cannot be honoured
    /// rather than becoming a second catalog validation rule.
    /// </remarks>
    [Fact]
    public void ReadFrom_CatalogSpellingIsAmbiguousButUnconfigured_Loads()
    {
        var dir = CreateAppHostDirectory(
            """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
              Orders:
                repository: https://github.com/company/orders-two
                project: src/Orders.Two/Orders.Two.csproj
              payments:
                repository: https://github.com/company/payments
                project: src/Payments.Api/Payments.Api.csproj
            """,
            """{ "services": { "payments": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var loaded = ServiceSourcesConfigCache.LoadedFor(builder);

        Assert.Equal(["payments"], loaded.DeveloperConfig.Services.Keys.ToArray());
    }

    /// <remarks>
    /// A service the catalog doesn't describe has no spelling to adopt and keeps its own, so the
    /// failure still comes from the catalog lookup — which can name the file that would fix it —
    /// rather than from the entry going missing here.
    /// </remarks>
    [Fact]
    public void ReadFrom_ServiceNotInTheCatalog_KeepsTheCasingConfigurationGaveIt()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "Payments": { "source": "url" } } }""");

        var builder = CreateBuilder(dir);

        var loaded = ServiceSourcesConfigCache.LoadedFor(builder);

        Assert.Equal(["Payments"], loaded.DeveloperConfig.Services.Keys.ToArray());
    }

    /// <remarks>
    /// The file is re-rooted under our own prefix as it joins the chain, which would otherwise make
    /// it a route for anything else in the file to reach the AppHost's live configuration.
    /// </remarks>
    [Fact]
    public void ReadFrom_FileCarriesOtherTopLevelKeys_LeavesThemOutOfTheAppHostConfiguration()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """
            {
              "services": { "orders": { "source": "local" } },
              "ConnectionStrings": { "db": "Server=somewhere" }
            }
            """);

        var builder = CreateBuilder(dir);

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("local", config.Source);
        Assert.Null(builder.Configuration["ServiceSources:ConnectionStrings:db"]);
    }

    /// <remarks>
    /// Some service being configured no longer implies the file exists — CI pins services from the
    /// environment and ships none — so the advice can't send the developer to edit a path that
    /// holds nothing.
    /// </remarks>
    [Fact]
    public void ResolveService_ConfiguredOnlyFromTheEnvironmentAndNoFile_TellsTheDeveloperToCreateIt()
    {
        var dir = CreateAppHostDirectory(OrdersCatalog);

        Environment.SetEnvironmentVariable("ServiceSources__Services__nofilepayments__Source", "url");
        try
        {
            var builder = CreateBuilder(dir);

            var ex = Assert.Throws<ServiceSourcesConfigurationException>(
                () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

            Assert.Contains($"create '{Path.Combine(dir, "servicesources.local.json")}'", ex.Message);
            Assert.DoesNotContain("under \"services\" in", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceSources__Services__nofilepayments__Source", null);
        }
    }

    private const string SwitcherCatalog = """
        services:
          switcher:
            repository: https://github.com/company/switcher
            project: Switcher.csproj
        """;

    /// <remarks>
    /// Environment variables are process-global and xunit runs test classes in parallel, so the
    /// service this test names must be one no other test uses.
    /// </remarks>
    [Fact]
    public void ResolveService_HigherLayerSwitchesSource_LeavesTheOldSourcesBlockUnread()
    {
        var checkout = Directory.CreateTempSubdirectory().FullName;
        var dir = CreateAppHostDirectory(
            SwitcherCatalog,
            """
            { "services": { "switcher": {
                "source": "url",
                "url": { "url": "http://from-local-json.invalid" } } } }
            """);

        Environment.SetEnvironmentVariable("ServiceSources__Services__switcher__Source", "local");
        Environment.SetEnvironmentVariable("ServiceSources__Services__switcher__Local__Path", checkout);
        try
        {
            var builder = CreateBuilder(dir);

            var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "switcher");

            Assert.Equal("local", config.Source);
            Assert.Equal(checkout, config.Local.Path);

            // Still bound, and that is the point: the entry it came from is untouched, and nothing
            // reads it while the effective source is "local".
            Assert.Equal("http://from-local-json.invalid", config.Url.Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceSources__Services__switcher__Source", null);
            Environment.SetEnvironmentVariable("ServiceSources__Services__switcher__Local__Path", null);
        }
    }

    private const string BlankingCatalog = """
        services:
          blanking:
            repository: https://github.com/company/blanking
            project: Blanking.csproj
        """;

    /// <remarks>
    /// Blanking a value is the only gesture a higher layer has for dropping a field the file below
    /// set — configuration can add a key but not remove one. Without this the empty value binds as
    /// "" rather than null, and 'path' in particular then resolves through
    /// Path.GetFullPath("", appHostDirectory) to the AppHost directory itself, which
    /// LocalGitCheckout adopts as the checkout and uses with no clone or fetch.
    ///
    /// The blank arrives on an in-memory layer rather than an environment variable because
    /// Environment.SetEnvironmentVariable(name, "") *deletes* the variable instead of setting it
    /// empty, so the test would assert nothing. A real shell's `VAR= dotnet run` does export an
    /// empty string, and the environment provider reads it as ""; only the in-process gesture for
    /// arranging it differs. Both layers sit above the file, which is inserted at index 0.
    /// </remarks>
    [Fact]
    public void ResolveService_BlankOverride_DropsTheFieldRatherThanBindingEmpty()
    {
        var configured = Directory.CreateTempSubdirectory().FullName;
        var dir = CreateAppHostDirectory(
            BlankingCatalog,
            $$"""
            { "services": { "blanking": { "source": "local",
                "local": { "path": "{{configured.Replace("\\", "\\\\")}}" } } } }
            """);

        var builder = CreateBuilder(dir);
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ServiceSources:Services:blanking:Local:Path"] = "" });

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "blanking");

        // Source comes only from the file, so it proves the file layer bound at all — without it a
        // null Path could as easily mean the file's value never arrived as that the blank dropped it.
        Assert.Equal("local", config.Source);
        Assert.Null(config.Local.Path);

        // The bug this closes: with the path left as "", PrepareRepoRoot takes its override branch
        // and Path.GetFullPath("", appHostDirectory) hands back the AppHost directory, which it then
        // adopts as the checkout. Absent, it uses the managed checkout instead. The .git directory
        // is pre-seeded so the adopt-existing branch answers without needing a git client.
        var repoRoot = Path.Combine(dir, ".servicesources", "checkouts", "blanking");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

        var prepared = LocalGitCheckout.PrepareRepoRoot(
            "blanking", new ServiceMetadata(), config, dir, gitClient: null!);

        Assert.Equal(repoRoot, prepared.RepoRoot);
        Assert.NotEqual(dir, prepared.RepoRoot);
    }

    private const string BlankUrlCatalog = """
        services:
          blankurl:
            url:
              url: https://from-catalog.example.com
        """;

    /// <remarks>
    /// Written blank in the file rather than overridden blank from above, which is the other way
    /// the value arrives empty. Before normalization this bound as "" and shadowed the catalog's
    /// url.url, so the service failed as "no url configured" while the catalog had one all along.
    /// </remarks>
    [Fact]
    public void ResolveService_BlankFieldInTheFile_FallsBackToTheCatalog()
    {
        var dir = CreateAppHostDirectory(
            BlankUrlCatalog,
            """{ "services": { "blankurl": { "source": "url", "url": { "url": "" } } } }""");

        var builder = CreateBuilder(dir);

        var (metadata, config) = ServiceSourcesConfigCache.ResolveService(builder, "blankurl");

        Assert.Null(config.Url.Url);

        // Absent rather than empty is what lets the catalog through: `config.Url.Url ?? metadata…`
        // does not fall through an empty string, so before this the service failed as "no url
        // configured" while the catalog had one all along.
        Assert.Equal(
            "https://from-catalog.example.com/",
            UrlSource.ResolveUrl("blankurl", metadata, config).ToString());
    }

    /// <remarks>
    /// The file's own root key is the one typo left silent after every key <em>inside</em> an entry
    /// became an error: only the <c>services</c> subtree crosses into the AppHost's configuration,
    /// so a misspelled root contributes nothing and the failure arrives as "nothing is configured"
    /// — a description of an empty file, handed to a developer looking at a populated one.
    ///
    /// Reported as a near miss rather than by rejecting root keys the file does not recognize: the
    /// file is entitled to carry keys of its own (pinned by
    /// <see cref="ReadFrom_FileCarriesOtherTopLevelKeys_LeavesThemOutOfTheAppHostConfiguration"/>),
    /// so an unknown root key is not distinguishable from a typo by validity — only by resemblance.
    /// </remarks>
    [Theory]
    [InlineData("service")]
    [InlineData("serivces")]
    public void ResolveService_RootKeyOfTheFileIsMisspelled_SaysSoAndNamesBothSpellings(string rootKey)
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            $$"""{ "{{rootKey}}": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains($"'{rootKey}'", ex.Message);
        Assert.Contains("'services'", ex.Message);
        Assert.Contains(Path.Combine(dir, "servicesources.local.json"), ex.Message);
    }

    /// <remarks>
    /// Configuration keys are case-insensitive, so a root key differing from <c>services</c> only
    /// by case is not a near miss but the key itself, and the file loads. Pinned because the near
    /// miss above folds case to find its candidates, and folding without excluding an exact fold
    /// would report the file that works as the file that is misspelled.
    /// </remarks>
    [Fact]
    public void ResolveService_RootKeyOfTheFileDiffersFromServicesOnlyByCase_LoadsTheEntries()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "Services": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("local", config.Source);
    }

    /// <remarks>
    /// A root key the file is entitled to carry is not a typo, and saying "did you mean services?"
    /// of one would be advice to rename a key that belongs to something else.
    /// </remarks>
    [Fact]
    public void ResolveService_FileCarriesOnlyAnUnrelatedRootKey_ReportsNothingConfiguredWithoutASuggestion()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "ConnectionStrings": { "db": "Server=somewhere" } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("empty in every configuration source", ex.Message);
        Assert.DoesNotContain("ConnectionStrings", ex.Message);
    }

    /// <remarks>
    /// A file carrying both is not a typo: whatever the second key is for, the entries under
    /// <c>services</c> are being read, so there is nothing to correct.
    /// </remarks>
    [Fact]
    public void ResolveService_FileCarriesServicesAndANearMiss_LoadsTheEntriesAndSaysNothing()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """
            {
              "services": { "orders": { "source": "local" } },
              "service": { "orders": { "source": "url" } }
            }
            """);

        var builder = CreateBuilder(dir);

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("local", config.Source);
    }

    /// <remarks>
    /// The same rule where <c>services</c> is present but empty. The near miss explains an empty
    /// section and nothing else, and an empty <c>services</c> is a file whose root key is right and
    /// whose entries are missing — a different mistake, already reported as such.
    /// </remarks>
    [Fact]
    public void ResolveService_ServicesIsEmptyAndANearMissCarriesTheEntries_ReportsNothingConfiguredWithoutASuggestion()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """
            {
              "services": { },
              "service": { "orders": { "source": "local" } }
            }
            """);

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("empty in every configuration source", ex.Message);
        Assert.DoesNotContain("'service'", ex.Message);
    }

    /// <remarks>
    /// Two candidates in one file, which the message has to choose between. The closest one wins,
    /// and an exact tie is broken by the key itself, so the message names the same key on every run
    /// rather than whichever the configuration provider happened to enumerate first.
    /// </remarks>
    [Fact]
    public void ResolveService_FileCarriesTwoMisspellingsOfServices_NamesTheClosestOne()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """
            {
              "serivces": { "orders": { "source": "url" } },
              "service": { "orders": { "source": "local" } }
            }
            """);

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("'service'", ex.Message);
        Assert.DoesNotContain("'serivces'", ex.Message);
    }
}
