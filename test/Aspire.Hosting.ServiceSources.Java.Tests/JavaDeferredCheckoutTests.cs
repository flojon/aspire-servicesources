using Aspire.Hosting.ApplicationModel;
using static Aspire.Hosting.ServiceSources.Java.Tests.TestHelpers;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

/// <summary>
/// <c>ResolveDeferred</c> (#159): the java kind builds its whole resource against a checkout that
/// has not been cloned yet, and hands core the checks it could not run because of that.
/// </summary>
public class JavaDeferredCheckoutTests
{
    /// <summary>
    /// The path a clone is going to land in. Never created — that is the entire point: everything
    /// asserted here has to hold with nothing on disk.
    /// </summary>
    private static string PlannedRepoRoot() => Path.Combine(CreateTempDirectory(), "checkouts", "java-api");

    private static DeferredLocalResource ResolveDeferred(
        IDistributedApplicationBuilder builder, string repoRoot, params (string Key, object Value)[] block)
    {
        var registration = new JavaLocalResourceKind()
            .ResolveDeferred(builder, "java-api", repoRoot, Block(block));

        return Assert.IsType<DeferredLocalResource>(registration);
    }

    [Fact]
    public void ResolveDeferred_MissingCheckout_StillBuildsTheResource()
    {
        var builder = CreateBuilder();
        var repoRoot = PlannedRepoRoot();

        var registration = ResolveDeferred(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        var resource = Assert.IsType<JavaAppExecutableResource>(registration.Service.Resource);

        Assert.False(Directory.Exists(repoRoot), "the checkout must not exist for this test to mean anything");
        Assert.Equal("java-api", resource.Name);
        Assert.Equal(Path.GetFullPath(repoRoot), Path.GetFullPath(resource.WorkingDirectory));
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, resource));
    }

    [Fact]
    public void ResolveDeferred_DeclaresTheEndpointFromTheCatalogAlone()
    {
        var builder = CreateBuilder();

        var registration = ResolveDeferred(builder, PlannedRepoRoot(),
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        // Why java can be deferred where dotnet needed a warning: java.port is a required field of
        // the java: block, so the endpoint is fully known from the committed catalog before any
        // clone. Nothing is synthesised from a launch profile, so there is nothing to lose.
        var endpoint = Assert.Single(registration.Service.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(8080, endpoint.TargetPort);
    }

    [Fact]
    public void ResolveDeferred_RunsViaTheWrapperTheCloneWillBring()
    {
        var builder = CreateBuilder();
        var repoRoot = PlannedRepoRoot();

        var registration = ResolveDeferred(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        var resource = Assert.IsType<JavaAppExecutableResource>(registration.Service.Resource);

        // The wrapper is named now and checked later. WithMavenGoal reads the wrapper annotation as
        // it runs, so the command has to be settled at composition time whether the file is there
        // yet or not.
        Assert.Equal(Path.Combine(repoRoot, MavenWrapperName), resource.Command);
    }

    [Fact]
    public void ResolveDeferred_Jar_LooksForNoWrapper()
    {
        var builder = CreateBuilder();
        var repoRoot = PlannedRepoRoot();

        var registration = ResolveDeferred(builder, repoRoot,
            ("jarPath", "build/libs/app.jar"),
            ("port", 8080));

        var resource = Assert.IsType<JavaAppExecutableResource>(registration.Service.Resource);

        // Carried through as written, the way the eager path carries it: the integration resolves it
        // against the working directory when it runs, which is after the clone either way.
        Assert.Equal("build/libs/app.jar", resource.JarPath);
        Assert.Equal(Path.GetFullPath(repoRoot), Path.GetFullPath(resource.WorkingDirectory));

        // "java -jar" runs no wrapper, so a missing one is not something the landed checkout can be
        // wrong about — and the working directory is the only thing left to check.
        var failure = Assert.Throws<ServiceSourcesConfigurationException>(registration.ValidateCheckout!);
        Assert.Contains("workingDirectory", failure.Message);
    }

    [Fact]
    public void ValidateCheckout_MissingWorkingDirectory_ReportsIt()
    {
        var builder = CreateBuilder();
        var repoRoot = PlannedRepoRoot();

        var registration = ResolveDeferred(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("workingDirectory", "services/api"),
            ("port", 8080));

        Assert.NotNull(registration.ValidateCheckout);

        // The same message the eager path gives, moved to the only moment it can be true or false.
        var failure = Assert.Throws<ServiceSourcesConfigurationException>(registration.ValidateCheckout!);
        Assert.Contains("java-api", failure.Message);
        Assert.Contains("services/api", failure.Message);
        Assert.Contains("does not exist in the service's checkout", failure.Message);
    }

    [Fact]
    public void ValidateCheckout_MissingWrapper_ReportsIt()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateTempDirectory();

        var registration = ResolveDeferred(builder, repoRoot,
            ("gradleTask", "bootRun"),
            ("port", 8080));

        // The working directory landed but the wrapper did not — the case that would otherwise reach
        // the developer as a bare exec failure from DCP.
        var failure = Assert.Throws<ServiceSourcesConfigurationException>(registration.ValidateCheckout!);
        Assert.Contains("gradleTask", failure.Message);
        Assert.Contains(GradleWrapperName, failure.Message);
    }

    [Fact]
    public void ValidateCheckout_CheckoutLanded_Passes()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateTempDirectory();

        var registration = ResolveDeferred(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        // Everything the resource was registered against is now real, which is the ordinary case:
        // the clone brought what the catalog said it would.
        WriteWrapper(repoRoot, MavenWrapperName);

        registration.ValidateCheckout!();
    }

    [Fact]
    public void ResolveDeferred_MalformedOptions_StillFailsImmediately()
    {
        var builder = CreateBuilder();

        // A bad options block is settleable from the catalog alone, so it must not wait for a clone
        // to be reported — deferral moves the checkout-dependent checks and nothing else.
        Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new JavaLocalResourceKind().ResolveDeferred(
                builder, "java-api", PlannedRepoRoot(), Block(("mavenGoal", "spring-boot:run"))));
    }
}
