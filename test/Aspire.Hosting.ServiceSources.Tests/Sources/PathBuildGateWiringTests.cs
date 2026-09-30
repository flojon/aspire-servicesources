using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

[Trait("IO", "true")]
public class PathBuildGateWiringTests
{
    private sealed class RecordingRunner : IBuildRunner
    {
        public List<(string Project, string? Configuration)> Builds { get; } = [];

        public Task<int> RunAsync(
            string projectFile, string? configuration, Action<string> onLine, CancellationToken cancellationToken)
        {
            lock (Builds)
            {
                Builds.Add((projectFile, configuration));
            }

            return Task.FromResult(0);
        }
    }

    private sealed class StandInKind : ILocalResourceKind
    {
        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            builder.AddResource(new StandInResource(serviceName, repoRoot)).WithHttpEndpoint(targetPort: 8080);
    }

    private sealed class StandInResource(string name, string workingDirectory)
        : ExecutableResource(name, "run", workingDirectory), IResourceWithServiceDiscovery;

    private static ServiceDefinition Definition(
        string path, string? buildGroup = null, string kind = LocalKinds.Dotnet, string project = "Api.csproj") =>
        new ServiceMetadata { Path = path, Project = project, Kind = kind, BuildGroup = buildGroup }
            .ToDefinition("servicesources.yaml", "svc", TestHelpers.EmptyRepositories);

    private static ServiceDeveloperConfig DevConfig() => new() { Source = "path" };

    // Two services side by side in one git repository, the AppHost beside them.
    private static string Repository(out string[] serviceDirs, int services = 2)
    {
        var root = TempDirectories.CreateSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var appHost = Directory.CreateDirectory(Path.Combine(root, "AppHost")).FullName;
        serviceDirs = new string[services];

        for (var i = 0; i < services; i++)
        {
            serviceDirs[i] = Directory.CreateDirectory(Path.Combine(root, $"svc{i}")).FullName;
            File.WriteAllText(Path.Combine(serviceDirs[i], "Api.csproj"), "<Project />");
        }

        return appHost;
    }

