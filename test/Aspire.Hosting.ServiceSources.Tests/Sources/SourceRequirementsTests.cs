using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;
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

    [Theory]
    [InlineData(null, 8080)]
    [InlineData("ghcr.io/company/orders", null)]
    [InlineData("ghcr.io/company/orders", 70000)]
    public void Container_InvalidCatalogBlock_NotOffered_AndMessageIsResolveContainerConfigs(string? image, int? port)
    {
        var definition = Definition(image: image ?? " ", containerPort: port);

        var requirements = new ContainerSource().Requirements(Input(definition));
        var thrown = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ContainerSource.ResolveContainerConfig("orders", definition, new ServiceDeveloperConfig()));

        Assert.False(requirements.Offered);
        Assert.Equal(thrown.Message, requirements.Problem?.Message);
    }

    [Fact]
    public void Container_NoContainerBlock_NotOffered() =>
        Assert.False(new ContainerSource().Requirements(Input(Definition())).Offered);

    [Fact]
    public void Container_ImageAndPort_ResolvesWithNoFields()
    {
        var requirements = new ContainerSource().Requirements(Input(Definition(image: "ghcr.io/company/orders", containerPort: 8080)));

        Assert.Empty(requirements.Fields);
        Assert.True(requirements.Resolves);
    }

    [Fact]
    public void Container_UnsupportedCatalogScheme_NotOffered()
    {
        var requirements = new ContainerSource().Requirements(
            Input(Definition(image: "ghcr.io/company/orders", containerPort: 8080, containerScheme: "ftp")));

        Assert.False(requirements.Resolves);
        Assert.False(requirements.Offered);
    }

    [Fact]
    public void Container_IsASwitchRemedy() =>
        Assert.True(new ContainerSource().IsSwitchRemedy);

    private sealed class CountingAllocator : IPortAllocator
    {
        public int Calls { get; private set; }

        public bool IsAvailable(int candidate) => true;

        public int AllocatePort()
        {
            Calls++;
            return 54321;
        }

        public IReadOnlyList<int> AllocatePorts(int count) => throw new NotSupportedException();
    }

    private static KubernetesSource Kubernetes() => new(new CountingAllocator());

    private static ServiceDeveloperConfig KubernetesConfig(string? context = null, int? port = null) =>
        new() { Kubernetes = new() { Context = context, Port = port } };

    [Fact]
    public void Kubernetes_WithoutCatalogPort_AsksContextPortNamespaceScheme() =>
        Assert.Equal(
            [("kubernetes:context", true), ("kubernetes:port", true), ("kubernetes:namespace", false), ("kubernetes:scheme", false)],
            Keys(Kubernetes().Requirements(Input(Definition(kubernetesService: "orders")))));

    [Fact]
    public void Kubernetes_WithCatalogPort_DoesNotAskPort() =>
        Assert.Equal(
            [("kubernetes:context", true), ("kubernetes:namespace", false), ("kubernetes:scheme", false)],
            Keys(Kubernetes().Requirements(Input(Definition(kubernetesService: "orders", kubernetesPort: 80)))));

    [Fact]
    public void Kubernetes_PortIsNumberInput_OthersText()
    {
        var fields = Kubernetes().Requirements(Input(Definition(kubernetesService: "orders"))).Fields;

        Assert.Equal(InputType.Number, fields.Single(f => f.Key == "kubernetes:port").InputType);
        Assert.All(fields.Where(f => f.Key != "kubernetes:port"), f => Assert.Equal(InputType.Text, f.InputType));
    }

    [Fact]
    public void Kubernetes_EmptyConfig_ProblemIsMissingContext_Offered()
    {
        var requirements = Kubernetes().Requirements(Input(Definition(kubernetesService: "orders", kubernetesPort: 80)));

        Assert.Equal("kubernetes:context", requirements.Problem?.MissingSetting);
        Assert.True(requirements.Offered);
    }

    [Fact]
    public void Kubernetes_NoServiceName_ProblemIsNonAskable_NotOffered()
    {
        var requirements = Kubernetes().Requirements(Input(Definition()));

        Assert.Equal("kubernetes:service", requirements.Problem?.MissingSetting);
        Assert.False(requirements.Offered);
    }

    [Fact]
    public void Kubernetes_UnsupportedCatalogScheme_NotOffered()
    {
        var requirements = Kubernetes().Requirements(
            Input(Definition(kubernetesService: "orders", kubernetesPort: 80, kubernetesScheme: "ftp")));

        Assert.Null(requirements.Problem?.MissingSetting);
        Assert.False(requirements.Offered);
    }

    [Fact]
    public void Kubernetes_ContextAndPortGiven_Resolves() =>
        Assert.True(Kubernetes().Requirements(
            Input(Definition(kubernetesService: "orders"), KubernetesConfig("dev", 8080))).Resolves);

    [Fact]
    public void Kubernetes_PortOutOfRange_ProblemIsUntagged()
    {
        var requirements = Kubernetes().Requirements(
            Input(Definition(kubernetesService: "orders"), KubernetesConfig("dev", 70000)));

        Assert.NotNull(requirements.Problem);
        Assert.Null(requirements.Problem.MissingSetting);
    }

    [Fact]
    public void Kubernetes_Requirements_NeverAllocatesAPort()
    {
        var allocator = new CountingAllocator();

        new KubernetesSource(allocator).Requirements(
            Input(Definition(kubernetesService: "orders", kubernetesPort: 80), KubernetesConfig("dev")));

        Assert.Equal(0, allocator.Calls);
    }

    [Fact]
    public void Kubernetes_IsASwitchRemedy() =>
        Assert.True(Kubernetes().IsSwitchRemedy);
}
