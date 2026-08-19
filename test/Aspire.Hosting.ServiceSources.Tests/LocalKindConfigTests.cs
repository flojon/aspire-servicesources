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
}
