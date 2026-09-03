using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

/// <summary>
/// Covers everything the handler rejects before it builds anything: the options block, and the
/// paths in it read against the service's checkout. These all go through
/// <see cref="ILocalResourceKind.Validate"/>, which core calls immediately before this service's
/// <see cref="ILocalResourceKind.Resolve"/> and against the same resolved checkout — so a typo'd
/// options block, or one naming a directory the repository does not have, is caught without the
/// handler having to start building a resource first.
/// </summary>
public class JavaScriptLocalKindValidationTests
{
    /// <summary>
    /// Validated against a checkout holding a <c>package.json</c> and a <c>server.js</c> at its
    /// root, which is what every options block here names unless it says otherwise — the cases
    /// about the checkout itself pass their own.
    /// </summary>
    private static void Validate(string yaml, string? repoRoot = null) =>
        new JavaScriptLocalKind().Validate(
            "frontend", repoRoot ?? TestHelpers.CreateRepo(), TestHelpers.ParseOptionsBlock(yaml));

    private static ServiceSourcesConfigurationException Rejects(string yaml, string? repoRoot = null) =>
        Assert.Throws<ServiceSourcesConfigurationException>(() => Validate(yaml, repoRoot));

    [Fact]
    public void NoOptionsBlockIsAccepted()
    {
        // A service can declare kind: javascript and nothing else — every option has a default.
        new JavaScriptLocalKind().Validate("frontend", TestHelpers.CreateRepo(), null);
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
            """, TestHelpers.CreateRepo("src/frontend"));

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
    [InlineData("scriptPath")]
    // The two choice fields follow the same rule as the free-text ones: an explicitly empty value is
    // a mistake to name, not a reason to quietly fall back to the default.
    [InlineData("appType")]
    [InlineData("packageManager")]
    public void ExplicitlyEmptyValueIsRejected(string field)
    {
        // Distinct from omitting the field, which falls back to a default. Writing `runScript: ""`
        // is a mistake, and silently defaulting would hide it.
        var ex = Rejects($"{field}: ''");

        Assert.Contains(field, ex.Message);
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void AppDirectoryMissingFromTheCheckoutIsRejected()
    {
        var ex = Rejects("appDirectory: src/frontendd");

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("src/frontendd", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("vite")]
    [InlineData("nextjs")]
    public void AppDirectoryWithoutAPackageJsonIsRejected(string appType)
    {
        // These app types run a package.json script, so an appDirectory without one cannot work.
        // Left unchecked it reaches the developer as an npm "could not read package.json" from the
        // installer resource, detached from the service whose entry pointed at the wrong directory —
        // the same reason scriptPath is checked to exist.
        var ex = Rejects($"appType: {appType}", TestHelpers.CreateRepo(withPackageJson: false));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("package.json", ex.Message);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("bun")]
    public void RunScriptWithoutAPackageJsonIsRejected(string appType)
    {
        // A run script IS a package.json script, and Aspire's AddNodeApp/AddBunApp only wire up a
        // package manager when the app directory has a package.json. Without this check the run
        // script is silently dropped and the service starts the scriptPath it was told to override.
        var ex = Rejects($"""
            appType: {appType}
            scriptPath: server.js
            runScript: start
            """, TestHelpers.CreateRepo(withPackageJson: false));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("package.json", ex.Message);
        Assert.Contains("runScript", ex.Message);
    }

    [Fact]
    public void ScriptPathMissingFromTheCheckoutIsRejected()
    {
        // Otherwise a typo surfaces at run time as "node: cannot find module", detached from the
        // service whose catalog entry named it — the dotnet kind checks its project file the same way.
        var ex = Rejects("""
            appType: node
            scriptPath: serverr.js
            """);

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("serverr.js", ex.Message);
        Assert.Contains("not found", ex.Message);
    }
}
