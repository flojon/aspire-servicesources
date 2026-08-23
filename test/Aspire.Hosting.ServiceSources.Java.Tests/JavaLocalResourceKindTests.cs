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

        // No wrapper anywhere in this checkout, deliberately: "java -jar" needs none, so the wrapper
        // check must not reach jar mode.
        var repoRoot = CreateRepoRoot();

        var resource = ResolveResource(builder, repoRoot,
            ("jarPath", "target/app.jar"),
            ("port", 8080));

        Assert.Equal("target/app.jar", resource.JarPath);
        Assert.Equal("java", resource.Command);
    }

    [Fact]
    public void Resolve_MavenGoalWithoutTheWrapperInTheCheckout_ThrowsNamingThePathAndTheOverride()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();

        // Without this the resource is added happily and DCP fails much later, execing a path the
        // developer never wrote.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ResolveResource(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080)));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains(Path.GetFullPath(Path.Combine(repoRoot, MavenWrapperName)), ex.Message);
        Assert.Contains("wrapperPath", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "java-api");
    }

    [Fact]
    public void Resolve_GradleTaskWithoutTheWrapperInTheCheckout_ThrowsNamingThePathAndTheOverride()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ResolveResource(builder, repoRoot,
            ("gradleTask", "bootRun"),
            ("port", 8080)));

        Assert.Contains(Path.GetFullPath(Path.Combine(repoRoot, GradleWrapperName)), ex.Message);
        Assert.Contains("wrapperPath", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "java-api");
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
    public void Resolve_WrapperPathThatIsNotThere_ThrowsNamingTheConfiguredValue()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateRepoRoot();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ResolveResource(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("wrapperPath", "tools/mvnw"),
            ("port", 8080)));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("tools/mvnw", ex.Message);
        Assert.Contains(Path.GetFullPath(Path.Combine(repoRoot, "tools", "mvnw")), ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "java-api");
    }

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
        new JavaLocalResourceKind().Validate("java-api", Block(
            ("mavenGoal", "spring-boot:run"),
            ("port", 8080)));
    }

    [Fact]
    public void Validate_MalformedBlock_ThrowsNamingTheService()
    {
        // That throwing here keeps the service out of the app model is what
        // UseJavaTests.AddService_MalformedJavaBlock_ThrowsWithoutAddingAnyResource establishes:
        // ILocalResourceKind.Validate isn't handed a builder, so it can't be asserted from here.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => new JavaLocalResourceKind().Validate("java-api", Block(("port", 8080))));

        Assert.Contains("java-api", ex.Message);
    }
}
