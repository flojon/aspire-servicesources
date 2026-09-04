using Aspire.Hosting.ApplicationModel;
using static Aspire.Hosting.ServiceSources.Java.Tests.TestHelpers;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

public class JavaLocalResourceKindTests
{
    private static string CreateRepoRoot(params string[] subdirectories)
    {
        var repoRoot = CreateTempDirectory();
        foreach (var subdirectory in subdirectories)
        {
            Directory.CreateDirectory(Path.Combine(repoRoot, subdirectory));
        }

        return repoRoot;
    }

    private static JavaAppExecutableResource ResolveResource(
        IDistributedApplicationBuilder builder, string repoRoot, params (string Key, object Value)[] block) =>
        Assert.IsType<JavaAppExecutableResource>(
            new JavaLocalResourceKind().Resolve(builder, "java-api", repoRoot, Block(block)).Resource);

    /// <summary>
    /// The checks core runs against the resolved checkout, immediately before <c>Resolve</c>. That
    /// throwing here keeps the service out of the app model is what the matching
    /// <c>UseJavaTests</c> cases establish: <c>ILocalResourceKind.Validate</c> isn't handed a
    /// builder, so it can't be asserted from this side.
    /// </summary>
    private static ServiceSourcesConfigurationException RejectedByValidate(
        string repoRoot, params (string Key, object Value)[] block) =>
        Assert.Throws<ServiceSourcesConfigurationException>(
            () => new JavaLocalResourceKind().Validate("java-api", repoRoot, Block(block)));

    [Fact]
    public void Resolve_MavenGoal_AddsJavaAppRootedAtTheCheckout()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();
        WriteWrapper(repoRoot, MavenWrapperName);

