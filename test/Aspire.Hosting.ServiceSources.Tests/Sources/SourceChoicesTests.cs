using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class SourceChoicesTests
{
    private static ServiceDefinition Definition(
        string? repository = "https://github.com/company/orders", bool container = true, bool kubernetes = true,
        string? image = "ghcr.io/company/orders", int? port = 8080, string? url = null, string? kubernetesScheme = null) =>
        new ServiceMetadata
        {
            Repository = repository ?? "",
            Project = "Orders.csproj",
            Container = container ? new ContainerMetadata { Image = image!, Port = port } : null,
            Url = url is null ? null : new UrlMetadata { Url = url },
            Kubernetes = kubernetes ? new KubernetesMetadata { Service = "orders", Port = 80, Scheme = kubernetesScheme } : null,
        }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    [Fact]
    public void All_EqualsTheSourceDictionaryKeys() =>
        Assert.Equal(
            new[] { "container", "disabled", "kubernetes", "path", "repository", "url" },
            ServiceSourcesBuilderExtensions.Sources.Keys.Order(StringComparer.Ordinal));

    [Fact]
    public void For_DefinitionWithoutContainerBlock_OmitsContainer() =>
        Assert.DoesNotContain("container", SourceChoices.For(Definition(container: false)));

    [Fact]
    public void For_WithoutRepository_OmitsRepository() =>
        Assert.DoesNotContain("repository", SourceChoices.For(Definition(repository: null)));

    [Fact]
    public void For_WithoutKubernetesService_OmitsKubernetes() =>
        Assert.DoesNotContain("kubernetes", SourceChoices.For(Definition(kubernetes: false)));

    [Fact]
    public void For_AnyDefinition_AlwaysOffersUrlPathDisabled()
    {
        var choices = SourceChoices.For(Definition(repository: null, container: false, kubernetes: false));

        Assert.Equal(["url", "path", "disabled"], choices);
    }

    [Fact]
    public void For_FullyDeclaredDefinition_OffersAllSix() =>
        Assert.Equal(
            ["repository", "container", "url", "kubernetes", "path", "disabled"],
            SourceChoices.For(Definition()));

    [Theory]
    [InlineData(null, 8080)]
    [InlineData("", 8080)]
    [InlineData("  ", 8080)]
    [InlineData("ghcr.io/company/orders", null)]
    [InlineData("ghcr.io/company/orders", 0)]
    [InlineData("ghcr.io/company/orders", 70000)]
    public void For_InvalidContainerBlock_OmitsContainer(string? image, int? port) =>
        Assert.DoesNotContain("container", SourceChoices.For(Definition(image: image, port: port)));

    [Theory]
    [InlineData(1)]
    [InlineData(65535)]
    public void For_ValidContainerPort_OffersContainer(int port) =>
        Assert.Contains("container", SourceChoices.For(Definition(port: port)));

    [Fact]
    public void DisplayOrder_CoversEverySource() =>
        Assert.Equal(
            ServiceSourcesBuilderExtensions.Sources.Keys.Order(StringComparer.Ordinal),
            SourceChoices.DisplayOrder.Order(StringComparer.Ordinal));

    [Fact]
    public void For_UrlWithFtpCatalogScheme_OmitsUrl() =>
        Assert.DoesNotContain("url", SourceChoices.For(Definition(url: "ftp://orders.example.com")));

    [Fact]
    public void For_UrlWithHttpsCatalogUrl_OffersUrl() =>
        Assert.Contains("url", SourceChoices.For(Definition(url: "https://orders.example.com")));

    [Fact]
    public void For_KubernetesWithUnsupportedCatalogScheme_OmitsKubernetes() =>
        Assert.DoesNotContain("kubernetes", SourceChoices.For(Definition(kubernetesScheme: "ftp")));

    private sealed class ThrowingSource : IServiceSource
    {
        public bool IsSwitchRemedy => false;

        public SourceRequirements Requirements(SourceInput input) => throw new UriFormatException("not a uri");

        public IResourceBuilder<ServiceResource> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
            ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void For_WhenASourceThrowsUnexpectedly_OmitsItAndStillOffersTheRest()
    {
        var choices = SourceChoices.For(
            Definition(),
            name => name == "container" ? new ThrowingSource() : ServiceSourcesBuilderExtensions.Sources[name]);

        Assert.DoesNotContain("container", choices);
        Assert.Contains("url", choices);
        Assert.Contains("kubernetes", choices);
    }
}
