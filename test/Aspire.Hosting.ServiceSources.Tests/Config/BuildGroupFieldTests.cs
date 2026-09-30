using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

[Trait("IO", "true")]
public class BuildGroupFieldTests
{
    private static ServiceSourcesConfigurationException? LoadExpectingFailure(string buildGroupLine)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, $"services:\n  orders:\n    repository: https://github.com/company/orders\n    project: src/Api.csproj\n    {buildGroupLine}\n");

        try
        {
            return Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ServiceDefinition LoadDefinition(string yaml)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, yaml);

        try
        {
            var (catalog, _) = ServiceCatalogLoader.Load(path);
            return catalog.Services["orders"].ToDefinition(path, "orders", TestHelpers.EmptyRepositories);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_BuildGroup_ReachesTheDefinition()
    {
        var definition = LoadDefinition("""
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Api.csproj
                buildGroup: web
            """);

        Assert.Equal("web", definition.BuildGroup);
    }

    [Fact]
    public void Load_NoBuildGroup_LeavesItNull()
    {
        var definition = LoadDefinition("""
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Api.csproj
            """);

        Assert.Null(definition.BuildGroup);
    }

    [Theory]
    [InlineData("buildGroup: \"\"")]
    [InlineData("buildGroup: \"   \"")]
    [InlineData("buildGroup: \" web\"")]
    [InlineData("buildGroup: \"web \"")]
    public void Load_MalformedBuildGroup_ThrowsNamingTheService(string line)
    {
        var ex = LoadExpectingFailure(line);

        Assert.Contains("orders", ex!.Message, StringComparison.Ordinal);
        Assert.Contains("buildGroup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ValidBuildGroupOnAServiceThatResolvesToAnotherSource_IsAccepted()
    {
        var definition = LoadDefinition("""
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Api.csproj
                defaultSource: repository
                buildGroup: web
            """);

        Assert.Equal("web", definition.BuildGroup);
    }

    [Fact]
    public void Load_MalformedBuildGroupOnAServiceThatResolvesToAnotherSource_IsStillRefused()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "services:\n  orders:\n    repository: https://github.com/company/orders\n    project: src/Api.csproj\n    defaultSource: repository\n    buildGroup: \" \"\n");

        try
        {
            Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ServiceKindNamedBuildGroup_IsRejectedAsAReservedName()
    {
        Assert.True(ServiceCatalogLoader.IsReservedKindName("buildGroup"));
    }

    [Fact]
    public void WithBuildGroup_SetsIt()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders").WithBuildGroup("web").Build();

        Assert.Equal("web", definition.BuildGroup);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" web")]
    [InlineData("web ")]
    public void WithBuildGroup_MalformedValue_Throws(string value)
    {
        var chain = new ServiceCatalogBuilder().AddService("orders");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithBuildGroup(value));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithBuildGroup_CalledTwice_Throws()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders").WithBuildGroup("web");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithBuildGroup("other"));

        Assert.Contains("WithBuildGroup", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithBuildGroup_NotCalled_LeavesItNull()
    {
        Assert.Null(new ServiceCatalogBuilder().AddService("orders").Build().BuildGroup);
    }
}