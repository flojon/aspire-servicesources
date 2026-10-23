using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class SourceChoicesTests
{
    private static ServiceDefinition Definition(
        string? repository = "https://github.com/company/orders", bool container = true, bool kubernetes = true) =>
        new ServiceMetadata
        {
            Repository = repository,
            Project = "Orders.csproj",
            Container = container ? new ContainerMetadata { Image = "ghcr.io/company/orders", Port = 8080 } : null,
            Kubernetes = kubernetes ? new KubernetesMetadata { Service = "orders", Port = 80 } : null,
        }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    [Fact]
    public void All_EqualsTheSourceDictionaryKeys() =>
        Assert.Equal(
            new[] { "container", "disabled", "kubernetes", "path", "repository", "url" },
            SourceChoices.All().Order(StringComparer.Ordinal));

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
}
