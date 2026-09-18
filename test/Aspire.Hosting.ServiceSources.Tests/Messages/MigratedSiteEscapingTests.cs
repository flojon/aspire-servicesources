using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

public class MigratedSiteEscapingTests
{
    private const string Forgery = "orders'\nFATAL: everything is fine";

    [Fact]
    public void For_EscapesANameHole()
    {
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name(Forgery)}' failed.");

        Assert.DoesNotContain("\n", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("' failed.\nFATAL", exception.Message, StringComparison.Ordinal);
        Assert.StartsWith("Service 'orders\\'\\n", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void For_CarriesAnInnerException()
    {
        var inner = new InvalidOperationException("underneath");
        var exception = ServiceSourcesConfigurationException.For($"Service '{new Name("orders")}'.", inner);

        Assert.Same(inner, exception.InnerException);
        Assert.Equal("Service 'orders'.", exception.Message);
    }

    [Fact]
    public void For_AcceptsAMessageWithNoHoles() =>
        Assert.Equal("nothing interpolated here",
            ServiceSourcesConfigurationException.For($"nothing interpolated here").Message);
}
