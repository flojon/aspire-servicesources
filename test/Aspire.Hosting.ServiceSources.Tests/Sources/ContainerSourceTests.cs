using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class ContainerSourceTests
{
    private const string ServiceName = "orders";

    private static ServiceMetadata Metadata(string? image = "ghcr.io/company/orders", int? port = 8080, string? defaultTag = null) =>
        new()
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Container = image is null ? null : new ContainerMetadata { Image = image, Port = port, DefaultTag = defaultTag },
        };

    private static ServiceDeveloperConfig DevConfig(string? tag = null) =>
        new() { Source = "container", Tag = tag };

    [Fact]
    public void ResolveContainerConfig_NoTagAnywhere_ReturnsNullTag()
    {
        var (image, tag, port) = ContainerSource.ResolveContainerConfig(
            ServiceName, Metadata(image: "ghcr.io/company/orders", port: 8080, defaultTag: null), DevConfig(tag: null));

        Assert.Equal("ghcr.io/company/orders", image);
        Assert.Null(tag);
        Assert.Equal(8080, port);
    }

    [Fact]
    public void ResolveContainerConfig_LocalTagOverride_TakesPrecedenceOverCatalogDefaultTag()
    {
        var (_, tag, _) = ContainerSource.ResolveContainerConfig(
            ServiceName, Metadata(defaultTag: "latest"), DevConfig(tag: "v1.4.2"));

        Assert.Equal("v1.4.2", tag);
    }

    [Fact]
    public void ResolveContainerConfig_LocalTagUnset_FallsBackToCatalogDefaultTag()
    {
        var (_, tag, _) = ContainerSource.ResolveContainerConfig(
            ServiceName, Metadata(defaultTag: "latest"), DevConfig(tag: null));

        Assert.Equal("latest", tag);
    }

    [Fact]
    public void ResolveContainerConfig_NoContainerBlock_ThrowsNamingServiceAndImage()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ContainerSource.ResolveContainerConfig(ServiceName, Metadata(image: null), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("container.image", ex.Message);
    }

    [Fact]
    public void ResolveContainerConfig_EmptyImage_ThrowsNamingServiceAndImage()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ContainerSource.ResolveContainerConfig(ServiceName, Metadata(image: ""), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("container.image", ex.Message);
    }

    [Fact]
    public void ResolveContainerConfig_WhitespaceImage_ThrowsNamingServiceAndImage()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ContainerSource.ResolveContainerConfig(ServiceName, Metadata(image: "   "), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("container.image", ex.Message);
    }

    [Fact]
    public void ResolveContainerConfig_MissingPort_ThrowsNamingServiceAndPort()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ContainerSource.ResolveContainerConfig(ServiceName, Metadata(port: null), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("container.port", ex.Message);
    }
}
