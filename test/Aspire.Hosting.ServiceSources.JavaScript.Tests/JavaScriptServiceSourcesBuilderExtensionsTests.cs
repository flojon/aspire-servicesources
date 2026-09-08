using Aspire.Hosting.ServiceSources;
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

    /// <summary>
    /// Mirrors the Java package's equivalent test: an <see cref="JavaScriptKindOptions"/> instance
    /// built by <c>AsJavaScript</c> survives <see cref="LocalKindConfig.Parse{T}"/>'s already-typed
    /// branch unchanged (design finding 6) and then satisfies the real validation path — the same
    /// <see cref="JavaScriptLocalKind.Validate"/> a yaml-declared service is checked against — rather
    /// than merely being the right C# type.
    /// </summary>
    [Fact]
    public void AsJavaScript_ProducedOptions_ParseSuccessfullyThroughTheExistingValidator()
    {
        var definition = new ServiceCatalogBuilder().AddService("frontend")
            .WithRepository("https://github.com/example/frontend")
            .AsJavaScript(o => o.AppType(JavaScriptAppTypes.Vite).Port(3000))
            .Build();

        // Throws on failure; a Vite app type with no appDirectory override validates against the
        // package.json TestHelpers.CreateRepo() puts at the checkout root by default.
        new JavaScriptLocalKind().Validate("frontend", TestHelpers.CreateRepo(), definition.KindOptions);
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
