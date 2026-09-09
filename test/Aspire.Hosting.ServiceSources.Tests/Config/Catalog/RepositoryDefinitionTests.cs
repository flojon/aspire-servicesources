using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Tests.Config.Catalog;

public class RepositoryDefinitionTests
{
    [Fact]
    public void Properties_RoundTrip()
    {
        var prepare = new PrepareMetadata { Command = ["./prepare.sh"], Mode = "once" };

        var repository = new RepositoryDefinition
        {
            Url = "https://github.com/example/repo",
            DefaultRef = "main",
            Prepare = prepare,
            CheckoutName = "orders",
        };

        Assert.Equal("https://github.com/example/repo", repository.Url);
        Assert.Equal("main", repository.DefaultRef);
        Assert.Same(prepare, repository.Prepare);
        Assert.Equal("orders", repository.CheckoutName);
    }
}
