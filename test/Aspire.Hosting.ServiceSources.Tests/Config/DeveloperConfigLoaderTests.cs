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
    public void Load_ParsesKubernetesFieldsFromJson()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {
              "services": {
                "orders": { "source": "kubernetes", "context": "dev-west", "namespace": "orders", "port": 8080 }
              }
            }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Equal("kubernetes", config.Services["orders"].Source);
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
    public void Load_KubernetesFieldsOmitted_LeavesThemNull()
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

    [Fact]
    public void Load_ParsesTagFromJson()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {
              "services": {
                "orders": { "source": "container", "tag": "v1.4.2" }
              }
            }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Equal("container", config.Services["orders"].Source);
            Assert.Equal("v1.4.2", config.Services["orders"].Tag);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TagOmitted_LeavesItNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            { "services": { "orders": { "source": "local" } } }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Null(config.Services["orders"].Tag);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
