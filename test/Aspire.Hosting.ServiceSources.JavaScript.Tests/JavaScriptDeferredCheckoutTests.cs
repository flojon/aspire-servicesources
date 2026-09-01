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

    [Theory]
    [InlineData("appType: node\nscriptPath: server.js")]
    [InlineData("appType: bun\nscriptPath: server.js")]
    public void ResolveDeferred_NodeWithoutAGuaranteedPackageJson_DeclinesDeferral(string optionsYaml)
    {
        var builder = CreateBuilder();
        var options = TestHelpers.ParseOptionsBlock(optionsYaml);
        var kind = new JavaScriptLocalKind();

        // AddNodeApp/AddBunApp attach their package manager only when they can see a package.json in
        // the app directory, so what a warm run builds depends on what the repository holds — and a
        // cold checkout cannot be looked at. Rather than guess, the kind declines and takes the
        // eager path, which is the "resolve me eagerly" the null return exists for.
        Assert.False(kind.SupportsDeferredCheckout(options));
        Assert.Null(kind.ResolveDeferred(builder, "frontend", PlannedRepoRoot(), options));

        // Declining has to cost nothing: core falls back to the eager path, so anything registered
        // here would be a duplicate.
        Assert.Empty(builder.Resources);
    }

    [Theory]
    [InlineData("appType: node\nscriptPath: server.js\nrunScript: dev", "npm")]
    [InlineData("appType: bun\nscriptPath: server.js\nrunScript: dev", "bun")]
    [InlineData("appType: node\nscriptPath: server.js\npackageManager: pnpm", "pnpm")]
    public void ResolveDeferred_NodeWithAGuaranteedPackageJson_StillGetsItsInstaller(
        string optionsYaml, string expectedPackageManager)
    {
        var builder = CreateBuilder();
        var options = TestHelpers.ParseOptionsBlock(optionsYaml);
        var kind = new JavaScriptLocalKind();

        Assert.True(kind.SupportsDeferredCheckout(options));

        var registration = Assert.IsType<DeferredLocalResource>(
            kind.ResolveDeferred(builder, "frontend", PlannedRepoRoot(), options));

        // The defect this guards: on a cold checkout AddNodeApp/AddBunApp see no package.json and
        // attach nothing, and everything hanging off that annotation goes with it — the installer,
        // the app's wait for it, and the rewrite that makes runScript mean "npm run dev" rather than
        // running scriptPath directly. A first run would exec the entry point against a checkout
        // with no node_modules. Attaching it by hand is what makes the deferred resource the one a
        // warm run builds.
        var installer = Assert.Single(builder.Resources.OfType<JavaScriptInstallerResource>());
        var waits = registration.Service.Resource.Annotations.OfType<WaitAnnotation>();
        Assert.Contains(waits, w => ReferenceEquals(w.Resource, installer));

        var packageManager = Assert.Single(
            registration.Service.Resource.Annotations.OfType<JavaScriptPackageManagerAnnotation>());
        Assert.Equal(expectedPackageManager, packageManager.ExecutableName);
    }

    [Theory]
    [InlineData("appType: vite")]
    [InlineData("appType: nextjs")]
    [InlineData("appType: javascript")]
    public void ResolveDeferred_AppTypesThatAlwaysRunAPackageScript_DeferUnconditionally(string optionsYaml)
    {
        // These three reach a builder call that attaches a package manager whatever is on disk, so
        // cold and warm produce the same resource and there is nothing to decline for.
        Assert.True(new JavaScriptLocalKind().SupportsDeferredCheckout(TestHelpers.ParseOptionsBlock(optionsYaml)));
    }

    [Fact]
    public void SupportsDeferredCheckout_MalformedBlock_AnswersFalseRatherThanThrowing()
    {
        // Probed for services that may never be added, so it is not this call's place to report a
        // bad block: answering false routes it to the eager path, where Validate raises the same
        // parse failure with the service named.
        Assert.False(new JavaScriptLocalKind().SupportsDeferredCheckout(
            TestHelpers.ParseOptionsBlock("appType: not-a-real-app-type")));
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

        // packageManager, because a bare node block is not deferrable at all — see
        // ResolveDeferred_NodeWithoutAGuaranteedPackageJson_DeclinesDeferral.
        var registration = ResolveDeferred(
            builder, repoRoot, "appType: node\nscriptPath: server.js\npackageManager: npm");

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
