using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// Deferring a cold <c>"local"</c> checkout past startup (#130): the AppHost reaches the dashboard
/// while the clone is still running, and the service starts when its checkout lands.
/// </summary>
public class DeferredCheckoutTests
{
    private sealed class FakeGitClient : IGitClient
    {
        private readonly Dictionary<string, ManualResetEventSlim> _blockUntil = new(StringComparer.Ordinal);

        private readonly Dictionary<string, Exception> _failFor = new(StringComparer.Ordinal);

        public List<string> Cloned { get; } = [];

        /// <summary>Holds this repository's clone open until the returned gate is set.</summary>
        public ManualResetEventSlim BlockFor(string repositoryUrl) =>
            _blockUntil[repositoryUrl] = new ManualResetEventSlim(false);

        public void FailFor(string repositoryUrl, Exception exception) => _failFor[repositoryUrl] = exception;

        public void Clone(string repositoryUrl, string destinationPath)
        {
            lock (Cloned)
            {
                Cloned.Add(repositoryUrl);
            }

            if (_blockUntil.TryGetValue(repositoryUrl, out var gate))
            {
                gate.Wait(TimeSpan.FromSeconds(30));
            }

            if (_failFor.TryGetValue(repositoryUrl, out var exception))
            {
                throw exception;
            }

            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "Service.csproj"), "<Project />");
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
    }

    private static string CreateAppHostDirectory(params string[] localServices)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        var yaml = string.Join("\n", localServices.Select(name =>
            $"  {name}:\n    repository: https://example.com/{name}.git\n    project: Service.csproj"));
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), $"services:\n{yaml}\n");

        var json = string.Join(",", localServices.Select(name => $"\"{name}\": {{ \"source\": \"local\" }}"));
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), $"{{ \"services\": {{ {json} }} }}");

        return dir;
    }

    private static ServiceMetadata Metadata(string name, string project = "Service.csproj") =>
        new() { Repository = $"https://example.com/{name}.git", Project = project };

    private static ServiceDeveloperConfig DevConfig(string? path = null) => new() { Source = "local", Path = path };

    private static string ExpectedRepoRoot(string appHostDirectory, string serviceName) =>
        Path.Combine(appHostDirectory, ".servicesources", "checkouts", serviceName);

    /// <summary>
    /// Plants a checkout that already exists — the warm case, which deferral must leave alone.
    /// </summary>
    private static string PlantExistingCheckout(string appHostDirectory, string serviceName)
    {
        var repoRoot = ExpectedRepoRoot(appHostDirectory, serviceName);
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        File.WriteAllText(Path.Combine(repoRoot, "Service.csproj"), "<Project />");

        return repoRoot;
    }

    private static bool IsDeferred(IResource resource) =>
        resource.Annotations.OfType<ExplicitStartupAnnotation>().Any();

    [Fact]
    public void WithoutOptIn_ColdCheckout_StillResolvesEagerly()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilder(dir);

        var service = new LocalProjectSource(new FakeGitClient()).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        // Default off: the behaviour change is user-visible, so nobody gets it without asking.
        Assert.False(IsDeferred(service.Resource));
        Assert.True(File.Exists(Path.Combine(ExpectedRepoRoot(dir, "orders"), "Service.csproj")));
    }

    [Fact]
    public void OptedIn_ColdCheckout_ReturnsWhileTheCloneIsStillRunning()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/orders.git");

        // Composition runs to completion with the clone deliberately wedged open. Under the eager
        // path this call would sit here until the gate was released — which is the whole complaint.
        var service = new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        var repoRoot = ExpectedRepoRoot(dir, "orders");

        Assert.True(IsDeferred(service.Resource));
        Assert.IsAssignableFrom<ProjectResource>(service.Resource);
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, service.Resource));
        Assert.False(Directory.Exists(repoRoot), "the checkout should not exist yet");

        // The path DCP will freeze into the executable spec at startup, named before the clone that
        // fills it has finished.
        var metadata = Assert.Single(service.Resource.Annotations.OfType<IProjectMetadata>());
        Assert.IsType<DeferredProjectMetadata>(metadata);
        Assert.Equal(Path.Combine(repoRoot, "Service.csproj"), metadata.ProjectPath);

        gate.Set();
    }

    [Fact]
    public void OptedIn_WarmCheckout_ResolvesEagerlyWithFullLaunchProfileFidelity()
    {
        var dir = CreateAppHostDirectory("orders");
        PlantExistingCheckout(dir, "orders");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        var service = new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        // Every run after the first. Nothing to wait for, so nothing to defer — and the eager path
        // is the one that reads the repository's own launchSettings.json during composition.
        Assert.False(IsDeferred(service.Resource));
        Assert.Empty(git.Cloned);
    }

    [Fact]
    public void OptedIn_PathOverride_ResolvesEagerly()
    {
        var dir = CreateAppHostDirectory("orders");
        var checkout = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(checkout, "Service.csproj"), "<Project />");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var service = new LocalProjectSource(new FakeGitClient())
            .Resolve(builder, "orders", Metadata("orders"), DevConfig(path: checkout));

        // 'path' is the developer's own directory: there is no clone to wait for, and this package
        // is not entitled to create anything at that path if it is missing.
        Assert.False(IsDeferred(service.Resource));
    }

    [Fact]
    public void AspiresOwnAddProject_StillRejectsAProjectFileThatDoesNotExist()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilder(dir);
        var missing = Path.Combine(ExpectedRepoRoot(dir, "orders"), "Service.csproj");

        // The constraint this whole class is built around, asserted rather than assumed: AddProject
        // reads the project's launch settings during composition, and that read does File.Exists on
        // the project path. If a future Aspire stops throwing here, DeferredProjectMetadata and the
        // hand-assembled AddResource + WithProjectDefaults it exists for can both go away.
        Assert.ThrowsAny<Exception>(() => builder.AddProject("orders", missing));
    }

    [Fact]
    public void OptedIn_ColdCheckout_BuildsTheApplicationWithAProjectFileThatDoesNotExistYet()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/orders.git");
        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig()).WithHttpEndpoint();

        // The claim the whole design rests on: Aspire will carry a ProjectResource whose .csproj is
        // not on disk all the way through composition. AddProject(name, missingPath) will not — it
        // throws out of WithProjectDefaults, via a launch-settings read that does File.Exists on the
        // project path — which is why the resource is assembled from AddResource +
        // DeferredProjectMetadata + WithProjectDefaults instead.
        Assert.False(File.Exists(Path.Combine(ExpectedRepoRoot(dir, "orders"), "Service.csproj")));

        using var app = builder.Build();

        gate.Set();
    }

    [Fact]
    public async Task DeferredServiceWithoutDeclaredEndpoints_FailsAtBeforeStartEventNamingTheFix()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/orders.git");
        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => TestHelpers.PublishBeforeStartEventAsync(builder));

        // A deferred project has no launch profile to take endpoints from, and nothing re-runs that
        // step after the checkout lands, so a service that declares none would come up unreachable.
        Assert.Contains("orders", ex.Message);
        Assert.Contains("WithHttpEndpoint", ex.Message);

        gate.Set();
    }

    [Fact]
    public async Task DeferredServiceWithDeclaredEndpoint_PassesBeforeStartEvent()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/orders.git");

        // The line an AppHost adds to opt a service into deferral — and the same line is correct on
        // a warm checkout, where every argument is null and the existing endpoint is left alone.
        new LocalProjectSource(git)
            .Resolve(builder, "orders", Metadata("orders"), DevConfig())
            .WithHttpEndpoint();

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        gate.Set();
    }

    [Fact]
    public void DeferredService_IsNotReportedAsAPrefetchNobodyAskedFor()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilder(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/orders.git");
        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        // The prefetch decides what to report at BeforeStartEvent, before a deferred service has
        // waited on its checkout. Without MarkRequested it would name this one as speculative work
        // the AppHost never wanted.
        Assert.Null(LocalCheckoutPrefetch.For(builder, git).UnusedCheckoutsMessage);

        gate.Set();
    }

    [Fact]
    public async Task AfterResourcesCreated_RunsTheCheckoutThatCompositionSkipped()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/orders.git");
        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig()).WithHttpEndpoint();

        var repoRoot = ExpectedRepoRoot(dir, "orders");
        Assert.False(Directory.Exists(repoRoot));

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new AfterResourcesCreatedEvent(services, new DistributedApplicationModel(builder.Resources)));

        // The event handler hands the wait to a background task and returns, so the host is not held
        // while the clone runs — releasing the gate only now is what proves it.
        gate.Set();

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(File.Exists(Path.Combine(repoRoot, "Service.csproj")));
    }

    [Fact]
    public async Task DeferredCheckoutFailure_DoesNotFaultTheBackgroundTask()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        git.FailFor("https://example.com/orders.git", new InvalidOperationException("no such repo"));
        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig()).WithHttpEndpoint();

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new AfterResourcesCreatedEvent(services, new DistributedApplicationModel(builder.Resources)));

        // A clone that fails after startup costs one service. Reported as resource state and
        // resource logs — never as an exception on a task nobody awaits, which would take the host
        // down and undo the isolation deferral exists for.
        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void DeferredProjectMetadata_MissingCheckout_ReportsEmptyLaunchSettingsRatherThanReadingTheFile()
    {
        var missing = Path.Combine(Directory.CreateTempSubdirectory().FullName, "Service.csproj");

        var launchSettings = new DeferredProjectMetadata(missing).LaunchSettings;

        // Non-null short-circuits the read that throws for a missing .csproj, and empty makes every
        // launch-profile selector decline rather than throwing for a profile that isn't there.
        Assert.NotNull(launchSettings);
        Assert.Empty(launchSettings.Profiles);
    }

    [Fact]
    public void DeferredProjectMetadata_CheckoutHasLanded_DefersToTheRepositorysOwnLaunchSettings()
    {
        var projectPath = Path.Combine(Directory.CreateTempSubdirectory().FullName, "Service.csproj");
        File.WriteAllText(projectPath, "<Project />");

        // Null is how IProjectMetadata says "read Properties/launchSettings.json" — which Aspire
        // does again at start time, in ExecutableCreator.CreateObjectAsync, and therefore after the
        // clone for a resource held back by WithExplicitStart().
        Assert.Null(new DeferredProjectMetadata(projectPath).LaunchSettings);
    }
}
