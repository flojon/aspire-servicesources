using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class SourceRequirementsTests
{
    private static ServiceDefinition Definition(
        string? path = null, string? url = null, string? image = null, int? containerPort = null,
        string? containerScheme = null, string? kubernetesService = null, int? kubernetesPort = null,
        string? kubernetesScheme = null, string? repository = "https://github.com/company/orders") =>
        new ServiceMetadata
        {
            Repository = repository ?? "",
            Project = "Orders.csproj",
            Path = path,
            Url = url is null ? null : new UrlMetadata { Url = url },
            Container = image is null ? null : new ContainerMetadata { Image = image, Port = containerPort, Scheme = containerScheme },
            Kubernetes = kubernetesService is null
                ? null
                : new KubernetesMetadata { Service = kubernetesService, Port = kubernetesPort, Scheme = kubernetesScheme },
        }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static SourceInput Input(ServiceDefinition definition, ServiceDeveloperConfig? config = null, string appHost = "apphost") =>
        new("orders", definition, config ?? new ServiceDeveloperConfig(), appHost);

    private static (string Key, bool Required)[] Keys(SourceRequirements requirements) =>
        [.. requirements.Fields.Select(f => (f.Key, f.Required))];

    [Fact]
    public void Disabled_RequirementsHaveNoFields_AndResolve()
    {
        var requirements = new DisabledSource().Requirements(Input(Definition()));

        Assert.Empty(requirements.Fields);
        Assert.True(requirements.Resolves);
    }

    [Fact]
    public void Disabled_IsNotASwitchRemedy() =>
        Assert.False(new DisabledSource().IsSwitchRemedy);

    [Fact]
    public void Url_BlankCatalogUrl_AsksUrlField_AndTheProblemIsTheMissingUrlSetting()
    {
        var requirements = new UrlSource().Requirements(Input(Definition()));

        Assert.Equal([("url:url", true)], Keys(requirements));
        Assert.Equal("url:url", requirements.Problem?.MissingSetting);
        Assert.True(requirements.Offered);
        Assert.False(requirements.Resolves);
    }

    [Fact]
    public void Url_CatalogUrlPresent_NoFields_Resolves()
    {
        var requirements = new UrlSource().Requirements(Input(Definition(url: "https://orders.example.com")));

        Assert.Empty(requirements.Fields);
        Assert.True(requirements.Resolves);
    }

    [Fact]
    public void Url_DeveloperUrlOnly_Resolves()
    {
        var config = new ServiceDeveloperConfig { Url = new UrlDeveloperConfig { Url = "http://localhost:5000" } };

        Assert.True(new UrlSource().Requirements(Input(Definition(), config)).Resolves);
    }

    [Fact]
    public void Url_FtpCatalogUrl_ProblemIsUntagged_NotOffered()
    {
        var requirements = new UrlSource().Requirements(Input(Definition(url: "ftp://orders.example.com")));

        Assert.NotNull(requirements.Problem);
        Assert.Null(requirements.Problem.MissingSetting);
        Assert.False(requirements.Offered);
    }

    [Fact]
    public void Url_ProblemMessage_EqualsWhatResolveThrows_AndRedactsCredentials()
    {
        var definition = Definition(url: "redis://user:pw@cache:6379");

        var problem = new UrlSource().Requirements(Input(definition)).Problem;
        var thrown = Assert.Throws<ServiceSourcesConfigurationException>(
            () => UrlSource.ResolveUrl("orders", definition, new ServiceDeveloperConfig()));

        Assert.NotNull(problem);
        Assert.Equal(thrown.Message, problem.Message);
        Assert.DoesNotContain("pw", problem.Message);
    }

    [Fact]
    public void Url_IsASwitchRemedy() =>
        Assert.True(new UrlSource().IsSwitchRemedy);
}
