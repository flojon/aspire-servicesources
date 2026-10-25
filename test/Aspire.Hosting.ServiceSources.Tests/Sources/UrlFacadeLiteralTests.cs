using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class UrlFacadeLiteralTests
{
    private static EndpointAnnotation Endpoint(string scheme, string host, int port) =>
        new(ProtocolType.Tcp, uriScheme: scheme, name: scheme, transport: "http", port: port, targetPort: port)
        {
            TargetHost = host,
            IsProxied = false,
        };

    [Fact]
    public void Url_OmitsDefaultHttpsPort() =>
        Assert.Equal("https://httpbin.org", UrlFacadeLiteral.Literal(Endpoint("https", "httpbin.org", 443)));

    [Fact]
    public void Url_OmitsDefaultHttpPort() =>
        Assert.Equal("http://httpbin.org", UrlFacadeLiteral.Literal(Endpoint("http", "httpbin.org", 80)));

    [Fact]
    public void Url_KeepsNonDefaultPort() =>
        Assert.Equal("https://httpbin.org:8443", UrlFacadeLiteral.Literal(Endpoint("https", "httpbin.org", 8443)));

    [Fact]
    public void Url_KeepsAnHttpPortThatIsTheHttpsDefault() =>
        Assert.Equal("http://httpbin.org:443", UrlFacadeLiteral.Literal(Endpoint("http", "httpbin.org", 443)));

    [Fact]
    public void Property_Scheme_IsUriScheme() =>
        Assert.Equal("https", Property(EndpointProperty.Scheme));

    [Theory]
    [InlineData(EndpointProperty.Host)]
    [InlineData(EndpointProperty.IPV4Host)]
    public void Property_Host_And_IPV4Host_AreTargetHost(EndpointProperty property) =>
        Assert.Equal("httpbin.org", Property(property));

    [Theory]
    [InlineData(EndpointProperty.Port)]
    [InlineData(EndpointProperty.TargetPort)]
    public void Property_Port_And_TargetPort_AreThePort(EndpointProperty property) =>
        Assert.Equal("443", Property(property));

    [Fact]
    public void Property_HostAndPort_AlwaysKeepsThePort() =>
        Assert.Equal("httpbin.org:443", Property(EndpointProperty.HostAndPort));

    [Fact]
    public void Property_Url_EqualsTheLiteral() =>
        Assert.Equal("https://httpbin.org", Property(EndpointProperty.Url));

    [Fact]
    public void Property_Unknown_ReturnsNull() =>
        Assert.Null(Property((EndpointProperty)999));

    private static string? Property(EndpointProperty property) =>
        UrlFacadeLiteral.TryProperty(Endpoint("https", "httpbin.org", 443), property, out var value) ? value : null;
}
