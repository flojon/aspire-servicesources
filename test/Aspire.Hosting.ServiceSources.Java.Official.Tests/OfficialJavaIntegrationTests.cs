using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Java;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Java.Official.Tests;

/// <summary>
/// The <c>java</c> kind in an AppHost that references Aspire's own <c>Aspire.Hosting.Java</c> and
/// not the Community Toolkit's — the only test project where that integration is the one detected.
/// </summary>
[Trait("IO", "true")]
public class OfficialJavaIntegrationTests
{
    private const string ServiceName = "java-api";

    private static string MavenWrapperName => OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw";

    private static string GradleWrapperName => OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew";

    [Fact]
    public void Installed_IsTheOfficialIntegration()
    {
        Assert.IsType<OfficialJavaIntegration>(JavaIntegration.Installed);
        Assert.Equal(typeof(JavaAppResource), new JavaLocalResourceKind().ResourceType);
    }

    /// <summary>
    /// The too-old message quotes the version core compiles against, which lives in
    /// <c>Directory.Packages.props</c>; a bump there has to move the message with it.
    /// </summary>
    [Fact]
    public void MinimumVersion_IsTheVersionCoreRestores()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(directory!.FullName, "Directory.Packages.props")))
        {
            directory = directory.Parent;
        }

        var packages = File.ReadAllText(Path.Combine(directory.FullName, "Directory.Packages.props"));

        Assert.Contains(
            $"<PackageVersion Include=\"Aspire.Hosting.Java\" Version=\"{OfficialJavaIntegration.MinimumVersion}\" />",
            packages,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_MavenGoal_RunsTheCheckoutsWrapper()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateTempDirectory();
        var wrapper = WriteFile(repoRoot, MavenWrapperName);

        var resource = Resolve(builder, repoRoot, ("mavenGoal", "spring-boot:run"), ("port", 8080));

        Assert.Equal(ServiceName, resource.Name);
        Assert.Equal(Path.GetFullPath(repoRoot), Path.GetFullPath(resource.WorkingDirectory));
        await AssertLaunchesAsync(wrapper, resource);
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, resource));
    }

    [Fact]
    public async Task Resolve_GradleTask_RunsTheCheckoutsWrapper()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateTempDirectory();
        var wrapper = WriteFile(repoRoot, GradleWrapperName);

        var resource = Resolve(builder, repoRoot, ("gradleTask", "bootRun"), ("port", 8080));

        await AssertLaunchesAsync(wrapper, resource);
    }

    [Fact]
    public async Task Resolve_WrapperPathAtTheRepositoryRoot_RunsThatWrapper()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateTempDirectory();
        Directory.CreateDirectory(Path.Combine(repoRoot, "services", "api"));
        var wrapper = WriteFile(repoRoot, MavenWrapperName);

        var resource = Resolve(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"),
            ("workingDirectory", "services/api"),
            ("wrapperPath", "mvnw"),
            ("port", 8080));

        Assert.Equal(Path.GetFullPath(Path.Combine(repoRoot, "services", "api")), Path.GetFullPath(resource.WorkingDirectory));
        await AssertLaunchesAsync(wrapper, resource);
    }

    [Fact]
    public void Resolve_JarPath_RunsJava()
    {
        var builder = CreateBuilder();
        var repoRoot = CreateTempDirectory();

        var resource = Resolve(builder, repoRoot, ("jarPath", "target/app.jar"), ("port", 8080));

        Assert.Equal("java", resource.Command);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public void Resolve_Port_BecomesAnEndpointNamedAfterTheScheme(string scheme)
    {
        var builder = CreateBuilder();
        var repoRoot = CreateTempDirectory();
        WriteFile(repoRoot, MavenWrapperName);

        var resource = Resolve(builder, repoRoot,
            ("mavenGoal", "spring-boot:run"), ("port", 8443), ("scheme", scheme));

        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(8443, endpoint.TargetPort);
        Assert.Equal(scheme, endpoint.UriScheme);
        Assert.Equal(scheme, endpoint.Name);
        Assert.Contains(resource.Annotations, a => a is OtlpExporterAnnotation);
    }

    /// <summary>
    /// The official integration checks its wrapper itself, later than composition. A deferred
    /// service is registered before its clone, so that check must not run until the clone has had
    /// the chance to bring the wrapper — at the latest, not at <c>BeforeStartEvent</c>.
    /// </summary>
    [Fact]
    public async Task ResolveDeferred_WrapperNotClonedYet_SurvivesBeforeStart()
    {
        var builder = CreateBuilderThatCanStart(CreateTempDirectory());
        var repoRoot = Path.Combine(CreateTempDirectory(), "checkouts", ServiceName);

        var registration = new JavaLocalResourceKind().ResolveDeferred(
            builder, ServiceName, repoRoot, Block(("mavenGoal", "spring-boot:run"), ("port", 8080)));

        var resource = Assert.IsType<JavaAppResource>(registration!.Service.Resource);
        await AssertLaunchesAsync(Path.Combine(repoRoot, MavenWrapperName), resource);

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));

        Assert.Throws<ServiceSourcesConfigurationException>(registration.ValidateCheckout!);
    }

    /// <summary>
    /// Windows cannot exec a <c>.cmd</c>/<c>.bat</c> directly, so there the official integration runs
    /// the wrapper through <c>cmd.exe /c call</c>, naming it relative to the working directory;
    /// elsewhere the wrapper is the command itself.
    /// </summary>
    private static async Task AssertLaunchesAsync(string wrapper, JavaAppResource resource)
    {
        var expected = Path.GetFullPath(wrapper);

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(expected, Path.GetFullPath(resource.Command));
            return;
        }

        // Its replacement is not in the Aspire version core builds against.
#pragma warning disable CS0618
        var args = await resource.GetArgumentValuesAsync();
#pragma warning restore CS0618
        Assert.Contains(args, arg => string.Equals(
            Path.GetFullPath(Path.Combine(resource.WorkingDirectory, arg)), expected, StringComparison.OrdinalIgnoreCase));
    }

    private static JavaAppResource Resolve(
        IDistributedApplicationBuilder builder, string repoRoot, params (string Key, object Value)[] block)
    {
        var kind = new JavaLocalResourceKind();
        kind.Validate(ServiceName, repoRoot, Block(block));
        return Assert.IsType<JavaAppResource>(kind.Resolve(builder, ServiceName, repoRoot, Block(block)).Resource);
    }

    private static IDistributedApplicationBuilder CreateBuilder() =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = CreateTempDirectory(),
            Args = [TestBuilderDefaults.DisableConfigReloadArg],
            DisableDashboard = true,
        });

    private static IDistributedApplicationBuilder CreateBuilderThatCanStart(string appHostDirectory)
    {
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [TestBuilderDefaults.DisableConfigReloadArg],
            DisableDashboard = true,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DcpPublisher:CliPath"] = "unused-dcp-path",
            ["DcpPublisher:DashboardPath"] = "unused-dcp-path",
        });
        return builder;
    }

    private static string CreateTempDirectory() => TempDirectories.CreateSubdirectory().FullName;

    private static string WriteFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "");
        return path;
    }

    private static Dictionary<object, object> Block(params (string Key, object Value)[] entries) =>
        entries.ToDictionary(e => (object)e.Key, e => e.Value);
}
