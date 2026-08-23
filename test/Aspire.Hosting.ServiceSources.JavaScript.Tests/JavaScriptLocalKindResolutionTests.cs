// NextJsAppResource is [Experimental] in Aspire.Hosting.JavaScript, and the nextjs app type exists
// to reach it — asserting on the type it produces has to opt in to the same diagnostic the handler does.
#pragma warning disable ASPIREJAVASCRIPT001

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

/// <summary>
/// Covers what the handler builds out of an already-resolved checkout: which
/// <c>Aspire.Hosting.JavaScript</c> integration runs the app, and how the options block reaches it.
/// </summary>
public class JavaScriptLocalKindResolutionTests
{
    private static IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string repoRoot, string? yaml = null) =>
        new JavaScriptLocalKind().Resolve(
            builder, "frontend", repoRoot, yaml is null ? null : TestHelpers.ParseOptionsBlock(yaml));

    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory("servicesources-js-apphost-").FullName);

    [Fact]
    public void DefaultsToAJavaScriptAppAtTheRepositoryRoot()
    {
        var repoRoot = TestHelpers.CreateRepo();
        var builder = Builder();

        var app = Resolve(builder, repoRoot);

        var resource = Assert.IsType<JavaScriptAppResource>(app.Resource);
        Assert.Equal("frontend", resource.Name);
        Assert.Equal(repoRoot, resource.WorkingDirectory);
    }

    [Theory]
    [InlineData("vite", typeof(ViteAppResource))]
    [InlineData("nextjs", typeof(NextJsAppResource))]
    public void AppTypeSelectsTheMatchingIntegration(string appType, Type expected)
    {
        var repoRoot = TestHelpers.CreateRepo();

        var app = Resolve(Builder(), repoRoot, $"appType: {appType}");

        Assert.IsType(expected, app.Resource);
    }

    [Theory]
    [InlineData("node", typeof(NodeAppResource))]
    [InlineData("bun", typeof(BunAppResource))]
    public void AppTypeThatRunsAScriptFileSelectsTheMatchingIntegration(string appType, Type expected)
    {
        var repoRoot = TestHelpers.CreateRepo();

        var app = Resolve(Builder(), repoRoot, $"""
            appType: {appType}
            scriptPath: server.js
            """);

        Assert.IsType(expected, app.Resource);
    }

    [Fact]
    public void AppDirectoryIsAnchoredToTheCheckout()
    {
        var repoRoot = TestHelpers.CreateRepo("src/frontend");

        var app = Resolve(Builder(), repoRoot, "appDirectory: src/frontend");

        var resource = Assert.IsType<JavaScriptAppResource>(app.Resource);
        Assert.Equal(Path.Combine(repoRoot, "src", "frontend"), resource.WorkingDirectory);
    }

    [Fact]
    public void MissingAppDirectoryIsReportedAgainstTheService()
    {
        var repoRoot = TestHelpers.CreateRepo();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(Builder(), repoRoot, "appDirectory: src/frontendd"));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("src/frontendd", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Theory]
    [InlineData("javascript")]
    [InlineData("vite")]
    [InlineData("nextjs")]
    public void AppDirectoryWithoutAPackageJsonIsReportedAgainstTheService(string appType)
    {
        // These app types run a package.json script, so an appDirectory without one cannot work.
        // Left unchecked it reaches the developer as an npm "could not read package.json" from the
        // installer resource, detached from the service whose entry pointed at the wrong directory —
        // the same reason scriptPath is checked to exist.
        var repoRoot = TestHelpers.CreateRepo(withPackageJson: false);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(Builder(), repoRoot, $"appType: {appType}"));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("package.json", ex.Message);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("bun")]
    public void RunScriptWithoutAPackageJsonIsReportedAgainstTheService(string appType)
    {
        // A run script IS a package.json script, and Aspire's AddNodeApp/AddBunApp only wire up a
        // package manager when the app directory has a package.json. Without this check the run
        // script is silently dropped and the service starts the scriptPath it was told to override.
        var repoRoot = TestHelpers.CreateRepo(withPackageJson: false);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(Builder(), repoRoot, $"""
                appType: {appType}
                scriptPath: server.js
                runScript: start
                """));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("package.json", ex.Message);
        Assert.Contains("runScript", ex.Message);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("bun")]
    public void AnAppTypeThatRunsAScriptFileNeedsNoPackageJson(string appType)
    {
        // The whole point of these two app types: a checkout holding nothing but an entry-point file
        // is a legitimate service, so the package.json requirement must not reach them.
        var repoRoot = TestHelpers.CreateRepo(withPackageJson: false);

        var app = Resolve(Builder(), repoRoot, $"""
            appType: {appType}
            scriptPath: server.js
            """);

        Assert.Equal("frontend", app.Resource.Name);
    }

    [Theory]
    [InlineData("../..")]
    [InlineData("/etc")]
    public void AppDirectoryOutsideTheCheckoutIsRejected(string appDirectory)
    {
        // Path.Combine returns an absolute appDirectory unchanged and "../.." climbs out of the
        // checkout, either of which would otherwise run something the service doesn't own.
        var repoRoot = TestHelpers.CreateRepo();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(Builder(), repoRoot, $"appDirectory: {appDirectory}"));

        Assert.Contains("outside the service's checkout", ex.Message);
    }

    [Fact]
    public void AppDirectoryIsComparedWithTheCheckoutTheWayTheFilesystemCompares()
    {
        // An appDirectory that climbs out of the checkout and back in ("../Frontend/web") is the one
        // way its resolved path can differ from the root in casing. Where the filesystem ignores
        // casing that is the same directory and has to be accepted; where it does not it is a
        // different directory and the guard has to reject it. Which branch runs is decided by
        // probing the filesystem rather than by the OS, since a macOS volume can be either.
        var repoRoot = TestHelpers.CreateRepo("web");
        var parent = Path.GetDirectoryName(repoRoot)!;
        var recased = Path.GetFileName(repoRoot).ToUpperInvariant();
        var appDirectory = $"../{recased}/web";

        if (Directory.Exists(Path.Combine(parent, recased)))
        {
            var app = Resolve(Builder(), repoRoot, $"appDirectory: {appDirectory}");

            Assert.Equal("frontend", app.Resource.Name);
        }
        else
        {
            var ex = Assert.Throws<ServiceSourcesConfigurationException>(
                () => Resolve(Builder(), repoRoot, $"appDirectory: {appDirectory}"));

            Assert.Contains("outside the service's checkout", ex.Message);
        }
    }

    [Fact]
    public void AppDirectoryIsAnchoredToACheckoutPathThatEndsInASeparator()
    {
        // A developer "path" override reaches the handler verbatim, and shell tab-completion puts a
        // trailing slash on it. Path.GetFullPath preserves that slash, so a containment check that
        // appends its own separator would compare against "root//" and reject every appDirectory the
        // service could name — including the default ".".
        var repoRoot = TestHelpers.CreateRepo() + Path.DirectorySeparatorChar;

        var app = Resolve(Builder(), repoRoot);

        var resource = Assert.IsType<JavaScriptAppResource>(app.Resource);
        Assert.Equal(Path.TrimEndingDirectorySeparator(repoRoot), resource.WorkingDirectory);
    }

    [Theory]
    [InlineData("../../../../etc/evil.js")]
    [InlineData("/etc/evil.js")]
    public void ScriptPathOutsideTheCheckoutIsRejected(string scriptPath)
    {
        // node/bun are handed this file to execute, so it needs the same guard appDirectory gets:
        // without it a catalog entry runs something the service doesn't own.
        var repoRoot = TestHelpers.CreateRepo();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(Builder(), repoRoot, $"""
                appType: node
                scriptPath: {scriptPath}
                """));

        Assert.Contains("outside the service's checkout", ex.Message);
    }

    [Fact]
    public void MissingScriptPathIsReportedAgainstTheService()
    {
        // Otherwise a typo surfaces at run time as "node: cannot find module", detached from the
        // service whose catalog entry named it — the dotnet kind checks its project file the same way.
        var repoRoot = TestHelpers.CreateRepo();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => Resolve(Builder(), repoRoot, """
                appType: node
                scriptPath: serverr.js
                """));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("serverr.js", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void ScriptPathIsResolvedRelativeToTheAppDirectory()
    {
        // AddNodeApp runs the script from the app directory, so that is what it is checked against —
        // not the repository root.
        var repoRoot = TestHelpers.CreateRepo("src/frontend");

        var app = Resolve(Builder(), repoRoot, """
            appType: node
            appDirectory: src/frontend
            scriptPath: server.js
            """);

        Assert.IsType<NodeAppResource>(app.Resource);
    }

    [Fact]
    public void RunScriptReachesTheResource()
    {
        var repoRoot = TestHelpers.CreateRepo();

        var app = Resolve(Builder(), repoRoot, "runScript: start");

        var runScript = Assert.Single(app.Resource.Annotations.OfType<JavaScriptRunScriptAnnotation>());
        Assert.Equal("start", runScript.ScriptName);
    }

    [Theory]
    [InlineData("node")]
    [InlineData("bun")]
    public void RunScriptOverridesTheScriptFileForAppTypesThatRunOne(string appType)
    {
        // AddNodeApp/AddBunApp take the file to execute, so a run script can only be layered on
        // afterwards — Aspire's own documented pattern for these two.
        var repoRoot = TestHelpers.CreateRepo();

        var app = Resolve(Builder(), repoRoot, $"""
            appType: {appType}
            scriptPath: server.js
            runScript: start
            """);

        var runScript = Assert.Single(app.Resource.Annotations.OfType<JavaScriptRunScriptAnnotation>());
        Assert.Equal("start", runScript.ScriptName);
    }

    [Theory]
    [InlineData("npm", "npm")]
    [InlineData("yarn", "yarn")]
    [InlineData("pnpm", "pnpm")]
    [InlineData("bun", "bun")]
    public void PackageManagerSelectsTheMatchingModifier(string packageManager, string expectedExecutable)
    {
        var repoRoot = TestHelpers.CreateRepo();

        var app = Resolve(Builder(), repoRoot, $"packageManager: {packageManager}");

        // The modifier is applied last, so the annotation it added is the one that counts.
        var annotation = app.Resource.Annotations.OfType<JavaScriptPackageManagerAnnotation>().Last();
        Assert.Equal(expectedExecutable, annotation.ExecutableName);
    }

    [Fact]
    public void PackageManagerLeftUnsetKeepsTheIntegrationsOwnChoice()
    {
        // AddBunApp already picks Bun; defaulting to npm here would quietly override it.
        var repoRoot = TestHelpers.CreateRepo();

        var app = Resolve(Builder(), repoRoot, """
            appType: bun
            scriptPath: server.js
            """);

        var annotation = app.Resource.Annotations.OfType<JavaScriptPackageManagerAnnotation>().Last();
        Assert.Equal("bun", annotation.ExecutableName);
    }

    [Fact]
    public void AnHttpEndpointIsAlwaysAdded()
    {
        // AddJavaScriptApp adds no endpoint of its own, and without one the facade AddService hands
        // back carries nothing for a consumer's WithReference to resolve.
        var repoRoot = TestHelpers.CreateRepo();

        var app = Resolve(Builder(), repoRoot);

        var endpoint = TestHelpers.SingleEndpoint(app.Resource);
        Assert.Equal("http", endpoint.Name);
        Assert.Equal("PORT", TestHelpers.TargetPortEnvironmentVariable(endpoint));
    }

    [Fact]
    public void PortEnvRenamesThePortEnvironmentVariable()
    {
        var repoRoot = TestHelpers.CreateRepo();

        var app = Resolve(Builder(), repoRoot, "portEnv: SERVER_PORT");

        Assert.Equal("SERVER_PORT", TestHelpers.TargetPortEnvironmentVariable(TestHelpers.SingleEndpoint(app.Resource)));
    }

    [Fact]
    public void PortAndTargetPortReachTheEndpoint()
    {
        var repoRoot = TestHelpers.CreateRepo();

        var app = Resolve(Builder(), repoRoot, """
            port: 3000
            targetPort: 3001
            """);

        var endpoint = TestHelpers.SingleEndpoint(app.Resource);
        Assert.Equal(3000, endpoint.Port);
        Assert.Equal(3001, endpoint.TargetPort);
    }

    [Fact]
    public void ViteKeepsItsOwnSingleEndpointAndItsOwnPortVariable()
    {
        // AddViteApp already added an "http" endpoint, bound to the port variable of its own
        // choosing. Setting a port has to update that endpoint rather than add a second one, and
        // must leave the variable Vite wired up alone — which is why portEnv is rejected outright
        // for this app type.
        var repoRoot = TestHelpers.CreateRepo();

        // The baseline comes from AddViteApp directly rather than from Resolve: taken from Resolve it
        // would already have been through WithHttpEndpoint, so if that call ever cleared Vite's own
        // variable both sides would read null and the assertion below could not fail.
        var untouched = Builder().AddViteApp("frontend", repoRoot);
        var viteOwnPortVariable = TestHelpers.TargetPortEnvironmentVariable(TestHelpers.SingleEndpoint(untouched.Resource));

        var app = Resolve(Builder(), repoRoot, """
            appType: vite
            port: 4000
            """);

        var endpoint = TestHelpers.SingleEndpoint(app.Resource);
        Assert.Equal("http", endpoint.Name);
        Assert.Equal(4000, endpoint.Port);
        Assert.Equal(viteOwnPortVariable, TestHelpers.TargetPortEnvironmentVariable(endpoint));
    }

    [Fact]
    public void TheResolvedResourceExposesServiceDiscovery()
    {
        // The whole point of the handler's return type: AddService copies this resource's endpoint
        // annotations onto the facade it hands the AppHost author.
        var repoRoot = TestHelpers.CreateRepo();

        var app = Resolve(Builder(), repoRoot);

        Assert.IsAssignableFrom<IResourceWithServiceDiscovery>(app.Resource);
    }
}
