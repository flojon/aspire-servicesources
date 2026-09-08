using System.Text.Json.Nodes;
using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.Tests;

public sealed class ProbeOptions
{
    public string? Value { get; set; }
}

public sealed class OtherOptions
{
    public string? Other { get; set; }
}

public class LocalKindConfigTests
{
    private sealed class JarOptions
    {
        public string? JarPath { get; set; }

        public string[]? Args { get; set; }

        public int? Port { get; set; }
    }

    private sealed class Options
    {
        public string? AppDirectory { get; set; }

        public string? RunScript { get; set; }
    }

    // What a guest language's withKind(kind, { options: … }) actually delivers: a JsonObject, which
    // the yaml path's non-generic IDictionary test misses and its IEnumerable test then rejects as
    // "a list". Measured against a running TypeScript AppHost.
    [Fact]
    public void Parse_JsonObjectFromGuestLanguage_MapsCamelCaseKeysToProperties()
    {
        var raw = JsonNode.Parse("""{"appDirectory":"apps/plan","runScript":"start"}""");

        var options = LocalKindConfig.Parse<Options>(raw, "planning-frontend");

        Assert.NotNull(options);
        Assert.Equal("apps/plan", options.AppDirectory);
        Assert.Equal("start", options.RunScript);
    }

    [Fact]
    public void Parse_JsonObjectWithSequenceAndNumber_MapsBothThroughUnchanged()
    {
        // The java kind's jarPath block: where a JSON-to-yaml round-trip would break if flow
        // sequences or numeric scalars did not read.
        var raw = JsonNode.Parse(
            """{"jarPath":"graphhopper-web-11.0.jar","args":["server","gh-config-local.yml"],"port":8989}""");

        var options = LocalKindConfig.Parse<JarOptions>(raw, "planning-routing");

        Assert.NotNull(options);
        Assert.Equal("graphhopper-web-11.0.jar", options.JarPath);
        Assert.Equal(["server", "gh-config-local.yml"], Assert.IsType<string[]>(options.Args));
        Assert.Equal(8989, options.Port);
    }

    [Fact]
    public void Parse_JsonObjectWithUnknownProperty_ThrowsNamingPropertyAndService()
    {
        // A code-authored block is as typo-prone as a yaml one, and nothing else validates it.
        var raw = JsonNode.Parse("""{"runScrip":"start"}""");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindConfig.Parse<Options>(raw, "frontend"));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("runScrip", ex.Message);
    }

    [Fact]
    public void Parse_JsonArrayFromGuestLanguage_StillReportsListShape()
    {
        // The wrong shape keeps the shape message instead of reaching the new branch.
        var raw = JsonNode.Parse("""["appDirectory"]""");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindConfig.Parse<Options>(raw, "frontend"));

        Assert.Contains("a list", ex.Message);
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
    public void Parse_SequenceInsteadOfBlock_SaysListRatherThanStringifyingTheCollection()
    {
        // A sequence under the kind key also fails the mapping test, and stringifying it yields
        // "System.Collections.Generic.List`1[System.Object]", which points the reader nowhere.
        var raw = new List<object> { "a", "b" };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindConfig.Parse<Options>(raw, "frontend"));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("a list", ex.Message);
        Assert.DoesNotContain("System.Collections", ex.Message);
    }

    [Fact]
    public void Parse_MalformedBlockWithoutServiceName_StillThrowsConfigurationException()
    {
        Assert.Throws<ServiceSourcesConfigurationException>(() => LocalKindConfig.Parse<Options>("dev"));
    }

    [Fact]
    public void Parse_AlreadyTypedInstance_ReturnedUnchanged()
    {
        var options = new ProbeOptions { Value = "x" };

        var result = LocalKindConfig.Parse<ProbeOptions>(options, "svc");

        Assert.Same(options, result);
    }

    [Fact]
    public void Parse_InstanceOfWrongType_ThrowsNamingBothTypes()
    {
        var options = new OtherOptions { Other = "x" };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindConfig.Parse<ProbeOptions>(options, "svc"));

        Assert.Contains(nameof(OtherOptions), ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(ProbeOptions), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Dictionary_StillRoundTrips()
    {
        var raw = new Dictionary<object, object> { ["value"] = "x" };

        var result = LocalKindConfig.Parse<ProbeOptions>(raw, "svc");

        Assert.Equal("x", result!.Value);
    }

    [Theory]
    [InlineData("a string")]
    [InlineData(42)]
    [InlineData(true)]
    public void Parse_YamlProducedScalar_StillReportsScalarShape(object scalar)
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindConfig.Parse<ProbeOptions>(scalar, "svc"));

        Assert.Contains("must be a block of key/value pairs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_YamlProducedList_StillReportsListShape()
    {
        var list = new List<object> { "a", "b" };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindConfig.Parse<ProbeOptions>(list, "svc"));

        Assert.Contains("a list", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_Null_ReturnsNull()
    {
        Assert.Null(LocalKindConfig.Parse<ProbeOptions>(null, "svc"));
    }
}
