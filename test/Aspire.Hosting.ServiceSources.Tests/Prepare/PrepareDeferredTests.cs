using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests.Prepare;

/// <summary>
/// The step on the deferred path: it follows the checkout rather than composition, and lands between
/// the clone and the kind's own post-clone checks. This is the run that matters most — deferral
/// covers a cold managed checkout, so the one run it carries is the first, the expensive one.
/// </summary>
public class PrepareDeferredTests
{
    private const string KindName = "stand-in";

    private sealed class FakeGitClient : IGitClient
    {
        private readonly Dictionary<string, ManualResetEventSlim> _blockUntil = new(StringComparer.Ordinal);

        public List<string> Cloned { get; } = [];

        public ManualResetEventSlim BlockFor(string repositoryUrl) =>
            _blockUntil[repositoryUrl] = new ManualResetEventSlim(false);

        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
            lock (Cloned)
            {
                Cloned.Add(repositoryUrl);
            }

            if (_blockUntil.TryGetValue(repositoryUrl, out var gate))
            {
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            Directory.CreateDirectory(Path.Combine(destinationPath, ".git"));
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

        public string? GetHeadCommitSha(string repositoryPath) =>
            "1111111111111111111111111111111111111111";
    }

    /// <summary>
    /// Records the order things happened in, which is what these tests are about.
    /// </summary>
    private sealed class Journal
    {
        private readonly List<string> _entries = [];

