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

    /// <remarks>
    /// Two names one edit apart, which is what makes an entry resembling one of them ambiguous
    /// enough for the reverse check to matter.
    /// </remarks>
    private const string NeighbouringCartCatalog = """
        services:
          cart:
            repository: https://github.com/company/cart
            project: src/Cart.Api/Cart.Api.csproj
          carts:
            repository: https://github.com/company/carts
            project: src/Carts.Api/Carts.Api.csproj
        """;

    private const string CartCatalog = """
        services:
          cart:
            repository: https://github.com/company/cart
            project: src/Cart.Api/Cart.Api.csproj
        """;

    /// <remarks>
    /// Its own service and its own catalog, because the test that uses it sets a process-global
    /// environment variable — see the remark on the first of those in this class.
    /// </remarks>
    private const string NearMissEnvCatalog = """
        services:
          nearmissenv:
            repository: https://github.com/company/nearmissenv
            project: src/NearMissEnv/NearMissEnv.csproj
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
    /// Only the <c>services</c> subtree crosses into the AppHost's configuration, so a misspelled
    /// root key contributes nothing at all: without this the failure is "nothing is configured" —
    /// a description of an empty file, handed to a developer looking at a populated one.
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

    /// <summary>
    /// A misspelled root key is still named when another configuration layer has configured a
    /// service, so the file's entries are unread while the AppHost is otherwise working.
    /// </summary>
    /// <remarks>
    /// The near miss used to be read only when <em>nothing</em> was configured anywhere, which is the
    /// merged view across every layer — so one environment variable pinning one service hid the fact
    /// that the developer's whole file was going unread. What they got instead was the per-service
    /// error, which tells them to add an entry to that same file: advice that cannot work, since
    /// nothing in it is being read.
    /// <para>
    /// The same gating bug as the backing-service side of #206, in the section that has shipped.
    /// </para>
    /// </remarks>
    [Fact]
    public void ResolveService_RootKeyMisspelledButAnotherLayerConfiguresAService_StillNamesTheRootKey()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "serivces": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceSources:Services:billing:Source"] = "url",
        });

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("'serivces'", ex.Message);
        Assert.Contains("'services'", ex.Message);
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

    /// <summary>
    /// An empty <c>services</c> beside a near miss that carries the entries names the near miss.
    /// </summary>
    /// <remarks>
    /// <b>This reverses a decision, so the reasoning is worth keeping.</b> It used to report
    /// "nothing configured" and deliberately withhold the suggestion, on the grounds that an empty
    /// <c>services</c> is a file whose root key is right and whose entries are missing — a different
    /// mistake, already reported as such. That holds for a file carrying <c>services</c> alone. It
    /// does not hold for this one: the entries are right there under <c>service</c>, and a message
    /// saying the file "configures no services" while a populated near miss sits beside the empty
    /// section describes the file as emptier than it is.
    /// <para>
    /// The rule that replaces it is narrower rather than looser, because a key is now "present" only
    /// when something is written under it — on both sides of the comparison. So a suggestion is
    /// offered exactly when the correct key configures nothing <em>and</em> a resembling key
    /// configures something, and two empty sections still say nothing.
    /// </para>
    /// <para>
    /// It could not be left alone in any case: the same presence test decides the backing-service
    /// root key, where <c>"backingServices": { }</c> beside a misspelled key holding the real
    /// entries silently disabled the whole of #206's root-key half.
    /// </para>
    /// </remarks>
    [Fact]
    public void ResolveService_ServicesIsEmptyAndANearMissCarriesTheEntries_NamesTheNearMiss()
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

        Assert.Contains("'service'", ex.Message);
        Assert.Contains("'services'", ex.Message);
    }

    /// <summary>
    /// A misspelled root key is named before its entries have any values in them.
    /// </summary>
    /// <remarks>
    /// Candidates are drawn from every root key the file mentions, while the key being looked for
    /// has to <em>configure</em> something to count as present. Collapsing those two into one list
    /// gets one of them wrong whichever way it collapses: reading both as "mentions" put the
    /// backing-service blind spot back, and reading both as "configures" lost the suggestion here —
    /// a developer part-way through writing entries under a key they misspelled, which is exactly
    /// when the hint is worth most.
    /// </remarks>
    [Theory]
    [InlineData("""{ "service": { "orders": { } } }""")]
    [InlineData("""{ "service": { } }""")]
    [InlineData("""{ "services": { }, "service": { "orders": { } } }""")]
    public void ResolveService_MisspelledRootKeyWithNoValuesUnderIt_IsStillNamed(string json)
    {
        var dir = CreateAppHostDirectory(OrdersCatalog, json);

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("'service'", ex.Message);
        Assert.Contains("'services'", ex.Message);
    }

    /// <summary>
    /// A file with nothing resembling <c>services</c> still reports configuring nothing, with no
    /// suggestion to chase.
    /// </summary>
    [Fact]
    public void ResolveService_ServicesIsEmptyAndNothingResemblesIt_ReportsNothingConfigured()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """
            {
              "services": { },
              "myOwnSettings": { "anything": "at all" }
            }
            """);

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("empty in every configuration source", ex.Message);
        Assert.DoesNotContain("myOwnSettings", ex.Message);
    }

    /// <remarks>
    /// Two candidates in one file, which the message has to choose between. <c>service</c> is one
    /// edit away — a dropped letter — and <c>servcs</c> is two, so the nearer one wins.
    /// </remarks>
    [Fact]
    public void ResolveService_FileCarriesTwoMisspellingsOfServices_NamesTheClosestOne()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """
            {
              "servcs": { "orders": { "source": "url" } },
              "service": { "orders": { "source": "local" } }
            }
            """);

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("'service'", ex.Message);
        Assert.DoesNotContain("'servcs'", ex.Message);
    }

    /// <summary>
    /// Candidates the same distance away are separated by the key itself, so the message names the
    /// same one on every run rather than whichever the configuration provider enumerated first.
    /// </summary>
    /// <remarks>
    /// These two used to be one and two edits apart, which is what the test above was written
    /// around. Charging a swapped pair one edit rather than two — the change that lets a transposed
    /// <c>path</c> or <c>port</c> be recognised at all — makes <c>serivces</c> a one-edit
    /// misspelling too, so the pair became a genuine tie and the tie-break is what decides it.
    /// Ordinally <c>serivces</c> comes first, its <c>i</c> against the other's <c>v</c>.
    /// </remarks>
    [Fact]
    public void ResolveService_FileCarriesTwoEquallyCloseMisspellings_NamesTheSameOneEveryRun()
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

        Assert.Contains("'serivces'", ex.Message);
    }

    /// <summary>
    /// A misspelled <em>service name</em> is the near miss one level down from the file's root key:
    /// the entry is valid, correctly shaped and read, and nothing matches it to the service the
    /// AppHost asked for.
    /// </summary>
    /// <remarks>
    /// The advice the message already gives works — writing the entry a second time under the right
    /// spelling does configure the service — so what is added is the sentence that says the fix is
    /// one character in an entry the file already has.
    /// </remarks>
    [Fact]
    public void ResolveService_ConfiguredNameIsOneEditFromTheService_NamesTheEntryAndAsksWhichWasMeant()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "order": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains(
            "Note that 'order' is configured and reaches no service in 'servicesources.yaml'. "
            + "Did you mean 'orders'? If so, rename that entry rather than adding a second one.",
            ex.Message);
    }

    /// <summary>
    /// An entry naming a service the catalog declares is that service's entry, whatever else it
    /// resembles.
    /// </summary>
    /// <remarks>
    /// This is the reverse check the suggestion cannot do without. Resemblance alone would read a
    /// working entry for a neighbouring service as a misspelling of this one and advise renaming it,
    /// which breaks the service it belongs to. Only an entry the catalog cannot account for is a
    /// candidate.
    /// </remarks>
    [Fact]
    public void ResolveService_ResemblingEntryNamesAnotherCatalogService_IsNotOfferedAsAMisspelling()
    {
        var dir = CreateAppHostDirectory(
            """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
              order:
                repository: https://github.com/company/order
                project: src/Order.Api/Order.Api.csproj
            """,
            """{ "services": { "order": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.DoesNotContain("Did you mean", ex.Message);
    }

    /// <summary>
    /// An entry for a service this AppHost does not add is not a typo, and is left alone.
    /// </summary>
    /// <remarks>
    /// The legitimate shape the whole check has to stay silent about: a developer switching between
    /// two AppHosts out of one file, or keeping an entry for a service they have stopped adding, has
    /// done nothing wrong. Only resemblance separates that from a misspelling.
    /// </remarks>
    [Fact]
    public void ResolveService_NoConfiguredNameResemblesTheService_SaysNothing()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "billing": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.DoesNotContain("Did you mean", ex.Message);
    }

    /// <summary>
    /// The service's own entry, left without a source, is never offered as a misspelling of itself.
    /// </summary>
    /// <remarks>
    /// The other way into this message: the entry is there and reaches the service, and what is
    /// missing is the <c>source</c> key inside it. Its name is at distance zero, so a check drawing
    /// candidates from every configured name would answer "did you mean 'orders'?" to a developer
    /// who wrote exactly that. What keeps it quiet here is that the entry names a service the
    /// catalog declares, so it is not a candidate at all — <see cref="NearMiss.MisspellingOf"/>
    /// would also drop it, which is why this passes either way and why the restriction it is
    /// really about is pinned by
    /// <see cref="ResolveService_ResemblingEntryNamesAnotherCatalogService_IsNotOfferedAsAMisspelling"/>
    /// rather than by this one. Kept because the shape is
    /// the one a reader worries about, and a test that says so is cheaper than the worry.
    /// </remarks>
    [Fact]
    public void ResolveService_ServicesOwnEntryHasNoSource_IsNotOfferedAsAMisspellingOfItself()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "orders": { "local": { "path": "/tmp/orders" } } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.DoesNotContain("Did you mean", ex.Message);
    }

    /// <summary>
    /// Two entries the same distance away are separated by their own spelling, so the message names
    /// the same one on every run rather than whichever the configuration provider enumerated first.
    /// </summary>
    /// <remarks>
    /// <c>orderr</c> is a substitution away from <c>orders</c> and <c>ordrs</c> a dropped letter, so
    /// the two tie at one edit. Ordinally <c>orderr</c> comes first: they agree on <c>ord</c> and
    /// then it has <c>e</c> where the other has <c>r</c>.
    /// <para>
    /// The tie-break itself is pinned at the unit level, and has to be: configuration sorts a
    /// section's children by its own key comparer, so these two arrive here already in the order
    /// the answer wants and this would pass with the sort deleted. What it does pin is the answer a
    /// developer with two candidates actually reads, which is worth having end to end.
    /// </para>
    /// </remarks>
    [Fact]
    public void ResolveService_TwoEquallyCloseConfiguredNames_NamesTheSameOneEveryRun()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """
            {
              "services": {
                "ordrs": { "source": "url" },
                "orderr": { "source": "local" }
              }
            }
            """);

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("'orderr'", ex.Message);
        Assert.DoesNotContain("'ordrs'", ex.Message);
    }

    /// <summary>
    /// A short service name gets one edit of tolerance, and a transposition is one edit.
    /// </summary>
    [Fact]
    public void ResolveService_ShortServiceNameWithATransposedEntry_IsNamed()
    {
        var dir = CreateAppHostDirectory(
            CartCatalog,
            """{ "services": { "crat": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "cart"));

        Assert.Contains("'crat'", ex.Message);
        Assert.Contains("Did you mean 'cart'?", ex.Message);
    }

    /// <summary>
    /// An entry contributed by a layer other than the file is named the same way, and the note does
    /// not claim the file carries it.
    /// </summary>
    /// <remarks>
    /// These are merged configuration keys, so the file is where a developer normally writes one
    /// rather than where one must have come from — which is why the note is phrased against the
    /// configuration and names no path of its own. A message asserting the file carried this entry
    /// would be false here, and there is nothing in the value to tell the two apart by.
    /// <para>
    /// Environment variables are process-global and xunit runs test classes in parallel, so the
    /// service this names must be one no other test uses — otherwise the variable is still set
    /// while another class builds its own AppHost and silently configures its service.
    /// </para>
    /// </remarks>
    [Fact]
    public void ResolveService_MisspelledNameComesFromAnotherLayer_IsStillNamedAndTheFileIsNotBlamed()
    {
        var dir = CreateAppHostDirectory(NearMissEnvCatalog);

        Environment.SetEnvironmentVariable("ServiceSources__Services__nearmisenv__Source", "local");
        try
        {
            var builder = CreateBuilder(dir);

            var ex = Assert.Throws<ServiceSourcesConfigurationException>(
                () => ServiceSourcesConfigCache.ResolveService(builder, "nearmissenv"));

            Assert.Contains(
                "Note that 'nearmisenv' is configured and reaches no service in "
                + "'servicesources.yaml'. Did you mean 'nearmissenv'? If so, rename that entry "
                + "rather than adding a second one.",
                ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceSources__Services__nearmisenv__Source", null);
        }
    }

    /// <summary>
    /// An entry closer to a different catalog service is left for that service to name, even when
    /// it is inside the failing service's tolerance.
    /// </summary>
    /// <remarks>
    /// The half of the check that stops the note costing a developer their configuration. Both
    /// <c>cart</c> and <c>carts</c> are declared, and <c>crat</c> — a transposition of the first —
    /// is one edit from it and two from the second, so a failing <c>carts</c> asked only "which
    /// entry resembles me?" is told to rename the entry that configures <c>cart</c>. Following that
    /// leaves <c>cart</c> unconfigured, and its own failure then carries no suggestion at all,
    /// because <c>carts</c> is declared and the renamed entry is no longer a candidate.
    /// </remarks>
    [Fact]
    public void ResolveService_EntryIsCloserToAnotherCatalogService_IsLeftForThatServiceToName()
    {
        var dir = CreateAppHostDirectory(
            NeighbouringCartCatalog,
            """{ "services": { "crat": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "carts"));

        Assert.DoesNotContain("Did you mean", ex.Message);
    }

    /// <summary>
    /// The same entry, asked about by the service it is actually closest to, is named.
    /// </summary>
    /// <remarks>
    /// The other side of the test above, and what makes it a redirection rather than a silence: the
    /// suggestion is not withheld, it is made when the service it belongs to is the one asking.
    /// </remarks>
    [Fact]
    public void ResolveService_EntryIsClosestToTheFailingService_IsNamedEvenWithANeighbourDeclared()
    {
        var dir = CreateAppHostDirectory(
            NeighbouringCartCatalog,
            """{ "services": { "crat": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "cart"));

        Assert.Contains("'crat'", ex.Message);
        Assert.Contains("Did you mean 'cart'?", ex.Message);
    }

    /// <summary>
    /// When the service already has an entry of its own, the note says where the source belongs
    /// instead of telling the reader to rename onto a name the file already uses.
    /// </summary>
    /// <remarks>
    /// The second route into this error: the entry is there and its <c>source</c> is missing, and a
    /// stale near miss sits beside it carrying one. "Rename that entry" is a wrong instruction here
    /// — it asks for a second <c>"orders"</c> key in the same object, which the JSON provider
    /// refuses to load at all once the two collide on a leaf, leaving the AppHost failing to start
    /// over an error that never mentions this package.
    /// </remarks>
    [Fact]
    public void ResolveService_ServiceHasItsOwnSourcelessEntryBesideANearMiss_SaysWhereTheSourceBelongs()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """
            {
              "services": {
                "orders": { "local": { "path": "/tmp/orders" } },
                "order": { "source": "local", "local": { "path": "/tmp/order" } }
              }
            }
            """);

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains(
            "Did you mean 'orders'? An entry for 'orders' is there already, so the source belongs "
            + "on that one rather than on 'order'.",
            ex.Message);
        Assert.DoesNotContain("rename", ex.Message);
    }

    /// <summary>
    /// A file whose root key is misspelled, beside a near miss contributed by another layer, gets
    /// both notes — the root key first.
    /// </summary>
    /// <remarks>
    /// The only case where the order of the two is observable, and it is the order that reads: the
    /// root-key note ends by saying whatever is configured is coming from another layer, which is
    /// the provenance the second note deliberately does not state. Reversed, the reader meets
    /// "'ordrs' is configured" with nowhere to look and learns afterwards that the file they would
    /// have looked in is dead.
    /// <para>
    /// The near miss has to come from a layer other than the file, and here does: a misspelled root
    /// key is exactly the condition under which the file's own service entries never bind.
    /// </para>
    /// </remarks>
    [Fact]
    public void ResolveService_MisspelledRootKeyAndAMisspelledNameFromAnotherLayer_NamesTheRootKeyFirst()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "serivces": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceSources:Services:ordrs:Source"] = "url",
        });

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("Did you mean 'services'?", ex.Message);
        Assert.Contains("Did you mean 'orders'?", ex.Message);
        Assert.True(
            ex.Message.IndexOf("Did you mean 'services'?", StringComparison.Ordinal)
            < ex.Message.IndexOf("Did you mean 'orders'?", StringComparison.Ordinal),
            $"The root-key note should come first, but the message read: {ex.Message}");
    }

    /// <summary>
    /// The tolerance is the service name's, not the entry's.
    /// </summary>
    /// <remarks>
    /// <c>carted</c> is two edits from <c>cart</c> and six letters long, so scaling the tolerance by
    /// the entry would admit it. The catalog is the fixed vocabulary here and <c>cart</c> is four
    /// letters, which allows one edit — and two edits from a four-letter name reaches far enough
    /// that the suggestion would be a guess.
    /// </remarks>
    [Fact]
    public void ResolveService_LongerEntryTwoEditsFromAShortService_IsNotOfferedAsAMisspelling()
    {
        var dir = CreateAppHostDirectory(
            CartCatalog,
            """{ "services": { "carted": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "cart"));

        Assert.DoesNotContain("Did you mean", ex.Message);
    }
}
