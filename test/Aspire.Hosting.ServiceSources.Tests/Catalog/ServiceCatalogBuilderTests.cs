using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class ServiceCatalogBuilderTests
{
    [Fact]
    public void AddService_ThenFreeze_ProducesOneEntryPerCall()
    {
        var builder = new ServiceCatalogBuilder();

        builder.AddService("orders");
        builder.AddService("payments");

        var frozen = builder.Freeze();

        Assert.Equal(["orders", "payments"], frozen.Keys.Order());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddService_RejectsNullEmptyOrWhitespaceName(string? name)
    {
        var builder = new ServiceCatalogBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService(name!));

        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddService_TwoNamesDifferingOnlyByCase_RejectedNamingBoth()
    {
        var builder = new ServiceCatalogBuilder();
        builder.AddService("Orders");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("'Orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'orders'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddService_SameNameTwice_RejectedAsDuplicate()
    {
        var builder = new ServiceCatalogBuilder();
        builder.AddService("orders");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddService_OnFrozenBuilder_ThrowsInvalidOperationException()
    {
        var builder = new ServiceCatalogBuilder();
        builder.AddService("orders");
        var frozen = builder.Freeze();

        Assert.Throws<InvalidOperationException>(() => builder.AddService("payments"));
        Assert.Single(frozen);
    }
}
