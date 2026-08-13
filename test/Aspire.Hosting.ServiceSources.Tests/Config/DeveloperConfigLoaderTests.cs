using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

public class DeveloperConfigLoaderTests
{
    [Fact]
    public void Load_ParsesServicesFromJson()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {
              "cacheDirectory": "~/.servicesources/repos",
              "services": {
                "orders": { "source": "local" },
                "payments": { "source": "local", "path": "/home/dev/code/payments", "ref": "feature/new-checkout" }
              }
            }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Equal("~/.servicesources/repos", config.CacheDirectory);
            Assert.Equal(2, config.Services.Count);
            Assert.Equal("local", config.Services["orders"].Source);
            Assert.Null(config.Services["orders"].Path);
            Assert.Equal("/home/dev/code/payments", config.Services["payments"].Path);
            Assert.Equal("feature/new-checkout", config.Services["payments"].Ref);
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
            () => DeveloperConfigLoader.Load("/no/such/servicesources.local.json"));

        Assert.Contains("/no/such/servicesources.local.json", ex.Message);
    }

    [Fact]
    public void Load_ParsesClusterFieldsFromJson()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {
              "services": {
                "orders": { "source": "cluster", "context": "dev-west", "namespace": "orders", "port": 8080 }
              }
            }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Equal("cluster", config.Services["orders"].Source);
            Assert.Equal("dev-west", config.Services["orders"].Context);
            Assert.Equal("orders", config.Services["orders"].Namespace);
            Assert.Equal(8080, config.Services["orders"].Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ClusterFieldsOmitted_LeavesThemNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            { "services": { "orders": { "source": "local" } } }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Null(config.Services["orders"].Context);
            Assert.Null(config.Services["orders"].Namespace);
            Assert.Null(config.Services["orders"].Port);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