        var resource = ResolveResource(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        Assert.Equal("java-api", resource.Name);
        Assert.Equal(Path.GetFullPath(repoRoot), Path.GetFullPath(resource.WorkingDirectory));
        Assert.Null(resource.JarPath);
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, resource));
    }

    [Fact]
    public void Resolve_MavenGoal_RunsViaTheMavenWrapperRatherThanBareJava()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();
        var wrapper = WriteWrapper(repoRoot, MavenWrapperName);

        var resource = ResolveResource(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        // WithMavenGoal swaps the command from "java" to the wrapper script in run mode; asserting on
        // the command is what distinguishes a real Maven-run wiring from a resource that would start
        // a bare JVM with no arguments.
        Assert.Equal(Path.GetFullPath(wrapper), resource.Command);
    }

    [Fact]
    public void Resolve_GradleTask_RunsViaTheGradleWrapper()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();
        var wrapper = WriteWrapper(repoRoot, GradleWrapperName);

        var resource = ResolveResource(builder, repoRoot,
            ("gradleTask", "bootRun"),
            ("port", 8080));

        Assert.Equal(Path.GetFullPath(wrapper), resource.Command);
    }

    [Fact]
    public void Resolve_JarPath_RunsTheJarAndKeepsTheJavaCommand()
    {
        var builder = CreateBuilder();

        // No wrapper anywhere in this checkout, deliberately: "java -jar" runs none, so nothing
        // about jar mode may depend on one being there.
        var repoRoot = CreateRepoRoot();

        var resource = ResolveResource(builder, repoRoot,
            ("jarPath", "target/app.jar"),
            ("port", 8080));

        Assert.Equal("target/app.jar", resource.JarPath);
        Assert.Equal("java", resource.Command);
    }

    [Fact]
    public void Validate_MavenGoalWithoutTheWrapperInTheCheckout_ThrowsNamingThePathAndTheOverride()
    {
        var repoRoot = CreateRepoRoot();

        // Without this the resource is added happily and DCP fails much later, execing a path the
        // developer never wrote.
        var ex = RejectedByValidate(repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains(Path.GetFullPath(Path.Combine(repoRoot, MavenWrapperName)), ex.Message);
        Assert.Contains("wrapperPath", ex.Message);
    }

    [Fact]
    public void Validate_GradleTaskWithoutTheWrapperInTheCheckout_ThrowsNamingThePathAndTheOverride()
    {
        var repoRoot = CreateRepoRoot();

        var ex = RejectedByValidate(repoRoot,
            ("gradleTask", "bootRun"),
            ("port", 8080));

        Assert.Contains(Path.GetFullPath(Path.Combine(repoRoot, GradleWrapperName)), ex.Message);
        Assert.Contains("wrapperPath", ex.Message);
    }

    [Fact]
    public void Resolve_WrapperPathAtTheRepositoryRoot_RunsItFromTheProjectDirectory()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot(Path.Combine("services", "catalog"));

        // The monorepo shape a multi-project Gradle (or multi-module Maven) repository actually has:
        // one wrapper at the repository root, the project itself further down.
        var wrapper = WriteWrapper(repoRoot, GradleWrapperName);

        var resource = ResolveResource(builder, repoRoot,
            ("workingDirectory", "services/catalog"),
            ("gradleTask", "bootRun"),
            ("wrapperPath", GradleWrapperName),
            ("port", 8080));

        Assert.Equal(Path.GetFullPath(wrapper), resource.Command);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(repoRoot, "services", "catalog")),
            Path.GetFullPath(resource.WorkingDirectory));
    }

    [Fact]
    public void Resolve_WrapperPathForAMavenGoal_RunsThatWrapper()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot("modules");
        var wrapper = WriteWrapper(repoRoot, MavenWrapperName);

        var resource = ResolveResource(builder, repoRoot,
            ("workingDirectory", "modules"),
            ("mavenGoal", "spring-boot:run"),
            ("wrapperPath", MavenWrapperName),
            ("port", 8080));

        Assert.Equal(Path.GetFullPath(wrapper), resource.Command);
    }

    [Fact]
    public void Validate_WrapperPathThatIsNotThere_ThrowsNamingTheConfiguredValue()
    {
        var repoRoot = CreateRepoRoot();

        var ex = RejectedByValidate(repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("wrapperPath", "tools/mvnw"),
            ("port", 8080));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("tools/mvnw", ex.Message);
        Assert.Contains(Path.GetFullPath(Path.Combine(repoRoot, "tools", "mvnw")), ex.Message);
    }

    [Fact]
    public void Resolve_WrapperPathWrittenPosixStyle_RunsThePlatformWrapper()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot(Path.Combine("services", "catalog"));
        var wrapper = WriteWrapper(repoRoot, GradleWrapperName);

        // servicesources.yaml is shared by the whole team across platforms, so a wrapperPath can only
        // be written one way — POSIX-style, as the README's monorepo example writes it. On Windows the
        // file the checkout actually holds is gradlew.bat, and execing the POSIX script instead would
        // fail to start the app.
        var resource = ResolveResource(builder, repoRoot,
            ("workingDirectory", "services/catalog"),
            ("gradleTask", "bootRun"),
            ("wrapperPath", "gradlew"),
            ("port", 8080));

        Assert.Equal(Path.GetFullPath(wrapper), resource.Command);
    }

    // The platform is a parameter of WrapperForPlatform rather than read from OperatingSystem, so that
    // the Windows naming is assertable at all: these tests only ever run on the app host's own
    // platform, and CI's is Linux.
    [Theory]
    [InlineData("mvnw", ".cmd", "mvnw.cmd")]
    [InlineData("gradlew", ".bat", "gradlew.bat")]
    [InlineData("tools/mvnw", ".cmd", "tools/mvnw.cmd")]
    public void WrapperForPlatform_OnWindows_AppendsTheExtensionAnExtensionlessWrapperLacks(
        string wrapperPath, string windowsExtension, string expected) =>
        Assert.Equal(
            expected, JavaLocalResourceKind.WrapperForPlatform(wrapperPath, windowsExtension, isWindows: true));

    [Theory]
    [InlineData("gradlew.bat")]
    [InlineData("tools/mvnw.cmd")]
    [InlineData("scripts/run-maven.sh")]
    public void WrapperForPlatform_OnWindows_LeavesAWrapperThatNamesItsOwnExtension(string wrapperPath) =>
        Assert.Equal(wrapperPath, JavaLocalResourceKind.WrapperForPlatform(wrapperPath, ".cmd", isWindows: true));

    [Theory]
    [InlineData("mvnw")]
    [InlineData("tools/mvnw")]
    public void WrapperForPlatform_OffWindows_LeavesTheWrapperAsWritten(string wrapperPath) =>
        Assert.Equal(wrapperPath, JavaLocalResourceKind.WrapperForPlatform(wrapperPath, ".cmd", isWindows: false));

    [Fact]
    public void Resolve_WorkingDirectory_IsResolvedRelativeToTheCheckout()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot(Path.Combine("services", "api"));
        WriteWrapper(Path.Combine(repoRoot, "services", "api"), MavenWrapperName);

        var resource = ResolveResource(builder, repoRoot,
            ("workingDirectory", "services/api"),
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(repoRoot, "services", "api")),
            Path.GetFullPath(resource.WorkingDirectory));
    }

    [Fact]
    public void Validate_MissingWorkingDirectory_ThrowsNamingTheServiceAndTheResolvedPath()
    {
        var repoRoot = CreateRepoRoot();

        var ex = RejectedByValidate(repoRoot,
            ("workingDirectory", "services/api"),
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("services/api", ex.Message);
    }

    [Fact]
    public void Resolve_Port_BecomesAnHttpEndpointOnTheResource()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();
        WriteWrapper(repoRoot, MavenWrapperName);

        var resource = ResolveResource(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        // AddJavaApp declares no endpoint of its own, so without this the facade AddService() hands
        // back would carry nothing for a consumer's WithReference(...) to resolve.
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(8080, endpoint.TargetPort);
        Assert.Equal("http", endpoint.UriScheme);
    }

    [Fact]
    public void Resolve_ReturnsAResourceUsableForServiceDiscovery()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();
        WriteWrapper(repoRoot, MavenWrapperName);

        var resourceBuilder = new JavaLocalResourceKind().Resolve(
            builder, "java-api", repoRoot, Block(("mavenGoal", "spring-boot:run"), ("port", 8080)));

        // What core copies endpoint annotations from, and what AddService()'s facade stands in for.
        Assert.IsAssignableFrom<IResourceWithServiceDiscovery>(resourceBuilder.Resource);
    }

    [Fact]
    public void Validate_GoodBlock_DoesNotThrow()
    {
        var repoRoot = CreateRepoRoot();
        WriteWrapper(repoRoot, MavenWrapperName);

        new JavaLocalResourceKind().Validate("java-api", repoRoot, Block(
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080)));
    }

    [Fact]
    public void Validate_JarPath_NeedsNoWrapperInTheCheckout()
    {
        // "java -jar" runs no wrapper script, so the wrapper check must not reach jar mode — this
        // checkout holds nothing at all.
        new JavaLocalResourceKind().Validate("java-api", CreateRepoRoot(), Block(
            ("jarPath", "target/app.jar"),
            ("port", 8080)));
    }

    [Fact]
    public void Validate_MalformedBlock_ThrowsNamingTheService()
    {
        var ex = RejectedByValidate(CreateRepoRoot(), ("port", 8080));

        Assert.Contains("java-api", ex.Message);
    }
}