        public void Add(string entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_entries)
                {
                    return [.. _entries];
                }
            }
        }
    }

    private sealed class FakeRunner(Journal journal, string? produces = null) : IPrepareCommandRunner
    {
        public int ExitCode { get; set; }

        /// <summary>Held open until the returned gate is set, so two steps can be caught overlapping.</summary>
        public ManualResetEventSlim? Gate { get; set; }

        public int Runs;

        public int Run(string workingDirectory, IReadOnlyList<string> command, Action<string> onLine)
        {
            Interlocked.Increment(ref Runs);
            journal.Add($"prepare:{Path.GetFileName(workingDirectory)}");

            onLine("bootstrapping");

            Gate?.Wait(TimeSpan.FromSeconds(30));

            if (produces is not null && ExitCode == 0)
            {
                File.WriteAllText(Path.Combine(workingDirectory, produces), "produced");
            }

            return ExitCode;
        }
    }

    /// <summary>
    /// A kind shaped like java's: it builds its whole resource from the catalog and hands back the
    /// one check that needs the working tree — which here looks for the artifact the step produces.
    /// </summary>
    private sealed class StandInKind(Journal journal, string requires, bool withHelper = false)
        : ILocalResourceKind
    {
        public bool SupportsDeferredCheckout(object? rawConfig) => true;

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            Add(builder, serviceName, repoRoot);

        public DeferredLocalResource? ResolveDeferred(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            new()
            {
                Service = Add(builder, serviceName, repoRoot),
                ValidateCheckout = () =>
                {
                    journal.Add($"validate:{serviceName}");

                    if (!File.Exists(Path.Combine(repoRoot, requires)))
                    {
                        throw new ServiceSourcesConfigurationException(
                            $"Service '{serviceName}': '{requires}' is not in the checkout.");
                    }
                },
            };

        private IResourceBuilder<IResourceWithServiceDiscovery> Add(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot)
        {
            // The shape Aspire.Hosting.JavaScript's `npm install` installer puts core in: a resource
            // core starts ahead of the service, and one a prepare step is entitled to have generated
            // the input for.
            var helper = withHelper
                ? builder.AddExecutable($"{serviceName}-installer", "install", repoRoot)
                : null;

            var service = builder
                .AddResource(new StandInResource(serviceName, repoRoot))
                .WithHttpEndpoint(targetPort: 8080);

            if (helper is not null)
            {
                service.WaitFor(helper);
            }

            return service;
        }
    }

    private sealed class StandInResource(string name, string workingDirectory)
        : ExecutableResource(name, "run", workingDirectory), IResourceWithServiceDiscovery;

    private static string CreateAppHostDirectory(params string[] localServices)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        var yaml = string.Join("\n", localServices.Select(name =>
            $"  {name}:\n    repository: https://example.com/{name}.git\n    kind: {KindName}"));
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), $"services:\n{yaml}\n");

        var json = string.Join(",", localServices.Select(name => $"\"{name}\": {{ \"source\": \"local\" }}"));
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), $"{{ \"services\": {{ {json} }} }}");

        return dir;
    }

    private static ServiceMetadata Metadata(string name, PrepareMetadata? prepare = null) =>
        new() { Repository = $"https://example.com/{name}.git", Kind = KindName, Prepare = prepare };

    private static PrepareMetadata Prepare(string? mode = null) =>
        new() { Command = ["./prepare.sh"], Mode = mode };

    private static ServiceDeveloperConfig DevConfig() => new() { Source = "local", Local = new() };

    private static Task PublishNotStartedAsync(IServiceProvider services, IResource resource) =>
        services.GetRequiredService<ResourceNotificationService>()
            .PublishUpdateAsync(resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.NotStarted, null),
            });

    private static async Task<string?> StateOfAsync(
        IServiceProvider services, IResource resource, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);

        await foreach (var @event in services.GetRequiredService<ResourceNotificationService>()
                           .WatchAsync(deadline.Token))
        {
            if (ReferenceEquals(@event.Resource, resource))
            {
                return @event.Snapshot.State?.Text;
            }
        }

        return null;
    }

    [Fact]
    public async Task TheStepRunsAfterTheCloneLands_NotDuringComposition()
    {
        var dir = CreateAppHostDirectory("routing");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var journal = new Journal();
        builder.AddLocalKind(KindName, new StandInKind(journal, "app.jar"));

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/routing.git");
        var runner = new FakeRunner(journal, produces: "app.jar");

        var routing = new LocalProjectSource(git, runner)
            .Resolve(builder, "routing", Metadata("routing", Prepare()), DevConfig());

        // Composition is over and the clone has not even finished, so nothing can have prepared.
        Assert.Equal(0, runner.Runs);

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, routing.Resource);
        gate.Set();

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, runner.Runs);
    }

    /// <remarks>
    /// The ordering constraint the feature rests on, on this path too: the step runs before the kind
    /// is allowed to judge the checkout, and neither kind knows the step exists.
    /// </remarks>
    [Fact]
    public async Task TheStepRunsBeforeTheKindsCheckoutValidation()
    {
        var dir = CreateAppHostDirectory("routing");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var journal = new Journal();
        builder.AddLocalKind(KindName, new StandInKind(journal, "app.jar"));

        var routing = new LocalProjectSource(new FakeGitClient(), new FakeRunner(journal, produces: "app.jar"))
            .Resolve(builder, "routing", Metadata("routing", Prepare()), DevConfig());

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, routing.Resource);

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["prepare:routing", "validate:routing"], journal.Entries);
    }

    /// <remarks>
    /// Before the held-back <em>helpers</em>, not merely before the service: an installer core starts
    /// ahead of the app reads a <c>package.json</c> a prepare step is entitled to generate.
    /// </remarks>
    [Fact]
    public async Task TheStepRunsBeforeTheHeldBackHelpersAreStarted()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var journal = new Journal();
        builder.AddLocalKind(KindName, new StandInKind(journal, "package.json", withHelper: true));

        var frontend = new LocalProjectSource(new FakeGitClient(), new FakeRunner(journal, produces: "package.json"))
            .Resolve(builder, "frontend", Metadata("frontend", Prepare()), DevConfig());

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));

        foreach (var resource in builder.Resources.Where(
                     r => r.Annotations.OfType<ExplicitStartupAnnotation>().Any()))
        {
            await PublishNotStartedAsync(services, resource);
        }

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        // The step is first, before the kind's own check and before anything of this service is
        // asked to start.
        Assert.Equal("prepare:frontend", journal.Entries[0]);
        Assert.Equal("validate:frontend", journal.Entries[1]);
        Assert.Equal("frontend", frontend.Resource.Name);
    }

    /// <remarks>
    /// The same claim from the other side, and the one that pins it rather than inferring it: a
    /// failure reports every resource withheld for the service <em>except the ones that already
    /// started</em>. So the installer reaching <c>FailedToStart</c> is proof it had not been started
    /// when the step failed — where a step running after the helpers would have left it running and
    /// excluded from the report.
    /// </remarks>
    [Fact]
    public async Task AStepThatFails_LeavesTheHeldBackHelperUnstarted()
    {
        var dir = CreateAppHostDirectory("frontend");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var journal = new Journal();
        builder.AddLocalKind(KindName, new StandInKind(journal, "package.json", withHelper: true));

        var frontend = new LocalProjectSource(new FakeGitClient(), new FakeRunner(journal) { ExitCode = 4 })
            .Resolve(builder, "frontend", Metadata("frontend", Prepare()), DevConfig());

        var installer = Assert.Single(builder.Resources, r => r.Name == "frontend-installer");

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));

        await PublishNotStartedAsync(services, installer);
        await PublishNotStartedAsync(services, frontend.Resource);

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(
            KnownResourceStates.FailedToStart,
            await StateOfAsync(services, installer, TimeSpan.FromSeconds(30)));

        // And the kind never judged a checkout the step left incomplete.
        Assert.Equal(["prepare:frontend"], journal.Entries);
    }

    /// <remarks>
    /// #118 asks for "failure is per-service", and this is the one path that can satisfy it
    /// literally: the step runs on a task nobody awaits, long after composition returned.
    /// </remarks>
    [Fact]
    public async Task AFailedStep_SurfacesAsResourceStateRatherThanAnException()
    {
        var dir = CreateAppHostDirectory("routing");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var journal = new Journal();
        builder.AddLocalKind(KindName, new StandInKind(journal, "app.jar"));

        var routing = new LocalProjectSource(new FakeGitClient(), new FakeRunner(journal) { ExitCode = 9 })
            .Resolve(builder, "routing", Metadata("routing", Prepare()), DevConfig());

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, routing.Resource);

        // Nothing throws out of the background task, which would take the host down and undo the
        // isolation deferral exists for.
        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        // The state finally reached, rather than a sequence of intermediate snapshots: a resource
        // watch replays only the current one, which is what made the #189 tests flake.
        Assert.Equal(
            KnownResourceStates.FailedToStart,
            await StateOfAsync(services, routing.Resource, TimeSpan.FromSeconds(30)));

        // The kind never got to judge a checkout the step left incomplete.
        Assert.Equal(["prepare:routing"], journal.Entries);
    }

    /// <remarks>
    /// A failed deferred step is not confined to the dashboard: <c>ServiceStartupFailureNotices</c>
    /// reads resource <em>state</em> rather than any one failure path, so it already covers this one.
    /// </remarks>
    [Fact]
    public async Task AFailedStep_AlsoReachesTheAppHostsOwnConsole()
    {
        var dir = CreateAppHostDirectory("routing");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var notices = TestHelpers.StreamServiceSourcesWarnings(builder);

        var journal = new Journal();
        builder.AddLocalKind(KindName, new StandInKind(journal, "app.jar"));

        var routing = new LocalProjectSource(new FakeGitClient(), new FakeRunner(journal) { ExitCode = 9 })
            .Resolve(builder, "routing", Metadata("routing", Prepare()), DevConfig());

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, routing.Resource);

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var notice = await notices.ReadAsync(timeout.Token);

        Assert.Contains("'routing'", notice);
        Assert.Contains("dashboard", notice);
    }

    /// <remarks>
    /// <c>StartAll</c> awaits none of the tasks it launches, deliberately — waiting there would put
    /// back exactly the block deferral removes. So one service's four-minute import is not the start
    /// of every other deferred service whose checkout landed minutes earlier. What a command has to
    /// tolerate in exchange is running alongside a <em>different</em> service's command; never a
    /// shared working tree, since managed checkouts are per-service clones.
    /// </remarks>
    [Fact]
    public async Task TwoDeferredServices_PrepareWithoutWaitingOnEachOther()
    {
        var dir = CreateAppHostDirectory("routing", "tiles");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var journal = new Journal();
        builder.AddLocalKind(KindName, new StandInKind(journal, "app.jar"));

        var git = new FakeGitClient();

        // One runner per service, so each can be held open independently. The step that is held is
        // the one that would block the other if anything serialized them.
        var held = new ManualResetEventSlim(false);
        var slow = new FakeRunner(journal, produces: "app.jar") { Gate = held };
        var quick = new FakeRunner(journal, produces: "app.jar");

        var routing = new LocalProjectSource(git, slow)
            .Resolve(builder, "routing", Metadata("routing", Prepare()), DevConfig());
        var tiles = new LocalProjectSource(git, quick)
            .Resolve(builder, "tiles", Metadata("tiles", Prepare()), DevConfig());

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, routing.Resource);
        await PublishNotStartedAsync(services, tiles.Resource);

        // The quick service's step finishes while the slow one is still running, which is the whole
        // claim: nothing gates one on the other.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (quick.Runs == 0)
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, deadline.Token);
        }

        Assert.Equal(1, slow.Runs);

        held.Set();
        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        // Each service prepared exactly once: a service never races itself, whatever two services do
        // alongside each other.
        Assert.Equal(1, slow.Runs);
        Assert.Equal(1, quick.Runs);
    }

    /// <remarks>
    /// The warm case, which is every start after the first: deferral is refused for a checkout that
    /// already exists, so every re-run of every step — an <c>oncePerCommit</c> step running again
    /// because <c>ref</c> moved, every start of an <c>always</c> step — takes the eager path.
    /// </remarks>
    [Fact]
    public void AWarmCheckout_PreparesEagerly()
    {
        var dir = CreateAppHostDirectory("routing");
        var repoRoot = Path.Combine(dir, ".servicesources", "checkouts", "routing");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        File.WriteAllText(Path.Combine(repoRoot, "app.jar"), "already built");

        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var journal = new Journal();
        builder.AddLocalKind(KindName, new StandInKind(journal, "app.jar"));

        var runner = new FakeRunner(journal);
        new LocalProjectSource(new FakeGitClient(), runner)
            .Resolve(builder, "routing", Metadata("routing", Prepare("always")), DevConfig());

        // During composition, before anything was deferred.
        Assert.Equal(1, runner.Runs);
        Assert.Empty(DeferredCheckout.For(builder).StartTasks);
    }
}
