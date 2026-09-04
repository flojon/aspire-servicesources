using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Sources;
using static Aspire.Hosting.ServiceSources.Java.Tests.TestHelpers;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

/// <summary>
/// The case #118 was filed about, end to end through the real <c>java</c> kind: a GraphHopper-shaped
/// repository that commits <c>prepare.sh</c> and gitignores the jar and the routing graph the script
/// produces, so the checkout resolves cleanly and is not runnable until the step has run.
/// </summary>
public class JavaPrepareStepTests
{
    private const string ServiceName = "routing";

    /// <summary>
    /// Clones what the repository actually commits: the bootstrap script and the config it reads,
    /// and neither the jar nor the data directory.
    /// </summary>
    private sealed class FakeGitClient : IGitClient
    {
        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
            Directory.CreateDirectory(Path.Combine(destinationPath, ".git"));
            File.WriteAllText(Path.Combine(destinationPath, "prepare.sh"), "#!/bin/sh\n");
            File.WriteAllText(Path.Combine(destinationPath, "gh-config-local.yml"), "graphhopper:\n");
        }

        public void Checkout(string repositoryPath, string reference)
        {
        }

        public void Fetch(string repositoryPath)
        {
        }

        public bool HasUncommittedChanges(string repositoryPath) => false;

        public bool IsRefCheckedOut(string repositoryPath, string reference) => true;

        public string? GetOriginUrl(string repositoryPath) => null;

        public string? GetHeadCommitSha(string repositoryPath) =>
            "1111111111111111111111111111111111111111";
    }

    /// <summary>
    /// Stands in for <c>prepare.sh</c>: downloads the jar and builds the routing graph, which here
    /// means creating the two things the catalog entry names.
    /// </summary>
    private sealed class FakePrepareRunner(string jarPath) : IPrepareCommandRunner
    {
        public int Runs { get; private set; }

        public int Run(
            string workingDirectory,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken,
            Action<string> onLine)
        {
            Runs++;

            onLine($"Downloading {Path.GetFileName(jarPath)}...");
            File.WriteAllText(Path.Combine(workingDirectory, jarPath), "a jar");

            onLine("Importing sweden-latest.osm.pbf");
            Directory.CreateDirectory(Path.Combine(workingDirectory, "data"));

            return 0;
        }
    }

    private static string CreateAppHostDirectory()
    {
        var dir = CreateTempDirectory();

        File.WriteAllText(
            Path.Combine(dir, "servicesources.yaml"),
            $"services:\n  {ServiceName}:\n    repository: https://github.com/example/routing\n"
            + "    kind: java\n");

        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            $"{{ \"services\": {{ \"{ServiceName}\": {{ \"source\": \"local\" }} }} }}");

        return dir;
    }

    private static ServiceMetadata Metadata(PrepareMetadata? prepare, params (string Key, object Value)[] block) =>
        new()
        {
            Repository = "https://github.com/example/routing",
            Kind = "java",
            Prepare = prepare,
            KindConfig = Block(block),
        };

    private static PrepareMetadata Prepare() => new() { Command = ["./prepare.sh"] };

    private static ServiceDeveloperConfig DevConfig() => new() { Source = "local", Local = new() };

    /// <summary>
    /// The catalog entry from #118, near enough: a jar the repository does not commit, run with the
    /// config it does.
    /// </summary>
    private static (string Key, object Value)[] GraphHopperBlock =>
        [("jarPath", "graphhopper-web-11.0.jar"), ("args", new List<object> { "server", "gh-config-local.yml" }), ("port", 8989)];

    [Fact]
    public void AJavaServiceWhoseJarTheStepProduces_Resolves()
    {
        var dir = CreateAppHostDirectory();
        var builder = CreateBuilder(dir);
        builder.UseJava();

        var runner = new FakePrepareRunner("graphhopper-web-11.0.jar");

        var service = new LocalProjectSource(new FakeGitClient(), runner)
            .Resolve(builder, ServiceName, Metadata(Prepare(), GraphHopperBlock), DevConfig());

        Assert.Equal(1, runner.Runs);
        Assert.Equal(ServiceName, service.Resource.Name);

        // The step ran inside the checkout, so the jar the catalog names is there — which is what
        // "java -jar" is about to be handed.
        var repoRoot = Path.Combine(dir, ".servicesources", "checkouts", ServiceName);
        Assert.True(File.Exists(Path.Combine(repoRoot, "graphhopper-web-11.0.jar")));
        Assert.True(Directory.Exists(Path.Combine(repoRoot, "data")));
    }

    /// <remarks>
    /// The step is what completes the checkout, and the marker is what stops it doing so twice. Both
    /// resolutions are eager, which is the steady state: deferral covers a cold checkout only, so
    /// every repeat run of every step takes this path.
    /// </remarks>
    [Fact]
    public void ASecondResolutionOfAWarmCheckout_DoesNotRepeatTheStep()
    {
        var dir = CreateAppHostDirectory();
        var runner = new FakePrepareRunner("graphhopper-web-11.0.jar");

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var builder = CreateBuilder(dir);
            builder.UseJava();

            new LocalProjectSource(new FakeGitClient(), runner)
                .Resolve(builder, ServiceName, Metadata(Prepare(), GraphHopperBlock), DevConfig());
        }

        Assert.Equal(1, runner.Runs);
    }

    /// <remarks>
    /// What the issue describes happening today: the checkout resolves, and then the service fails
    /// its own working-tree check for missing what the repository is perfectly capable of producing.
    /// The <c>jarPath</c> run mode is the early return from the wrapper check, so what reports it is
    /// the <c>workingDirectory</c> requirement — which the clone above does satisfy, so this case
    /// uses one the checkout does not have.
    /// </remarks>
    [Fact]
    public void WithNoStep_TheKindStillJudgesTheCheckoutItself()
    {
        var dir = CreateAppHostDirectory();
        var builder = CreateBuilder(dir);
        builder.UseJava();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => new LocalProjectSource(new FakeGitClient(), new FakePrepareRunner("app.jar")).Resolve(
                builder,
                ServiceName,
                Metadata(prepare: null, ("jarPath", "app.jar"), ("workingDirectory", "generated"), ("port", 8989)),
                DevConfig()));

        Assert.Contains("workingDirectory", ex.Message);
    }

    /// <remarks>
    /// And the same service with a step that creates that directory resolves — the ordering the
    /// feature exists for, against the real kind rather than a stand-in.
    /// </remarks>
    [Fact]
    public void WithAStepThatCreatesIt_TheSameServiceResolves()
    {
        var dir = CreateAppHostDirectory();
        var builder = CreateBuilder(dir);
        builder.UseJava();

        var service = new LocalProjectSource(new FakeGitClient(), new GeneratingRunner())
            .Resolve(
                builder,
                ServiceName,
                Metadata(Prepare(), ("jarPath", "app.jar"), ("workingDirectory", "generated"), ("port", 8989)),
                DevConfig());

        Assert.Equal(ServiceName, service.Resource.Name);
    }

    /// <summary>A step whose output is the project directory the kind requires.</summary>
    private sealed class GeneratingRunner : IPrepareCommandRunner
    {
        public int Run(
            string workingDirectory,
            IReadOnlyList<string> command,
            CancellationToken cancellationToken,
            Action<string> onLine)
        {
            var generated = Directory.CreateDirectory(Path.Combine(workingDirectory, "generated")).FullName;
            File.WriteAllText(Path.Combine(generated, "app.jar"), "a jar");

            return 0;
        }
    }
}
