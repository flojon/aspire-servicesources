using Aspire.Hosting.ServiceSources.Catalog;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class ServiceDefinitionBuilderTests
{
    [Fact]
    public void WithRepository_SetsRepositoryProjectAndDefaultRef()
    {
        var builder = new ServiceCatalogBuilder();
        var definition = builder.AddService("orders")
            .WithRepository("https://github.com/example/repo", project: "src/Api.csproj", defaultRef: "main")
            .Build();

        Assert.Equal("https://github.com/example/repo", definition.Repository);
        Assert.Equal("src/Api.csproj", definition.Project);
        Assert.Equal("main", definition.DefaultRef);
    }

    [Fact]
    public void WithRepository_ProjectOmitted_DefaultsToEmpty()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo")
            .Build();

        Assert.Equal("", definition.Project);
    }

    [Fact]
    public void WithRepository_CalledTwice_ThrowsNamingServiceAndBlock()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithRepository("https://github.com/example/other"));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithRepository", ex.Message, StringComparison.Ordinal);
    }
}
