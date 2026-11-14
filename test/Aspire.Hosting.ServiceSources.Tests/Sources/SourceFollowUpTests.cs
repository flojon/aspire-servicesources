using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class SourceFollowUpTests
{
    private static ServiceDefinition Definition(string? path = null, string? url = null, int? kubernetesPort = null) =>
        new ServiceMetadata
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Path = path,
            Url = url is null ? null : new UrlMetadata { Url = url },
            Kubernetes = new KubernetesMetadata { Service = "orders", Port = kubernetesPort },
        }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static (string Key, bool Required)[] Keys(string source, ServiceDefinition definition) =>
        [.. SourceFollowUp.FieldsFor(source, definition).Select(f => (f.Key, f.Required))];

    [Fact]
    public void Kubernetes_WithoutCatalogPort_AsksContextPortNamespaceScheme() =>
        Assert.Equal(
            [("kubernetes:context", true), ("kubernetes:port", true), ("kubernetes:namespace", false), ("kubernetes:scheme", false)],
            Keys("kubernetes", Definition()));

    [Fact]
    public void Kubernetes_WithCatalogPort_DoesNotAskPort() =>
        Assert.Equal(
            [("kubernetes:context", true), ("kubernetes:namespace", false), ("kubernetes:scheme", false)],
            Keys("kubernetes", Definition(kubernetesPort: 80)));

    [Fact]
    public void Kubernetes_PortIsNumberInput_OthersText()
    {
        var fields = SourceFollowUp.FieldsFor("kubernetes", Definition());

        Assert.Equal(InputType.Number, fields.Single(f => f.Key == "kubernetes:port").InputType);
        Assert.All(fields.Where(f => f.Key != "kubernetes:port"), f => Assert.Equal(InputType.Text, f.InputType));
    }

    [Fact]
    public void Path_WithoutCatalogPath_AsksPath() =>
        Assert.Equal([("path:path", true)], Keys("path", Definition()));

    [Fact]
    public void Path_WithCatalogPath_AsksNothing() =>
        Assert.Empty(SourceFollowUp.FieldsFor("path", Definition(path: "orders")));

    [Fact]
    public void Path_WithWhitespaceCatalogPath_AsksPath() =>
        Assert.Equal([("path:path", true)], Keys("path", Definition(path: "  ")));

    [Fact]
    public void Url_WithoutCatalogUrl_AsksUrl() =>
        Assert.Equal([("url:url", true)], Keys("url", Definition()));

    [Fact]
    public void Url_WithCatalogUrl_AsksNothing() =>
        Assert.Empty(SourceFollowUp.FieldsFor("url", Definition(url: "https://orders.example.com")));

    [Fact]
    public void Url_WithWhitespaceCatalogUrl_AsksUrl() =>
        Assert.Equal([("url:url", true)], Keys("url", Definition(url: " ")));

    [Theory]
    [InlineData("repository")]
    [InlineData("container")]
    [InlineData("disabled")]
    [InlineData("unknown")]
    public void SourcesNeedingNothing_AskNothing(string source) =>
        Assert.Empty(SourceFollowUp.FieldsFor(source, Definition()));

    [Fact]
    public void Labels_AreNonEmpty() =>
        Assert.All(
            new[] { "kubernetes", "path", "url" }.SelectMany(s => SourceFollowUp.FieldsFor(s, Definition())),
            f => Assert.False(string.IsNullOrWhiteSpace(f.Label)));
}
