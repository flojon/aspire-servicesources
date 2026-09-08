using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class EndpointSchemeTests
{
    private const string ServiceName = "orders";

    private static readonly CatalogOrigin YamlOrigin = CatalogOrigin.FromYaml("servicesources.yaml");

    [Fact]
    public void Resolve_NoSchemeAnywhere_DefaultsToHttp()
    {
        var scheme = EndpointScheme.Resolve(
            ServiceName, "kubernetes", developerScheme: null, catalogScheme: null, YamlOrigin);

        Assert.Equal("http", scheme);
    }

    [Fact]
    public void Resolve_CatalogScheme_IsUsed()
    {
        var scheme = EndpointScheme.Resolve(
            ServiceName, "kubernetes", developerScheme: null, catalogScheme: "https", YamlOrigin);

        Assert.Equal("https", scheme);
    }

    [Fact]
    public void Resolve_DeveloperScheme_TakesPrecedenceOverCatalogScheme()
    {
        var scheme = EndpointScheme.Resolve(
            ServiceName, "kubernetes", developerScheme: "https", catalogScheme: "http", YamlOrigin);

        Assert.Equal("https", scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankDeveloperScheme_FallsBackToCatalogScheme(string developerScheme)
    {
        var scheme = EndpointScheme.Resolve(
            ServiceName, "kubernetes", developerScheme, catalogScheme: "https", YamlOrigin);

        Assert.Equal("https", scheme);
    }

    [Theory]
    [InlineData("HTTPS")]
    [InlineData("Https")]
    [InlineData(" https ")]
    public void Resolve_SchemeInAnyCasingOrPadding_NormalizesToLowercase(string configured)
    {
        var scheme = EndpointScheme.Resolve(
            ServiceName, "kubernetes", developerScheme: null, catalogScheme: configured, YamlOrigin);

        Assert.Equal("https", scheme);
    }

    [Fact]
    public void Resolve_UnsupportedCatalogScheme_ThrowsNamingServiceSchemeAndCatalogFile()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            EndpointScheme.Resolve(ServiceName, "kubernetes", developerScheme: null, catalogScheme: "grpc", YamlOrigin));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("grpc", ex.Message);
        Assert.Contains("kubernetes.scheme", ex.Message);
        Assert.Contains("servicesources.yaml", ex.Message);
    }

    [Fact]
    public void Resolve_UnsupportedCatalogScheme_CodeOrigin_DoesNotNameYamlFile()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            EndpointScheme.Resolve(
                ServiceName, "kubernetes", developerScheme: null, catalogScheme: "grpc", CatalogOrigin.Code));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("grpc", ex.Message);
        Assert.Contains("kubernetes.scheme", ex.Message);
        Assert.DoesNotContain("servicesources.yaml", ex.Message);
        Assert.Contains("AddServiceCatalog", ex.Message);
    }

    [Fact]
    public void Resolve_UnsupportedDeveloperScheme_ThrowsNamingTheDeveloperFileInstead()
    {
        // The two origins are reported separately because they are fixed in different files, and a
        // developer override is the one the person seeing the error can actually change.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            EndpointScheme.Resolve(ServiceName, "kubernetes", developerScheme: "ftp", catalogScheme: "https", YamlOrigin));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("ftp", ex.Message);
        Assert.Contains("servicesources.local.json", ex.Message);
        Assert.DoesNotContain("servicesources.yaml", ex.Message);
    }
}
