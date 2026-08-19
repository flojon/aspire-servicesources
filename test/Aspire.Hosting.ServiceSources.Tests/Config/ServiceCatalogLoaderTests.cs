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
}
