using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class UrlSourceTests
{
    private const string ServiceName = "orders";

    private static ServiceMetadata Metadata(string? url = "https://orders.example.com") =>
        new()
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Url = url is null ? null : new UrlMetadata { Url = url },
        };

    private static ServiceDeveloperConfig DevConfig(string? urlOverride = null) =>
        new() { Source = "url", Url = new() { Url = urlOverride } };

    [Fact]
    public void ResolveUrl_NoOverride_FallsBackToMetadataUrl()
    {
        var uri = UrlSource.ResolveUrl(ServiceName, Metadata(url: "https://orders.example.com"), DevConfig());

        Assert.Equal("https://orders.example.com/", uri.ToString());
    }

    [Fact]
    public void ResolveUrl_OverrideSet_TakesPrecedenceOverMetadata()
    {
        var uri = UrlSource.ResolveUrl(
            ServiceName, Metadata(url: "https://orders.example.com"), DevConfig(urlOverride: "https://orders.dev.internal"));

        Assert.Equal("https://orders.dev.internal/", uri.ToString());
    }

    [Fact]
    public void ResolveUrl_NeitherSet_ThrowsNamingServiceAndUrl()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            UrlSource.ResolveUrl(ServiceName, Metadata(url: null), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("url", ex.Message);
    }

    [Fact]
    public void ResolveUrl_NotAbsolute_ThrowsNamingServiceAndUrl()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            UrlSource.ResolveUrl(ServiceName, Metadata(), DevConfig(urlOverride: "not-a-url")));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("url", ex.Message);
    }

    [Fact]
    public void ResolveUrl_NonHttpScheme_ThrowsNamingServiceAndScheme()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            UrlSource.ResolveUrl(ServiceName, Metadata(), DevConfig(urlOverride: "ftp://orders.example.com")));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("http", ex.Message);
    }
}
