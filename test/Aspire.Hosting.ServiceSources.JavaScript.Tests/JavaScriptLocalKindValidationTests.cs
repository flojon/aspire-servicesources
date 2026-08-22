using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

/// <summary>
/// Covers everything the handler can reject from its options block alone. These all go through
/// <see cref="ILocalResourceKind.Validate"/>, which core calls for every service before any of them
/// has added a resource — a config mistake should be reported alongside the other services'
/// failures, not half-way through building the app model.
/// </summary>
public class JavaScriptLocalKindValidationTests
{
    private static void Validate(string yaml) =>
        new JavaScriptLocalKind().Validate("frontend", TestHelpers.ParseOptionsBlock(yaml));

    private static ServiceSourcesConfigurationException Rejects(string yaml) =>
        Assert.Throws<ServiceSourcesConfigurationException>(() => Validate(yaml));

    [Fact]
    public void NoOptionsBlockIsAccepted()
    {
        // A service can declare kind: javascript and nothing else — every option has a default.
        new JavaScriptLocalKind().Validate("frontend", null);
    }

    [Fact]
    public void FullOptionsBlockIsAccepted() =>
        Validate("""
            appType: vite
            appDirectory: src/frontend
            runScript: start
            packageManager: pnpm
            port: 3000
            targetPort: 5173
            """);

    [Fact]
    public void UnknownPropertyIsRejected()
    {
        var ex = Rejects("runScrip: dev");

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("runScrip", ex.Message);
    }

    [Fact]
    public void NonMappingBlockIsRejected()
    {
        var ex = Rejects("- dev");

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("key/value pairs", ex.Message);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("vite")]
    [InlineData("nextjs")]
    public void AppTypeThatRunsAPackageScriptIsAccepted(string appType) => Validate($"appType: {appType}");

    [Theory]
    [InlineData("node")]
    [InlineData("bun")]
    public void AppTypeThatRunsAScriptFileIsAcceptedWithScriptPath(string appType) =>
        Validate($"""
            appType: {appType}
            scriptPath: server.js
            """);

    [Fact]
    public void AppTypeIsMatchedCaseInsensitively() => Validate("appType: NextJs");

    [Fact]
    public void UnknownAppTypeIsRejected()
    {
        var ex = Rejects("appType: svelte");

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("svelte", ex.Message);
        // The message has to name the alternatives — there is nowhere else to discover them.
        Assert.Contains("javascript, vite, nextjs, node, bun", ex.Message);
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("yarn")]
    [InlineData("pnpm")]
    [InlineData("bun")]
    public void KnownPackageManagerIsAccepted(string packageManager) => Validate($"packageManager: {packageManager}");

    [Fact]
    public void UnknownPackageManagerIsRejected()
    {
        var ex = Rejects("packageManager: nom");

        Assert.Contains("nom", ex.Message);
        Assert.Contains("npm, yarn, pnpm, bun", ex.Message);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("bun")]
    public void ScriptPathIsRequiredByAppTypesThatRunAFile(string appType)
    {
        var ex = Rejects($"appType: {appType}");

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("scriptPath", ex.Message);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("vite")]
    [InlineData("nextjs")]
    public void ScriptPathIsRejectedForAppTypesThatRunAPackageScript(string appType)
    {
        var ex = Rejects($"""
            appType: {appType}
            scriptPath: server.js
            """);

        Assert.Contains("scriptPath", ex.Message);
        Assert.Contains("runScript", ex.Message);
    }

    [Theory]
    [InlineData("vite")]
    [InlineData("nextjs")]
    public void PortEnvIsRejectedForAppTypesThatBindTheirOwnPort(string appType)
    {
        var ex = Rejects($"""
            appType: {appType}
            portEnv: PORT
            """);

        Assert.Contains("portEnv", ex.Message);
        Assert.Contains(appType, ex.Message);
    }

    [Theory]
    [InlineData("port")]
    [InlineData("targetPort")]
    public void PortOutsideTheValidRangeIsRejected(string field)
    {
        var tooHigh = Rejects($"{field}: 65536");
        var tooLow = Rejects($"{field}: 0");

        Assert.Contains("between 1 and 65535", tooHigh.Message);
        Assert.Contains("between 1 and 65535", tooLow.Message);
        Assert.Contains(field, tooHigh.Message);
    }

    [Theory]
    [InlineData("appDirectory")]
    [InlineData("runScript")]
    [InlineData("portEnv")]
    public void ExplicitlyEmptyValueIsRejected(string field)
    {
        // Distinct from omitting the field, which falls back to a default. Writing `runScript: ""`
        // is a mistake, and silently defaulting would hide it.
        var ex = Rejects($"{field}: ''");

        Assert.Contains(field, ex.Message);
        Assert.Contains("empty", ex.Message);
    }
}
