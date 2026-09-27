using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

[Trait("IO", "true")]
public class ServiceCatalogLoaderTests
{
    [Fact]
    public void Load_ParsesServicesFromYaml()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                defaultRef: main
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            var orders = Assert.Single(catalog.Services);
            Assert.Equal("orders", orders.Key);
            Assert.Equal("https://github.com/company/orders", orders.Value.Repository);
            Assert.Equal("src/Orders.Api/Orders.Api.csproj", orders.Value.Project);
            Assert.Equal("main", orders.Value.DefaultRef);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ServiceWithDefaultSource_SetsTheField()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                defaultSource: repository
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            Assert.Equal("repository", catalog.Services["orders"].DefaultSource);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ServiceWithBlankDefaultSource_TreatedAsAbsent()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                defaultSource:
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            Assert.Null(catalog.Services["orders"].DefaultSource);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ServiceWithInvalidDefaultSource_ThrowsNamingTheFourValues()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                defaultSource: bogus
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
            Assert.Contains("bogus", ex.Message, StringComparison.Ordinal);
            Assert.Contains("repository", ex.Message, StringComparison.Ordinal);
            Assert.Contains("url", ex.Message, StringComparison.Ordinal);
            Assert.Contains("kubernetes", ex.Message, StringComparison.Ordinal);
            Assert.Contains("container", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// "local" is retired, not aliased: a catalog's <c>defaultSource: local</c> gets the same named
    /// migration a developer's own <c>source: "local"</c> does, rather than falling through to the
    /// generic "not a valid source" message <see cref="Load_ServiceWithInvalidDefaultSource_ThrowsNamingTheFourValues"/>
    /// covers.
    /// </summary>
    [Fact]
    public void Load_ServiceWithDefaultSourceLocal_ReportsTheRenameToRepository()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                defaultSource: local
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("Service 'orders'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("defaultSource", ex.Message, StringComparison.Ordinal);
            Assert.Contains("renamed", ex.Message, StringComparison.Ordinal);
            Assert.Contains("'repository'", ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("not a valid source", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ThrowsNamingPath()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceCatalogLoader.Load("/no/such/servicesources.yaml"));

        Assert.Contains("/no/such/servicesources.yaml", ex.Message);
    }

    [Fact]
    public void Load_ParsesKubernetesBlockFromYaml()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                kubernetes:
                  service: orders-svc
                  port: 8080
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            var orders = Assert.Single(catalog.Services);
            Assert.NotNull(orders.Value.Kubernetes);
            Assert.Equal("orders-svc", orders.Value.Kubernetes.Service);
            Assert.Equal(8080, orders.Value.Kubernetes.Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_NoKubernetesBlock_LeavesKubernetesNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            Assert.Null(catalog.Services["orders"].Kubernetes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ParsesContainerBlockFromYaml()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                container:
                  image: ghcr.io/company/orders
                  port: 8080
                  defaultTag: latest
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            var orders = Assert.Single(catalog.Services);
            Assert.NotNull(orders.Value.Container);
            Assert.Equal("ghcr.io/company/orders", orders.Value.Container.Image);
            Assert.Equal(8080, orders.Value.Container.Port);
            Assert.Equal("latest", orders.Value.Container.DefaultTag);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_NoContainerBlock_LeavesContainerNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            Assert.Null(catalog.Services["orders"].Container);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_NoKindSpecified_DefaultsToDotnet()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            Assert.Equal("dotnet", catalog.Services["orders"].Kind);
            Assert.Null(catalog.Services["orders"].KindConfig);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CustomKindWithMatchingBlock_CapturesKindAndRawBlock()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              frontend:
                repository: https://github.com/company/frontend
                kind: javascript
                javascript:
                  appDirectory: .
                  runScript: dev
                  packageManager: npm
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);
            var frontend = catalog.Services["frontend"];

            Assert.Equal("javascript", frontend.Kind);
            Assert.NotNull(frontend.KindConfig);
            var block = Assert.IsAssignableFrom<IDictionary<object, object>>(frontend.KindConfig);
            Assert.Equal(".", block["appDirectory"]);
            Assert.Equal("dev", block["runScript"]);
            Assert.Equal("npm", block["packageManager"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CustomKindWithoutMatchingBlock_LeavesKindConfigNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              frontend:
                repository: https://github.com/company/frontend
                kind: javascript
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            Assert.Equal("javascript", catalog.Services["frontend"].Kind);
            Assert.Null(catalog.Services["frontend"].KindConfig);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownTopLevelProperty_ThrowsNamingServiceAndProperty()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repositry: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message);
            Assert.Contains("repositry", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownPropertyInsideKubernetesBlock_ThrowsNamingServiceAndProperty()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                kubernetes:
                  servicee: orders-svc
                  port: 8080
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message);
            Assert.Contains("servicee", ex.Message);
            Assert.Contains("kubernetes", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownPropertyInsideUrlBlock_ThrowsNamingServiceAndProperty()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                url:
                  urll: https://orders.example.com
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message);
            Assert.Contains("urll", ex.Message);
            Assert.Contains("url", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownPropertyInsideContainerBlock_ThrowsNamingServiceAndProperty()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                container:
                  imagee: ghcr.io/company/orders
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message);
            Assert.Contains("imagee", ex.Message);
            Assert.Contains("container", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_EmptyKindValue_DefaultsToDotnet()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                kind:
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            Assert.Equal("dotnet", catalog.Services["orders"].Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ServiceEntryWithNoBody_ThrowsNamingService()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
            """);

        try
        {
            // YamlDotNet stores a null entry for a bodyless service key; report it by name rather
            // than dereferencing it while normalizing `kind`.
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_EveryKnownPropertyOnOneService_LoadsWithoutError()
    {
        // The unknown-property sets are derived from the metadata types by reflection; this guards
        // the derivation itself, so a property that the typed pass accepts can never be rejected
        // here as unknown.
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                defaultRef: main
                defaultSource: repository
                kind: dotnet
                kubernetes:
                  service: orders-svc
                  port: 8080
                url:
                  url: https://orders.example.com
                container:
                  image: ghcr.io/company/orders
                  port: 8080
                  defaultTag: latest
            """);

        try
        {
            var orders = ServiceCatalogLoader.Load(path).Catalog.Services["orders"];

            Assert.Equal("main", orders.DefaultRef);
            Assert.Equal("repository", orders.DefaultSource);
            Assert.Equal("orders-svc", orders.Kubernetes!.Service);
            Assert.Equal("https://orders.example.com", orders.Url!.Url);
            Assert.Equal("latest", orders.Container!.DefaultTag);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MisspelledRootKey_ThrowsNamingItInsteadOfYieldingAnEmptyCatalog()
    {
        // IgnoreUnmatchedProperties applies to the root too, so without the root check this parses
        // to an empty catalog and is reported much later as "service 'orders' was not found".
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            service:
              orders:
                repository: https://github.com/company/orders
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("service", ex.Message);
            Assert.Contains("services", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("container")]
    [InlineData("url")]
    [InlineData("kubernetes")]
    [InlineData("repository")]
    [InlineData("kind")]
    [InlineData("prepare")]
    [InlineData("project")]
    [InlineData("defaultRef")]
    public void Load_ServiceKindNamedAfterAWellKnownProperty_ThrowsStatingYamlCollision(string reserved)
    {
        // This service actually names 'reserved' as its kind from its own yaml entry, so the
        // collision this restriction exists for can occur — unlike a kind only ever reached via
        // WithKind in C#, which shares no document with these properties (#133).
        var path = Path.GetTempFileName();
        File.WriteAllText(path,
            "services:\n" +
            "  frontend:\n" +
            "    repository: https://github.com/company/frontend\n" +
            $"    kind: {reserved}\n");

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("frontend", ex.Message);
            Assert.Contains(reserved, ex.Message);
            // States the restriction is about this service's yaml entry, not a blanket "reserved"
            // claim about the kind name globally.
            Assert.Contains("servicesources.yaml", ex.Message);
            Assert.Contains("WithKind", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ServiceKindNamedAfterATypedBlockWithMatchingBlockPresent_StillThrowsStatingYamlCollision()
    {
        // The block-schema question this test used to guard ("don't validate 'container:'s keys
        // against ContainerMetadata") no longer arises: the reserved-name check now runs before the
        // block is ever inspected, for the same reason — the block can never bind as kind options
        // here regardless of what keys it contains.
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              frontend:
                repository: https://github.com/company/frontend
                kind: container
                container:
                  runScript: dev
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("frontend", ex.Message);
            Assert.Contains("container", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_YamlPresentButNoServiceUsesAReservedKind_LoadsWithoutError()
    {
        // Merely having a yaml catalog, or another service in it using an unrelated kind, must never
        // trigger the reserved-name check by itself — only a service that actually names the
        // reserved kind does (#133; this used to be an AppHost-wide gate in LocalKindRegistry.Register).
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              frontend:
                repository: https://github.com/company/frontend
                kind: widget
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            Assert.Equal("widget", catalog.Services["frontend"].Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_KindConfigProperty_IsRejectedAsUnknown()
    {
        // KindConfig is populated from the kind-matching block, never bound from yaml — so the
        // reflection-derived set must not start accepting it as a writable key.
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                kindConfig:
                  runScript: dev
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("kindConfig", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("services:\n")]
    [InlineData("services: {}\n")]
    public void Load_ServicesKeyWithNoServices_YieldsEmptyCatalog(string yaml)
    {
        // A bare 'services:' deserializes the map itself to null, overriding the property
        // initializer — it must still behave like the explicit empty mapping (and like an omitted
        // key) rather than faulting while the loader enumerates the catalog. A service that is
        // actually referenced is then reported by name by ServiceSourcesConfigCache.
        var path = Path.GetTempFileName();
        File.WriteAllText(path, yaml);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            Assert.Empty(catalog.Services);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("    kind: dotnet\n")]
    public void Load_StrayDotnetBlock_ThrowsNamingServiceAndProperty(string kindLine)
    {
        // 'dotnet' is resolved from the top-level repository/project metadata and never reads
        // KindConfig, and LocalKindRegistry.Register refuses to register it — so a 'dotnet:' block
        // is always stray or misspelled and must be rejected rather than captured and ignored.
        var path = Path.GetTempFileName();
        File.WriteAllText(path,
            "services:\n" +
            "  orders:\n" +
            "    repository: https://github.com/company/orders\n" +
            "    project: src/Orders.Api/Orders.Api.csproj\n" +
            kindLine +
            "    dotnet:\n" +
            "      runScript: dev\n");

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message);
            Assert.Contains("dotnet", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_DotnetService_LeavesKindConfigNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            Assert.Null(catalog.Services["orders"].KindConfig);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ParsesPrepareBlockFromYaml()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              routing:
                repository: https://github.com/example/routing
                project: Routing.csproj
                prepare:
                  command: ["./prepare.sh", "--full"]
                  windowsCommand: ["pwsh", "-File", "prepare.ps1"]
                  mode: once
            """);

        try
        {
            var prepare = ServiceCatalogLoader.Load(path).Catalog.Services["routing"].Prepare;

            Assert.NotNull(prepare);
            Assert.Equal<string[]>(["./prepare.sh", "--full"], prepare!.Command!);
            Assert.Equal<string[]>(["pwsh", "-File", "prepare.ps1"], prepare.WindowsCommand!);
            Assert.Equal("once", prepare.Mode);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_NoPrepareBlock_LeavesItAbsent()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
            """);

        try
        {
            Assert.Null(ServiceCatalogLoader.Load(path).Catalog.Services["orders"].Prepare);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The typo rejection the block gets for free by being a typed nested one, exactly as
    /// <c>kubernetes:</c> already does.
    /// </summary>
    [Fact]
    public void Load_UnknownPropertyInsidePrepare_ThrowsNamingExpectedSet()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              routing:
                repository: https://github.com/example/routing
                project: Routing.csproj
                prepare:
                  comand: ["./prepare.sh"]
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("unknown property 'comand' inside 'prepare'", ex.Message);
            Assert.Contains("command", ex.Message);
            Assert.Contains("windowsCommand", ex.Message);
            Assert.Contains("mode", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("repositories:\n")]
    [InlineData("repositories: {}\n")]
    public void Load_RepositoriesKeyWithNothingUnder_BindsToEmpty(string yaml)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, yaml);

        try
        {
            var (_, repositories) = ServiceCatalogLoader.Load(path);

            Assert.Empty(repositories);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownKeyInsideRepositoriesEntry_Throws()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            repositories:
              monorepo:
                repositry: https://github.com/company/monorepo
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("monorepo", ex.Message);
            Assert.Contains("repositry", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_UnknownKeyInsideRepositoryPrepareBlock_Throws()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            repositories:
              monorepo:
                repository: https://github.com/company/monorepo
                prepare:
                  comand: ["./prepare.sh"]
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("monorepo", ex.Message);
            Assert.Contains("unknown property 'comand' inside 'prepare'", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_RepositoryRefNamingNoRepositoriesEntry_ThrowsListingDeclaredNames()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            repositories:
              monorepo:
                repository: https://github.com/company/monorepo
            services:
              orders:
                repositoryRef: nonexistent
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message);
            Assert.Contains("nonexistent", ex.Message);
            Assert.Contains("monorepo", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_RepositoryRefNamingNoRepositoriesSectionAtAll_ThrowsWithoutADanglingList()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repositoryRef: nonexistent
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message);
            Assert.Contains("nonexistent", ex.Message);
            // "Expected one of: ." (no repositories: section, so nothing to list) reads as broken
            // output rather than as "there are none" — the message must say that in words instead.
            Assert.DoesNotContain("Expected one of:", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Design "The grouped name": every checkout-directory name goes through
    /// <c>LocalGitCheckout.IsContainedCheckoutDirectoryName</c>, the #224 traversal guard, whether it
    /// arrives explicit through <c>AddRepository</c>, or — here — as a yaml
    /// <c>repositories:</c> key. <c>ServiceCatalogBuilder.AddRepository</c> already refuses the
    /// equivalent unsafe name at composition time; this is the same check for the yaml authoring
    /// surface, so the two report the same class of mistake the same way rather than one of them
    /// falling through to a resolution-time failure deep inside whichever service resolves first.
    /// </summary>
    [Fact]
    public void Load_RepositoriesKeyIsNotAContainedDirectoryName_ThrowsAtLoadTime()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            repositories:
              ../evil:
                repository: https://github.com/company/monorepo
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("../evil", ex.Message);
            Assert.Contains("checkout directory name", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("repository: https://github.com/company/orders\n")]
    [InlineData("defaultRef: main\n")]
    public void Load_RepositoryOrDefaultRefBesideRepositoryRef_ThrowsNamingBothKeys(string conflictingLine)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path,
            "repositories:\n" +
            "  monorepo:\n" +
            "    repository: https://github.com/company/monorepo\n" +
            "services:\n" +
            "  orders:\n" +
            "    repositoryRef: monorepo\n" +
            "    " + conflictingLine);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message);
            Assert.Contains("repositoryRef", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_PrepareOnServiceCarryingRepositoryRef_ThrowsPointingAtRepositoriesEntry()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            repositories:
              monorepo:
                repository: https://github.com/company/monorepo
            services:
              orders:
                repositoryRef: monorepo
                prepare:
                  command: ["./prepare.sh"]
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message);
            Assert.Contains("repositoryRef", ex.Message);
            Assert.Contains("monorepo", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TwoServicesShareRepositoryRef_ToDefinitionReturnsTheSameRepositoryDefinitionInstance()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            repositories:
              monorepo:
                repository: https://github.com/company/monorepo
                defaultRef: main
            services:
              orders:
                repositoryRef: monorepo
                project: src/Orders.Api/Orders.Api.csproj
              payments:
                repositoryRef: monorepo
                project: src/Payments.Api/Payments.Api.csproj
            """);

        try
        {
            var (catalog, repositories) = ServiceCatalogLoader.Load(path);

            var ordersDefinition = catalog.Services["orders"].ToDefinition(path, "orders", repositories);
            var paymentsDefinition = catalog.Services["payments"].ToDefinition(path, "payments", repositories);

            Assert.Same(ordersDefinition.Repository, paymentsDefinition.Repository);
            Assert.Equal("monorepo", ordersDefinition.Repository.CheckoutName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ServiceHasNeitherRepositoryNorRepositoryRef_ThrowsNamingService()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(
                () => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
            Assert.Contains("repository", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ServiceHasEmptyRepositoryAndNoRepositoryRef_ThrowsNamingService()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: "   "
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(
                () => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
            Assert.Contains("repository", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The "no source configured" message's list of remedies grows to mention 'path' — design "New
    /// source: path", free consequence 2.
    /// </summary>
    [Fact]
    public void Load_ServiceHasNoResolvableFieldAtAll_MentionsPathAmongTheRemedies()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(
                () => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
            Assert.Contains("no source is configured", ex.Message, StringComparison.Ordinal);
            Assert.Contains("'path'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A bare 'path:' satisfies the "has a source" check by itself, the same way a bare 'url:' block
    /// does — no 'repository' is required alongside it.
    /// </summary>
    [Fact]
    public void Load_ServiceHasOnlyPath_DoesNotThrowNoSourceConfigured()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                path: services/orders
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            Assert.Equal("services/orders", catalog.Services["orders"].Path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Adding 'path' as a plain property on <see cref="ServiceMetadata"/> makes it a reserved kind
    /// name automatically, through the same reflection-derived mechanism 'repository'/'url' already
    /// collide with — design's free consequence 1, verified rather than assumed.
    /// </summary>
    [Fact]
    public void Load_ServiceKindNamedPath_IsRejectedAsAReservedName()
    {
        Assert.True(ServiceCatalogLoader.IsReservedKindName("path"));

        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                kind: path
            """);

        try
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(
                () => ServiceCatalogLoader.Load(path));

            Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
            Assert.Contains("kind 'path'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("collides", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// README "Combining sources on one catalog entry" (design finding 8): 'path:' joins
    /// 'repository:'/'url:'/'container:'/'kubernetes:' on one entry without any special-casing.
    /// </summary>
    [Fact]
    public void Load_PathCombinedWithRepositoryOnOneEntry_LoadsBothFields()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                path: services/orders
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);

            var orders = catalog.Services["orders"];
            Assert.Equal("https://github.com/company/orders", orders.Repository);
            Assert.Equal("services/orders", orders.Path);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
