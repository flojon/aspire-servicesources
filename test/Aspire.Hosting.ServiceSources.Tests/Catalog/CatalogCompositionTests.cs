using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Catalog;
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

    /// <summary>
    /// The ungrouped-collision warning (design question 5) fires when two ungrouped services really
    /// do share a repository URL and both actually resolve through the "local" source — the case its
    /// own text describes ("are all 'local' ... each gets its own checkout").
    /// </summary>
    [Fact]
    public async Task TwoUngroupedServicesShareUrlAndBothResolveLocally_WarnsToGroupThem()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://example.com/monorepo.git
                project: src/Orders.Api/Orders.Api.csproj
              billing:
                repository: https://example.com/monorepo.git
                project: src/Billing.Api/Billing.Api.csproj
            """);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "local" }, "billing": { "source": "local" } } }
            """);
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        ServiceSourcesConfigCache.ResolveService(builder, "orders");
        ServiceSourcesConfigCache.ResolveService(builder, "billing");

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Contains(warnings, w => w.Contains("are all 'local'", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two services can share a repository URL in the catalog while neither of them is ever actually
    /// cloned — README "Combining sources on one catalog entry" lets one entry carry a
    /// <c>repository:</c> block alongside a <c>kubernetes:</c>/<c>url:</c>/<c>container:</c> one, with
    /// <c>servicesources.local.json</c> picking which applies. The ungrouped-collision warning must
    /// not fire for that shape: its advice ("share one checkout ... AddRepository/WithSharedRepository")
    /// describes work that never happens when nothing resolves through "local" at all.
    /// </summary>
    [Fact]
    public async Task TwoServicesShareUrlButResolveThroughKubernetes_NoUngroupedCollisionWarning()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://example.com/monorepo.git
                project: src/Orders.Api/Orders.Api.csproj
                kubernetes:
                  service: orders-svc
                  port: 8080
              billing:
                repository: https://example.com/monorepo.git
                project: src/Billing.Api/Billing.Api.csproj
                kubernetes:
                  service: billing-svc
                  port: 8080
            """);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "kubernetes" }, "billing": { "source": "kubernetes" } } }
            """);
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        ServiceSourcesConfigCache.ResolveService(builder, "orders");
        ServiceSourcesConfigCache.ResolveService(builder, "billing");

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.DoesNotContain(warnings, w => w.Contains("are all 'local'", StringComparison.Ordinal));
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

    /// <summary>
    /// "One namespace, checked once" (design #291): a repository's own name is also the checkout
    /// directory an ungrouped service's own name would claim, so the two must never collide even
    /// though they are declared through entirely different keys (<c>repositories:</c> against
    /// <c>services:</c>).
    /// </summary>
    [Fact]
    public void RepositoryName_CollidesWithUngroupedServiceName_ThrowsNamingBoth()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            repositories:
              orders:
                repository: https://example.com/orders-group.git
            services:
              orders:
                repository: https://example.com/orders-solo.git
                project: Orders.csproj
            """);
        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("'orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ungrouped service", ex.Message, StringComparison.Ordinal);
        Assert.Contains("repository", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The repository-vs-repository case check is reachable from two yaml entries in the same file,
    /// not only from a code/yaml pair — yaml's own <c>Repositories</c> dictionary is Ordinal, so
    /// 'Monorepo:'/'monorepo:' both survive <c>ServiceCatalogLoader.Load</c> and land here as two
    /// distinct keys. The message must attribute the collision to the actual yaml file, not always
    /// to "code (AddServiceCatalog)" — a same-file collision has no code side at all.
    /// </summary>
    [Fact]
    public void RepositoryName_DiffersOnlyByCaseWithinTheSameYamlFile_AttributesBothToYaml()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var yamlPath = Path.Combine(dir, "servicesources.yaml");
        File.WriteAllText(yamlPath,
            """
            repositories:
              Monorepo:
                repository: https://example.com/repo-one.git
              monorepo:
                repository: https://example.com/repo-two.git
            services:
              orders:
                repositoryRef: Monorepo
                project: Orders.csproj
            """);
        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("'monorepo'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Monorepo'", ex.Message, StringComparison.Ordinal);
        Assert.Contains(yamlPath, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AddServiceCatalog", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Case-only, unlike <see cref="RepositoryName_CollidesWithUngroupedServiceName_ThrowsNamingBoth"/>:
    /// the default filesystem on Windows and macOS does not distinguish 'checkouts/Orders' from
    /// 'checkouts/orders', so the two would still fight over the same directory there even though
    /// the names are not byte-identical.
    /// </summary>
    [Fact]
    public void RepositoryName_DiffersOnlyByCaseFromUngroupedServiceName_ThrowsNamingBoth()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            repositories:
              Orders:
                repository: https://example.com/orders-group.git
            services:
              orders:
                repository: https://example.com/orders-solo.git
                project: Orders.csproj
            """);
        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("'orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("case", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RepositoryName_DeclaredInBothCatalogs_ThrowsDuplicateError()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var yamlPath = Path.Combine(dir, "servicesources.yaml");
        File.WriteAllText(yamlPath,
            """
            repositories:
              shared:
                repository: https://example.com/yaml-repo.git
            services:
              billing:
                repositoryRef: shared
                project: Billing.csproj
            """);
        var builder = CreateBuilder(dir);
        builder.AddServiceCatalog(c =>
        {
            var repository = c.AddRepository("https://example.com/code-repo.git", name: "shared");
            c.AddService("orders").WithSharedRepository(repository).WithProject("Orders.csproj");
        });

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("'shared'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("twice", ex.Message, StringComparison.Ordinal);
        Assert.Contains(yamlPath, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Case-only, across the two catalogs — the same rule
    /// <see cref="RepositoryName_DeclaredInBothCatalogs_ThrowsDuplicateError"/> checks for a
    /// byte-identical name, extended to a name that only differs by case on a filesystem that does
    /// not distinguish the two directories.
    /// </summary>
    [Fact]
    public void RepositoryName_DiffersOnlyByCaseAcrossCatalogs_ThrowsNamingBoth()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var yamlPath = Path.Combine(dir, "servicesources.yaml");
        File.WriteAllText(yamlPath,
            """
            repositories:
              Shared:
                repository: https://example.com/yaml-repo.git
            services:
              billing:
                repositoryRef: Shared
                project: Billing.csproj
            """);
        var builder = CreateBuilder(dir);
        builder.AddServiceCatalog(c =>
        {
            var repository = c.AddRepository("https://example.com/code-repo.git", name: "shared");
            c.AddService("orders").WithSharedRepository(repository).WithProject("Orders.csproj");
        });

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("'shared'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'Shared'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("case", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A regression guard, not new production code (Task 1/2 already keep the two catalogs'
    /// repository maps separate through <see cref="ServiceSourcesConfigCache.LoadedConfig.Load"/>):
    /// a code service's <c>WithSharedRepository</c> takes a <see cref="RepositoryBuilder"/>
    /// reference rather than a name, so it has no way to reach a yaml <c>repositories:</c> entry —
    /// and the two catalogs' own repositories, named differently here, resolve to entirely
    /// independent instances rather than being unified by anything about how they were declared.
    /// </summary>
    [Fact]
    public void CodeService_CannotReachYamlRepositoryRef()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            repositories:
              yaml-shared:
                repository: https://example.com/yaml-repo.git
            services:
              billing:
                repositoryRef: yaml-shared
                project: Billing.csproj
            """);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """
            { "services": {
                "orders": { "source": "local" },
                "payments": { "source": "local" },
                "billing": { "source": "local" }
            } }
            """);
        var builder = CreateBuilder(dir);
        builder.AddServiceCatalog(c =>
        {
            var repository = c.AddRepository("https://example.com/code-repo.git", name: "code-shared");
            c.AddService("orders").WithSharedRepository(repository).WithProject("Orders.csproj");
            c.AddService("payments").WithSharedRepository(repository).WithProject("Payments.csproj");
        });

        var (ordersDef, _) = ServiceSourcesConfigCache.ResolveService(builder, "orders");
        var (paymentsDef, _) = ServiceSourcesConfigCache.ResolveService(builder, "payments");
        var (billingDef, _) = ServiceSourcesConfigCache.ResolveService(builder, "billing");

        // The code catalog's own two services share one repository instance...
        Assert.Same(ordersDef.Repository, paymentsDef.Repository);
        // ...and the yaml catalog's is a completely separate one, unreachable from code.
        Assert.NotSame(ordersDef.Repository, billingDef.Repository);
        Assert.Equal("code-shared", ordersDef.Repository.CheckoutName);
        Assert.Equal("yaml-shared", billingDef.Repository.CheckoutName);
    }

    /// <summary>
    /// Design question 5, settled as "warn": two ungrouped services declaring the identical
    /// repository URL each pay for their own checkout, which is exactly what grouping them would
    /// avoid — worth naming, not worth refusing, since an existing catalog with this shape must keep
    /// working.
    /// </summary>
    [Fact]
    public async Task TwoUngroupedServices_OneUpstream_WarnsSuggestingGrouping()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              orders:
                repository: https://example.com/monorepo.git
                project: Orders.csproj
              billing:
                repository: https://example.com/monorepo.git
                project: Billing.csproj
            """);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "orders": { "source": "local" }, "billing": { "source": "local" } } }""");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);

        // Any resolution reaches LoadedConfig.Load, where the notice is buffered.
        ServiceSourcesConfigCache.ResolveService(builder, "orders");

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var warning = Assert.Single(warnings);
        Assert.Contains("'orders'", warning, StringComparison.Ordinal);
        Assert.Contains("'billing'", warning, StringComparison.Ordinal);
        Assert.Contains("monorepo.git", warning, StringComparison.Ordinal);
    }
}
