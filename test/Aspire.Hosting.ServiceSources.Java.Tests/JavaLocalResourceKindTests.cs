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

    [Fact]
    public void Resolve_MavenGoal_AddsJavaAppRootedAtTheCheckout()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();

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

        var resource = ResolveResource(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        // WithMavenGoal swaps the command from "java" to the wrapper script in run mode; asserting on
        // the command is what distinguishes a real Maven-run wiring from a resource that would start
        // a bare JVM with no arguments.
        Assert.Equal(
            Path.GetFullPath(Path.Combine(repoRoot, OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw")),
            resource.Command);
    }

    [Fact]
    public void Resolve_GradleTask_RunsViaTheGradleWrapper()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();

        var resource = ResolveResource(builder, repoRoot,
            ("gradleTask", "bootRun"),
            ("port", 8080));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(repoRoot, OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew")),
            resource.Command);
    }

    [Fact]
    public void Resolve_JarPath_RunsTheJarAndKeepsTheJavaCommand()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();

        var resource = ResolveResource(builder, repoRoot,
            ("jarPath", "target/app.jar"),
            ("port", 8080));

        Assert.Equal("target/app.jar", resource.JarPath);
        Assert.Equal("java", resource.Command);
    }

    [Fact]
    public void Resolve_WorkingDirectory_IsResolvedRelativeToTheCheckout()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot(Path.Combine("services", "api"));

        var resource = ResolveResource(builder, repoRoot,
            ("workingDirectory", "services/api"),
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080));

        Assert.Equal(
            Path.GetFullPath(Path.Combine(repoRoot, "services", "api")),
            Path.GetFullPath(resource.WorkingDirectory));
    }

    [Fact]
    public void Resolve_MissingWorkingDirectory_ThrowsNamingTheServiceAndTheResolvedPath()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();

        // Reported from Resolve only because Validate isn't handed the checkout directory; the
        // checkout does exist by the time core calls Validate. Move this once that signature carries
        // repoRoot.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ResolveResource(builder, repoRoot,
            ("workingDirectory", "services/api"),
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080)));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("services/api", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "java-api");
    }

    [Fact]
    public void Resolve_Port_BecomesAnHttpEndpointOnTheResource()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();

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

        var resourceBuilder = new JavaLocalResourceKind().Resolve(
            builder, "java-api", repoRoot, Block(("mavenGoal", "spring-boot:run"), ("port", 8080)));

        // What core copies endpoint annotations from, and what AddService()'s facade stands in for.
        Assert.IsAssignableFrom<IResourceWithServiceDiscovery>(resourceBuilder.Resource);
    }

    [Fact]
    public void Validate_GoodBlock_DoesNotThrowAndAddsNothingToTheAppModel()
    {
        var builder = CreateBuilder();

        new JavaLocalResourceKind().Validate("java-api", Block(
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080)));

        Assert.Empty(builder.Resources);
    }

    [Fact]
    public void Validate_MalformedBlock_ThrowsBeforeAnythingReachesTheAppModel()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => new JavaLocalResourceKind().Validate("java-api", Block(("port", 8080))));

        Assert.Contains("java-api", ex.Message);
    }
}
