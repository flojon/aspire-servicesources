using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.Tests;

public class LocalKindConfigTests
{
    private sealed class Options
    {
        public string? AppDirectory { get; set; }

        public string? RunScript { get; set; }
    }

    [Fact]
    public void Parse_NullConfig_ReturnsNull()
    {
        Assert.Null(LocalKindConfig.Parse<Options>(null));
    }

    [Fact]
    public void Parse_RawDictionary_MapsCamelCaseKeysToProperties()
    {
        var raw = new Dictionary<object, object>
        {
            ["appDirectory"] = ".",
            ["runScript"] = "dev",
        };

        var options = LocalKindConfig.Parse<Options>(raw);

        Assert.NotNull(options);
        Assert.Equal(".", options.AppDirectory);
        Assert.Equal("dev", options.RunScript);
    }

    [Fact]
    public void Parse_UnknownProperty_ThrowsNamingPropertyAndService()
    {
        // A typo in the kind block used to be swallowed (leaving RunScript null and the service
        // silently running the handler's default script), because this is the one block the
        // catalog loader's own unknown-property checks can't see into.
        var raw = new Dictionary<object, object> { ["runScrip"] = "dev" };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindConfig.Parse<Options>(raw, "frontend"));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("runScrip", ex.Message);
    }

    [Fact]
    public void Parse_ScalarInsteadOfBlock_ThrowsConfigurationExceptionNamingService()
    {
        // `javascript: dev` instead of an indented block — must not surface YamlDotNet's bare
        // "Exception during deserialization" out of this public API.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindConfig.Parse<Options>("dev", "frontend"));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("key/value pairs", ex.Message);
    }

    [Fact]
    public void Parse_MalformedBlockWithoutServiceName_StillThrowsConfigurationException()
    {
        Assert.Throws<ServiceSourcesConfigurationException>(() => LocalKindConfig.Parse<Options>("dev"));
    }
}
