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
}