    private static Task Start(IDistributedApplicationBuilder builder, IResource resource) =>
        builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource, builder.Services.BuildServiceProvider()));

    private static ProjectResource Project(IDistributedApplicationBuilder builder, string name) =>
        Assert.IsAssignableFrom<ProjectResource>(Assert.Single(builder.Resources, r => r.Name == name));

    [Fact]
    public async Task RunMode_TwoDotnetServicesInOneRepo_ShareAKeyAndTheRealProjectRunsTheBuild()
    {
        var appHost = Repository(out _);
        var builder = TestHelpers.CreateBuilder(appHost);
        var runner = new RecordingRunner();
        var source = new PathSource(buildRunner: runner);

        source.Resolve(builder, "a", Definition("../svc0"), DevConfig());
        source.Resolve(builder, "b", Definition("../svc1"), DevConfig());

        var key = BuildGroupKey.For(Path.Combine(appHost, "..", "svc0"), null, () => null);
        Assert.Equal(2, PathBuildGate.For(builder).MemberCount(key));

        await Start(builder, Project(builder, "a"));

        Assert.Equal(Path.Combine(Path.GetFullPath(Path.Combine(appHost, "..", "svc0")), "Api.csproj"), Assert.Single(runner.Builds).Project);
    }

    [Fact]
    public async Task PublishingTheEventForTheServiceFacade_RunsNothing()
    {
        var appHost = Repository(out _);
        var builder = TestHelpers.CreateBuilder(appHost);
        var runner = new RecordingRunner();
        var source = new PathSource(buildRunner: runner);

        var facade = source.Resolve(builder, "a", Definition("../svc0"), DevConfig());
        source.Resolve(builder, "b", Definition("../svc1"), DevConfig());

        await Start(builder, facade.Resource);

        Assert.Empty(runner.Builds);
    }

    [Fact]
    public async Task TheBuildConfiguration_IsTheAppHostsOwn()
    {
        var appHost = Repository(out _);
        var builder = TestHelpers.CreateBuilder(appHost);
        var runner = new RecordingRunner();
        var source = new PathSource(buildRunner: runner);

        source.Resolve(builder, "a", Definition("../svc0"), DevConfig());
        source.Resolve(builder, "b", Definition("../svc1"), DevConfig());
        await Start(builder, Project(builder, "a"));

        Assert.Equal(PathSource.ConfigurationOf(Assembly.GetEntryAssembly()), Assert.Single(runner.Builds).Configuration);
    }

    [Fact]
    public void ConfigurationOf_ReadsTheAssemblyConfigurationAttribute()
    {
        var assembly = typeof(PathSource).Assembly;
        var expected = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;

        Assert.Equal(string.IsNullOrEmpty(expected) ? null : expected, PathSource.ConfigurationOf(assembly));
        Assert.Null(PathSource.ConfigurationOf(null));
    }

    [Fact]
    public async Task ExplicitBuildGroup_JoinsServicesInDifferentRepositories()
    {
        var one = Repository(out _);
        var two = Repository(out _);
        var builder = TestHelpers.CreateBuilder(one);
        var runner = new RecordingRunner();
        var source = new PathSource(buildRunner: runner);
        var other = Path.GetFullPath(Path.Combine(two, "..", "svc0"));

        source.Resolve(builder, "a", Definition("../svc0", buildGroup: "web"), DevConfig());
        source.Resolve(builder, "b", Definition(null!, buildGroup: "web"), new ServiceDeveloperConfig
        {
            Source = "path",
            Path = new() { Path = other },
        });

        await Start(builder, Project(builder, "a"));

        Assert.Single(runner.Builds);
    }

    [Fact]
    public async Task ExplicitBuildGroupOnOneOfTwoSameRepoServices_SplitsThem()
    {
        var appHost = Repository(out _);
        var builder = TestHelpers.CreateBuilder(appHost);
        var runner = new RecordingRunner();
        var source = new PathSource(buildRunner: runner);

        source.Resolve(builder, "a", Definition("../svc0", buildGroup: "web"), DevConfig());
        source.Resolve(builder, "b", Definition("../svc1"), DevConfig());
        await Start(builder, Project(builder, "a"));
        await Start(builder, Project(builder, "b"));

        Assert.Empty(runner.Builds);
    }

    [Fact]
    public async Task NonDotnetKind_RegistersAndSubscribesNothing()
    {
        var appHost = Repository(out _);
        var builder = TestHelpers.CreateBuilder(appHost);
        builder.AddLocalKind("standin", new StandInKind());
        var runner = new RecordingRunner();
        var source = new PathSource(buildRunner: runner);

        source.Resolve(builder, "a", Definition("../svc0", kind: "standin"), DevConfig());
        source.Resolve(builder, "b", Definition("../svc1", kind: "standin"), DevConfig());

        var key = BuildGroupKey.For(Path.Combine(appHost, "..", "svc0"), null, () => null);
        Assert.Equal(0, PathBuildGate.For(builder).MemberCount(key));

        await Start(builder, Assert.Single(builder.Resources, r => r.Name == "a"));
        Assert.Empty(runner.Builds);
    }

    [Fact]
    public async Task PublishMode_RegistersAndSubscribesNothing()
    {
        var appHost = Repository(out _);
        var builder = TestHelpers.CreatePublishingBuilder(appHost);
        var runner = new RecordingRunner();
        var source = new PathSource(buildRunner: runner);

        source.Resolve(builder, "a", Definition("../svc0"), DevConfig());
        source.Resolve(builder, "b", Definition("../svc1"), DevConfig());

        var key = BuildGroupKey.For(Path.Combine(appHost, "..", "svc0"), null, () => null);
        Assert.Equal(0, PathBuildGate.For(builder).MemberCount(key));

        await Start(builder, Project(builder, "a"));
        Assert.Empty(runner.Builds);
    }

    [Fact]
    public async Task GroupedProjects_KeepTheirNormalRunArguments()
    {
        var appHost = Repository(out _);
        var builder = TestHelpers.CreateBuilder(appHost);
        var source = new PathSource(buildRunner: new RecordingRunner());

        source.Resolve(builder, "a", Definition("../svc0"), DevConfig());
        source.Resolve(builder, "b", Definition("../svc1"), DevConfig());
        await Start(builder, Project(builder, "a"));

        // The gated build leaves the run itself alone: no --no-build or any other argument is added.
        Assert.Empty(Project(builder, "a").Annotations.OfType<CommandLineArgsCallbackAnnotation>());
    }
}