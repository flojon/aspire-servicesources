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
    public void Load_ParsesClusterBlockFromYaml()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                cluster:
                  service: orders-svc
                  port: 8080
            """);

        try
        {
            var catalog = ServiceCatalogLoader.Load(path);

            var orders = Assert.Single(catalog.Services);
            Assert.NotNull(orders.Value.Cluster);
            Assert.Equal("orders-svc", orders.Value.Cluster.Service);
            Assert.Equal(8080, orders.Value.Cluster.Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_NoClusterBlock_LeavesClusterNull()
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

            Assert.Null(catalog.Services["orders"].Cluster);
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
}
