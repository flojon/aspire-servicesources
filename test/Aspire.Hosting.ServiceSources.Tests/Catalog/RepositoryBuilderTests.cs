using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class RepositoryBuilderTests
{
    [Fact]
    public void AddRepository_DerivesNameFromUrl_StrippingDotGit()
    {
        var catalog = new ServiceCatalogBuilder();
        var repository = catalog.AddRepository("https://github.com/example/monorepo.git");
        var definition = catalog.AddService("orders").WithSharedRepository(repository).Build();

        Assert.Equal("monorepo", definition.Repository.CheckoutName);
    }

    [Fact]
    public void AddRepository_ExplicitNameWins()
    {
        var catalog = new ServiceCatalogBuilder();
        var repository = catalog.AddRepository("https://github.com/example/monorepo", name: "mono");
        var definition = catalog.AddService("orders").WithSharedRepository(repository).Build();

        Assert.Equal("mono", definition.Repository.CheckoutName);
    }

    [Fact]
    public void AddRepository_NameCollidesWithAnotherRepository_ThrowsNamingBothUrls()
    {
        var catalog = new ServiceCatalogBuilder();
        catalog.AddRepository("https://github.com/example/monorepo");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => catalog.AddRepository("https://github.com/other/monorepo"));

        Assert.Contains("monorepo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("example/monorepo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRepository_BlankUrl_Throws() =>
        Assert.Throws<ServiceSourcesConfigurationException>(
            () => new ServiceCatalogBuilder().AddRepository("   "));

    [Fact]
    public void WithSharedRepository_TwoServices_ProduceTheSameRepositoryDefinitionInstance()
    {
        var catalog = new ServiceCatalogBuilder();
        var monorepo = catalog.AddRepository("https://github.com/example/monorepo");

        var orders = catalog.AddService("orders").WithSharedRepository(monorepo).Build();
        var payments = catalog.AddService("payments").WithSharedRepository(monorepo).Build();

        Assert.Same(orders.Repository, payments.Repository);
    }

    [Fact]
    public void WithSharedRepository_SetsUrlAndDefaultRef()
    {
        var catalog = new ServiceCatalogBuilder();
        var monorepo = catalog.AddRepository("https://github.com/example/monorepo", defaultRef: "main");

        var definition = catalog.AddService("orders").WithSharedRepository(monorepo).Build();

        Assert.Equal("https://github.com/example/monorepo", definition.Repository.Url);
        Assert.Equal("main", definition.Repository.DefaultRef);
    }

    [Fact]
    public void WithSharedRepository_ThenWithRepository_ThrowsAdditiveError()
    {
        var catalog = new ServiceCatalogBuilder();
        var monorepo = catalog.AddRepository("https://github.com/example/monorepo");

        var chain = catalog.AddService("orders").WithSharedRepository(monorepo);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithRepository("https://github.com/example/other"));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithRepository", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithRepository_ThenWithSharedRepository_ThrowsAdditiveError()
    {
        var catalog = new ServiceCatalogBuilder();
        var monorepo = catalog.AddRepository("https://github.com/example/monorepo");

        var chain = catalog.AddService("orders").WithRepository("https://github.com/example/other");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithSharedRepository(monorepo));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithSharedRepository", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithPrepare_OnServiceWithSharedRepository_ThrowsNamingTheRepository()
    {
        var catalog = new ServiceCatalogBuilder();
        var monorepo = catalog.AddRepository("https://github.com/example/monorepo");

        var chain = catalog.AddService("orders").WithSharedRepository(monorepo);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithPrepare(["./prepare.sh"]));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("monorepo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithSharedRepository_OnServiceWithPrepareAlready_ThrowsNamingTheRepository()
    {
        var catalog = new ServiceCatalogBuilder();
        var monorepo = catalog.AddRepository("https://github.com/example/monorepo");

        var chain = catalog.AddService("orders").WithPrepare(["./prepare.sh"]);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithSharedRepository(monorepo));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("monorepo", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryBuilder_WithPrepare_SetsPrepareOnTheSharedDefinition()
    {
        var catalog = new ServiceCatalogBuilder();
        var monorepo = catalog.AddRepository("https://github.com/example/monorepo")
            .WithPrepare(["./prepare.sh"], mode: PrepareMode.Once);

        var definition = catalog.AddService("orders").WithSharedRepository(monorepo).Build();

        Assert.NotNull(definition.Repository.Prepare);
        Assert.Equal(["./prepare.sh"], definition.Repository.Prepare.Command!);
        Assert.Equal("once", definition.Repository.Prepare.Mode);
    }

    [Fact]
    public void RepositoryBuilder_WithPrepare_CalledTwice_Throws()
    {
        var chain = new ServiceCatalogBuilder()
            .AddRepository("https://github.com/example/monorepo")
            .WithPrepare(["a.sh"]);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithPrepare(["b.sh"]));

        Assert.Contains("monorepo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithPrepare", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryBuilder_WithPrepare_UndefinedMode_ThrowsNamingTheFourSpellings()
    {
        var chain = new ServiceCatalogBuilder().AddRepository("https://github.com/example/monorepo");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithPrepare(["./prepare.sh"], mode: (PrepareMode)99));

        Assert.Contains("monorepo", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'oncePerCommit'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddRepository_WithNoServices_DoesNotThrow()
    {
        // Design question 2: a repository with no services is left unreported.
        var catalog = new ServiceCatalogBuilder();
        catalog.AddRepository("https://github.com/example/unused");

        var (_, _) = catalog.Freeze();
    }
}
