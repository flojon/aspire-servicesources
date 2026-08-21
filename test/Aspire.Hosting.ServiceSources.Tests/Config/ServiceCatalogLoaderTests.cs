using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

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
            var catalog = ServiceCatalogLoader.Load(path);

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
            var catalog = ServiceCatalogLoader.Load(path);

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
            var catalog = ServiceCatalogLoader.Load(path);

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
            var catalog = ServiceCatalogLoader.Load(path);

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
            var catalog = ServiceCatalogLoader.Load(path);

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
            var catalog = ServiceCatalogLoader.Load(path);

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
            var catalog = ServiceCatalogLoader.Load(path);
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
            var catalog = ServiceCatalogLoader.Load(path);

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
            var catalog = ServiceCatalogLoader.Load(path);

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
            var orders = ServiceCatalogLoader.Load(path).Services["orders"];

            Assert.Equal("main", orders.DefaultRef);
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

    [Fact]
    public void Load_KindNamedAfterATypedBlock_DoesNotValidateItAgainstThatBlocksSchema()
    {
        // LocalKindRegistry.Register makes this unreachable for a registered kind; the loader must
        // still not reject the block's own keys against ContainerMetadata's schema, so that the
        // failure the user sees is the accurate "kind 'container' is not registered".
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
            var catalog = ServiceCatalogLoader.Load(path);

            var frontend = catalog.Services["frontend"];
            Assert.Equal("container", frontend.Kind);
            Assert.NotNull(frontend.KindConfig);
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
}
