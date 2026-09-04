using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Prepare;

/// <summary>
/// Where the step runs when the checkout is resolved during composition: after the working tree is
/// complete and reconciled, and before the kind is allowed to judge it.
/// </summary>
public class PrepareEagerPathTests
{
    private const string KindName = "stand-in";

    /// <summary>
    /// Clones a checkout that is deliberately <em>incomplete</em>: it holds what the repository
    /// commits and not the artifact the prepare step exists to produce.
    /// </summary>
    private sealed class FakeGitClient : IGitClient
    {
        public string? HeadCommitSha { get; set; } = "1111111111111111111111111111111111111111";

        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "prepare.sh"), "#!/bin/sh\n");
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

        public string? GetHeadCommitSha(string repositoryPath) => HeadCommitSha;
    }

    /// <summary>
    /// A runner that writes <paramref name="produces"/> into the checkout, which is the whole point
    /// of a bootstrap: the file the kind is about to look for does not exist until it has run.
    /// </summary>
    private sealed class FakeRunner(string? produces = null) : IPrepareCommandRunner
    {
        public List<string> RanIn { get; } = [];

        public List<string> SawInCheckout { get; } = [];

        public int ExitCode { get; set; }

        public int Run(string workingDirectory, IReadOnlyList<string> command, Action<string> onLine)
        {
            RanIn.Add(workingDirectory);
            SawInCheckout.AddRange(
                Directory.EnumerateFiles(workingDirectory).Select(Path.GetFileName).OfType<string>());

            onLine("bootstrapping");

            if (produces is not null && ExitCode == 0)
            {
                File.WriteAllText(Path.Combine(workingDirectory, produces), "produced by the prepare step");
            }

            return ExitCode;
        }
    }

    /// <summary>
    /// A kind that checks the working tree in <see cref="Validate"/>, the way the java kind checks
    /// its wrapper and its working directory — so a step that had not run yet would fail it.
    /// </summary>
    private sealed class StandInKind(string requires) : ILocalResourceKind
    {
        public int ValidateCalls { get; private set; }

        public void Validate(string serviceName, string repoRoot, object? rawConfig)
        {
            ValidateCalls++;

            if (!File.Exists(Path.Combine(repoRoot, requires)))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': '{requires}' is not in the checkout at '{repoRoot}'.");
            }
        }

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            builder
                .AddResource(new StandInResource(serviceName, repoRoot))
                .WithHttpEndpoint(targetPort: 8080);
    }

    private sealed class StandInResource(string name, string workingDirectory)
        : ExecutableResource(name, "run", workingDirectory), IResourceWithServiceDiscovery;

    /// <param name="checkoutPath">
    /// A <c>local.path</c> override to write into the developer config. It has to be in the file and
    /// not only in the entry handed to <c>Resolve</c>, because the speculative prefetch reads the
    /// file: a service the file leaves managed is cloned there before this call, and the checkout it
    /// started is the one resolution then uses.
    /// </param>
    private static string CreateAppHostDirectory(
        string serviceName, string kind = KindName, string? checkoutPath = null)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(
            Path.Combine(dir, "servicesources.yaml"),
            $"services:\n  {serviceName}:\n    repository: https://example.com/{serviceName}.git\n"
            + $"    project: Service.csproj\n    kind: {kind}\n");

        var local = checkoutPath is null
            ? ""
            : $", \"local\": {{ \"path\": {System.Text.Json.JsonSerializer.Serialize(checkoutPath)} }}";

        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            $"{{ \"services\": {{ \"{serviceName}\": {{ \"source\": \"local\"{local} }} }} }}");

        return dir;
    }

    private static ServiceMetadata Metadata(
        string name, string kind = KindName, string project = "Service.csproj", PrepareMetadata? prepare = null) =>
        new()
        {
            Repository = $"https://example.com/{name}.git",
            Project = project,
            Kind = kind,
            Prepare = prepare,
        };

    private static PrepareMetadata Prepare(params string[] command) =>
        new() { Command = command.Length == 0 ? ["./prepare.sh"] : command };

    private static ServiceDeveloperConfig DevConfig(
        string? path = null, PrepareDeveloperConfig? prepare = null) =>
        new() { Source = "local", Local = new() { Path = path, Prepare = prepare } };

    [Fact]
    public void TheStepRunsInTheResolvedCheckout_AfterTheCloneLanded()
    {
        var dir = CreateAppHostDirectory("routing");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.AddLocalKind(KindName, new StandInKind("app.jar"));

        var runner = new FakeRunner(produces: "app.jar");
        new LocalProjectSource(new FakeGitClient(), runner)
            .Resolve(builder, "routing", Metadata("routing", prepare: Prepare()), DevConfig());

        Assert.Equal(
            Path.Combine(dir, ".servicesources", "checkouts", "routing"),
            Assert.Single(runner.RanIn));

        // The clone had already landed: the committed script was there for the step to run.
        Assert.Contains("prepare.sh", runner.SawInCheckout);
    }

    /// <remarks>
    /// The ordering the whole feature rests on. Without the step the kind rejects the checkout for
    /// missing precisely the file the step was about to produce, which is the failure #118 describes.
    /// </remarks>
    [Fact]
    public void TheStepRunsBeforeTheKindJudgesTheCheckout()
    {
        var dir = CreateAppHostDirectory("routing");
        var builder = TestHelpers.CreateBuilder(dir);
        var kind = new StandInKind("app.jar");
        builder.AddLocalKind(KindName, kind);

        var service = new LocalProjectSource(new FakeGitClient(), new FakeRunner(produces: "app.jar"))
            .Resolve(builder, "routing", Metadata("routing", prepare: Prepare()), DevConfig());

        Assert.Equal(1, kind.ValidateCalls);
        Assert.Equal("routing", service.Resource.Name);
    }

    [Fact]
    public void WithNoStep_TheKindRejectsTheCheckoutTheStepWouldHaveCompleted()
    {
        var dir = CreateAppHostDirectory("routing");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.AddLocalKind(KindName, new StandInKind("app.jar"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => new LocalProjectSource(new FakeGitClient(), new FakeRunner())
                .Resolve(builder, "routing", Metadata("routing"), DevConfig()));

        Assert.Contains("'app.jar' is not in the checkout", ex.Message);
    }

    /// <remarks>
    /// <c>ResolveProjectFile</c> is the <c>dotnet</c> kind's equivalent check, in the same position,
    /// so a repository that generates its own <c>.csproj</c> is served by the same ordering.
    /// </remarks>
    [Fact]
    public void ADotnetServiceWhoseProjectFileTheStepProduces_Resolves()
    {
        var dir = CreateAppHostDirectory("orders", kind: LocalKinds.Dotnet);
        var builder = TestHelpers.CreateBuilder(dir);

        var service = new LocalProjectSource(new FakeGitClient(), new FakeRunner(produces: "Generated.csproj"))
            .Resolve(
                builder,
                "orders",
                Metadata("orders", LocalKinds.Dotnet, "Generated.csproj", Prepare()),
                DevConfig());

        Assert.Equal("orders", service.Resource.Name);
    }

    [Fact]
    public void AFailedStep_FailsCompositionNamingTheService()
    {
        var dir = CreateAppHostDirectory("routing");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.AddLocalKind(KindName, new StandInKind("app.jar"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => new LocalProjectSource(new FakeGitClient(), new FakeRunner { ExitCode = 3 })
                .Resolve(builder, "routing", Metadata("routing", prepare: Prepare()), DevConfig()));

        Assert.Contains("'routing'", ex.Message);
        Assert.Contains("prepare step failed", ex.Message);
        Assert.Contains("code 3", ex.Message);
    }

    // ---- path checkouts -----------------------------------------------------

    /// <summary>
    /// A checkout the developer manages, already holding what the kind needs — so nothing about the
    /// assertions below depends on a step having run.
    /// </summary>
    private static string CreateOwnCheckout(string requires = "app.jar")
    {
        var checkout = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(checkout, requires), "already built");
        File.WriteAllText(Path.Combine(checkout, "prepare.sh"), "#!/bin/sh\n");
        return checkout;
    }

    [Fact]
    public async Task APathService_RunsNoCatalogStepAndSaysWhichCommandWasNotRun()
    {
        var checkout = CreateOwnCheckout();
        var dir = CreateAppHostDirectory("routing", checkoutPath: checkout);
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.AddLocalKind(KindName, new StandInKind("app.jar"));

        var runner = new FakeRunner();
        new LocalProjectSource(new FakeGitClient(), runner).Resolve(
            builder, "routing", Metadata("routing", prepare: Prepare("./prepare.sh", "--full")),
            DevConfig(path: checkout));

        Assert.Empty(runner.RanIn);

        var notices = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var notice = Assert.Single(notices, message => message.Contains("prepare"));
        Assert.Contains("'routing'", notice);
        Assert.Contains("\"./prepare.sh\", \"--full\"", notice);
    }

    [Fact]
    public async Task APathServiceWithItsOwnBlock_RunsItAndSaysNothing()
    {
        var checkout = CreateOwnCheckout();
        var dir = CreateAppHostDirectory("routing", checkoutPath: checkout);
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.AddLocalKind(KindName, new StandInKind("app.jar"));

        var runner = new FakeRunner();
        new LocalProjectSource(new FakeGitClient(), runner).Resolve(
            builder, "routing", Metadata("routing", prepare: Prepare()),
            DevConfig(path: checkout, prepare: new() { Command = ["make", "bootstrap"] }));

        Assert.Equal(checkout, Assert.Single(runner.RanIn));

        var notices = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);
        Assert.DoesNotContain(notices, message => message.Contains("prepare"));
    }

    /// <remarks>
    /// Any declared block silences the notice, <c>{"mode": "never"}</c> included — which is the one
    /// mode that means something without a command.
    /// </remarks>
    [Fact]
    public async Task APathServiceDeclaringModeNever_RunsNothingAndSaysNothing()
    {
        var checkout = CreateOwnCheckout();
        var dir = CreateAppHostDirectory("routing", checkoutPath: checkout);
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.AddLocalKind(KindName, new StandInKind("app.jar"));

        var runner = new FakeRunner();
        new LocalProjectSource(new FakeGitClient(), runner).Resolve(
            builder, "routing", Metadata("routing", prepare: Prepare()),
            DevConfig(path: checkout, prepare: new() { Mode = "never" }));

        Assert.Empty(runner.RanIn);

        var notices = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);
        Assert.DoesNotContain(notices, message => message.Contains("prepare"));
    }

    /// <remarks>
    /// The tool directory is acquired at the point of use. An AppHost whose services all use
    /// <c>path</c> and declare no step has nothing to keep there and should never grow one.
    /// </remarks>
    [Fact]
    public void APathOnlyAppHost_GetsTheToolDirectoryOnlyWhenAStepRuns()
    {
        var checkout = CreateOwnCheckout();
        var withoutStep = CreateAppHostDirectory("routing", checkoutPath: checkout);
        var withStep = CreateAppHostDirectory("routing", checkoutPath: checkout);

        foreach (var (dir, prepare) in new[]
                 {
                     (withoutStep, (PrepareDeveloperConfig?)null),
                     (withStep, new PrepareDeveloperConfig { Command = ["make", "bootstrap"] }),
                 })
        {
            var builder = TestHelpers.CreateBuilder(dir);
            builder.AddLocalKind(KindName, new StandInKind("app.jar"));

            new LocalProjectSource(new FakeGitClient(), new FakeRunner()).Resolve(
                builder, "routing", Metadata("routing"), DevConfig(path: checkout, prepare: prepare));
        }

        Assert.False(Directory.Exists(Path.Combine(withoutStep, ".servicesources")));
        Assert.True(File.Exists(Path.Combine(withStep, ".servicesources", ".gitignore")));
    }
}
