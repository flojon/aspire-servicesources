using Aspire.Hosting.ServiceSources.Catalog;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

public class JavaScriptServiceSourcesBuilderExtensionsTests
{
    [Fact]
    public void AsJavaScript_SetsKindNameAndTypedOptions()
    {
        var definition = new ServiceCatalogBuilder().AddService("frontend")
            .WithRepository("https://github.com/example/frontend")
            .AsJavaScript(o => o.AppType(JavaScriptAppTypes.Vite).Port(3000))
            .Build();

        Assert.Equal(JavaScriptLocalKind.KindName, definition.Kind);
        var options = Assert.IsType<JavaScriptKindOptions>(definition.KindOptions);
        Assert.Equal(JavaScriptAppTypes.Vite, options.AppType);
        Assert.Equal(3000, options.Port);
    }

    [Fact]
    public void AsJavaScript_ReturnsTheSameBuilderForFurtherChaining()
    {
        var chain = new ServiceCatalogBuilder().AddService("frontend")
            .WithRepository("https://github.com/example/frontend");

        var result = chain.AsJavaScript(o => o.AppType(JavaScriptAppTypes.Vite));

        Assert.Same(chain, result);
    }

    [Fact]
    public void AsJavaScript_CalledAfterWithKind_ThrowsAlreadyCalled()
    {
        var chain = new ServiceCatalogBuilder().AddService("frontend")
            .WithRepository("https://github.com/example/frontend")
            .WithKind("javascript", new Dictionary<string, object> { ["appType"] = "vite" });

        Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.AsJavaScript(o => o.AppType(JavaScriptAppTypes.Vite)));
    }
}
