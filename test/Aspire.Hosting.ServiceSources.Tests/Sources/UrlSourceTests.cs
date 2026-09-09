using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class UrlSourceTests
{
    private const string ServiceName = "orders";

    private static ServiceDefinition Definition(string? url = "https://orders.example.com") =>
        new ServiceMetadata
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Url = url is null ? null : new UrlMetadata { Url = url },
        }.ToDefinition("servicesources.yaml", ServiceName);

    private static ServiceDeveloperConfig DevConfig(string? urlOverride = null) =>
        new() { Source = "url", Url = new() { Url = urlOverride } };

    [Fact]
    public void ResolveUrl_NoOverride_FallsBackToMetadataUrl()
    {
        var uri = UrlSource.ResolveUrl(ServiceName, Definition(url: "https://orders.example.com"), DevConfig());

        Assert.Equal("https://orders.example.com/", uri.ToString());
    }

    [Fact]
    public void ResolveUrl_OverrideSet_TakesPrecedenceOverMetadata()
    {
        var uri = UrlSource.ResolveUrl(
            ServiceName, Definition(url: "https://orders.example.com"), DevConfig(urlOverride: "https://orders.dev.internal"));

        Assert.Equal("https://orders.dev.internal/", uri.ToString());
    }

    [Fact]
    public void ResolveUrl_NeitherSet_ThrowsNamingServiceAndUrl()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            UrlSource.ResolveUrl(ServiceName, Definition(url: null), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("url", ex.Message);
    }

    [Fact]
    public void ResolveUrl_NotAbsolute_ThrowsNamingServiceAndUrl()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            UrlSource.ResolveUrl(ServiceName, Definition(), DevConfig(urlOverride: "not-a-url")));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("url", ex.Message);
    }

    [Fact]
    public void ResolveUrl_NonHttpScheme_ThrowsNamingServiceAndScheme()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            UrlSource.ResolveUrl(ServiceName, Definition(), DevConfig(urlOverride: "ftp://orders.example.com")));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("http", ex.Message);
    }

    /// <summary>
    /// Neither refusal echoes the credentials the URL was carrying.
    /// </summary>
    /// <remarks>
    /// A URL is where userinfo lives, and both of these messages quote the value back — into
    /// <c>~/.aspire/logs</c>, and from there into whatever issue the failure is pasted into. The
    /// wrong-scheme refusal is the one to worry about: pointing a <c>"url"</c> service at a Redis or
    /// AMQP endpoint is an ordinary mistake, and those URLs carry credentials as a matter of course.
    /// </remarks>
    [Theory]
    // Wrong scheme, which is where a credential-bearing URL most plausibly arrives.
    [InlineData("redis://user:hunter2@cache:6379", "cache:6379")]
    [InlineData("ftp://user:hunter2@files.internal/x", "files.internal")]
    [InlineData("redis://:hunter2@cache:6379", "cache:6379")]
    // No '//' at all, so there is no authority to hang a userinfo on.
    [InlineData("mailto:user:hunter2@host", "url")]
    [InlineData("user:hunter2@host", "url")]
    // Unparseable, so there is no Uri to read the userinfo off — it has to come out of the text.
    [InlineData("https://user:hunter2@host:notaport/p", "notaport")]
    // The secret is the username, which is how a PAT is written. Nothing may survive here.
    [InlineData("//hunter2:x-oauth-basic@cache:6379", "url")]
    [InlineData("hunter2@host", "url")]
    // Carried by a query rather than by a userinfo, which is how a Redis or Mongo URL writes one.
    [InlineData("redis://cache:6379?password=hunter2", "cache:6379")]
    [InlineData("mongodb://host:27017/db?authSource=admin&password=hunter2", "host:27017")]
    // A whole connection string pasted into the field, which is what makes the wrong-scheme
    // refusal the one that matters.
    [InlineData("Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKey=hunter2", "SharedAccessKey")]
    [InlineData("Server=db;Database=orders;User Id=sa;Password=hunter2;", "Database=orders")]
    // Nothing delimits the secret from the host but a comma.
    [InlineData("redis://cache:6379,hunter2", "cache:6379")]
    public void ResolveUrl_UrlCarryingCredentials_DoesNotEchoThem(string url, string survives)
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            UrlSource.ResolveUrl(ServiceName, Definition(), DevConfig(urlOverride: url)));

        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);

        // The echo still has to say which value was refused, or the developer cannot tell which of
        // their services this is about — except where nothing in the value was recognised at all, in
        // which case the rows above ask only that the message still mentions the 'url' field.
        Assert.Contains(survives, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A URL with nothing to hide is quoted exactly as the developer wrote it.
    /// </summary>
    [Theory]
    [InlineData("ftp://orders.example.com")]
    [InlineData("not-a-url")]
    [InlineData("redis://cache.internal:6379")]
    // A URL missing its scheme is the most ordinary mistake here, and the echo is how it is
    // diagnosed — so it must not be answered with '***'.
    [InlineData("orders.example.com")]
    [InlineData("htttps://orders.example.com")]
    public void ResolveUrl_UrlWithNoCredentials_IsEchoedWhole(string url)
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            UrlSource.ResolveUrl(ServiceName, Definition(), DevConfig(urlOverride: url)));

        Assert.Contains($"'{url}'", ex.Message, StringComparison.Ordinal);
    }
}
