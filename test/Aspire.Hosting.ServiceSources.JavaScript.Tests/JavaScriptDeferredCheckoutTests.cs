using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

/// <summary>
/// <c>ResolveDeferred</c> (#159): the javascript kind builds its whole resource against a checkout
/// that has not been cloned yet, and hands core the checks it could not run because of that.
/// </summary>
public class JavaScriptDeferredCheckoutTests
{
    /// <summary>
    /// The path a clone is going to land in. Never created — that is the entire point: everything
    /// asserted here has to hold with nothing on disk.
    /// </summary>
    private static string PlannedRepoRoot() => Path.Combine(
        Directory.CreateTempSubdirectory("servicesources-js-").FullName, "checkouts", "frontend");

    private static IDistributedApplicationBuilder CreateBuilder() =>
        TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

    private static DeferredLocalResource ResolveDeferred(
        IDistributedApplicationBuilder builder, string repoRoot, string optionsYaml)
    {
        var registration = new JavaScriptLocalKind().ResolveDeferred(
            builder, "frontend", repoRoot, TestHelpers.ParseOptionsBlock(optionsYaml));

        return Assert.IsType<DeferredLocalResource>(registration);
    }

    [Fact]
    public void ResolveDeferred_MissingCheckout_StillBuildsTheResource()
    {
        var builder = CreateBuilder();
        var repoRoot = PlannedRepoRoot();

        var registration = ResolveDeferred(builder, repoRoot, "appType: vite");

        var resource = Assert.IsType<ViteAppResource>(registration.Service.Resource);

        Assert.False(Directory.Exists(repoRoot), "the checkout must not exist for this test to mean anything");
        Assert.Equal("frontend", resource.Name);
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, resource));
    }

    [Fact]
    public void ResolveDeferred_StillGetsAnHttpEndpoint()
    {
        var builder = CreateBuilder();

        var registration = ResolveDeferred(builder, PlannedRepoRoot(), "appType: vite");

        // Why javascript can be deferred: port/targetPort are optional and Aspire allocates them
        // when unset, and the service always gets an "http" endpoint. None of that reads the
        // repository, so a consumer's WithReference resolves exactly as it does on a warm run.
        var endpoint = TestHelpers.SingleEndpoint(registration.Service.Resource);
        Assert.Equal("http", endpoint.Name);
    }

    [Fact]
    public void ResolveDeferred_HonoursAConfiguredPort()
    {
        var builder = CreateBuilder();

        var registration = ResolveDeferred(builder, PlannedRepoRoot(), "appType: vite\nport: 5173");

        Assert.Equal(5173, TestHelpers.SingleEndpoint(registration.Service.Resource).Port);
    }

    [Fact]
    public void ResolveDeferred_AddsTheInstallerResourceForCoreToHoldBack()
    {
        var builder = CreateBuilder();

        var registration = ResolveDeferred(builder, PlannedRepoRoot(), "appType: vite\npackageManager: npm");

        // The wrinkle #159 calls out. The integration creates a separate resource to run
        // "npm install", which the app waits for. This handler does not hold it back itself — core
        // withholds everything the call added — but it has to actually be there for core to find,
        // so the shape is asserted here.
        var installer = Assert.Single(builder.Resources.OfType<JavaScriptInstallerResource>());
        Assert.NotSame(registration.Service.Resource, installer);

        // And the app waits for it, which is why core has to start the installer first.
        var waits = registration.Service.Resource.Annotations.OfType<WaitAnnotation>();
        Assert.Contains(waits, w => ReferenceEquals(w.Resource, installer));
    }

    [Fact]
    public void ValidateCheckout_MissingAppDirectory_ReportsIt()
    {
        var builder = CreateBuilder();

        var registration = ResolveDeferred(builder, PlannedRepoRoot(), "appType: vite\nappDirectory: web");

        Assert.NotNull(registration.ValidateCheckout);

        // The same message the eager path gives, moved to the only moment it can be true or false.
        var failure = Assert.Throws<ServiceSourcesConfigurationException>(registration.ValidateCheckout!);
        Assert.Contains("frontend", failure.Message);
        Assert.Contains("appDirectory 'web'", failure.Message);
    }

    [Fact]
    public void ValidateCheckout_MissingScriptPath_ReportsIt()
    {
        var builder = CreateBuilder();
        var repoRoot = TestHelpers.CreateRepo(withPackageJson: false);
        File.Delete(Path.Combine(repoRoot, "server.js"));

        var registration = ResolveDeferred(builder, repoRoot, "appType: node\nscriptPath: server.js");

        // The app directory landed but the entry point did not — otherwise a "node: cannot find
        // module" at run time, detached from the entry that named it.
        var failure = Assert.Throws<ServiceSourcesConfigurationException>(registration.ValidateCheckout!);
        Assert.Contains("scriptPath 'server.js'", failure.Message);
    }

    [Fact]
    public void ValidateCheckout_MissingPackageJson_ReportsIt()
    {
        var builder = CreateBuilder();
        var repoRoot = TestHelpers.CreateRepo(withPackageJson: false);

        var registration = ResolveDeferred(builder, repoRoot, "appType: vite");

        var failure = Assert.Throws<ServiceSourcesConfigurationException>(registration.ValidateCheckout!);
        Assert.Contains("package.json", failure.Message);
    }

    [Fact]
    public void ValidateCheckout_CheckoutLanded_Passes()
    {
        var builder = CreateBuilder();
        var repoRoot = TestHelpers.CreateRepo();

        var registration = ResolveDeferred(builder, repoRoot, "appType: vite");

        // The ordinary case: the clone brought what the catalog said it would.
        registration.ValidateCheckout!();
    }

    [Fact]
    public void ResolveDeferred_PathOutsideTheCheckout_StillFailsImmediately()
    {
        var builder = CreateBuilder();

        // Containment is pure path arithmetic — nothing about it needs the repository — so it must
        // not wait for a clone to be reported. Deferral moves the checkout-dependent checks only.
        var failure = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new JavaScriptLocalKind().ResolveDeferred(
                builder, "frontend", PlannedRepoRoot(),
                TestHelpers.ParseOptionsBlock("appType: vite\nappDirectory: ../elsewhere")));

        Assert.Contains("outside the service's checkout", failure.Message);
    }

    [Fact]
    public void ResolveDeferred_MalformedOptions_StillFailsImmediately()
    {
        var builder = CreateBuilder();

        Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new JavaScriptLocalKind().ResolveDeferred(
                builder, "frontend", PlannedRepoRoot(), TestHelpers.ParseOptionsBlock("appType: haskell")));
    }
}
