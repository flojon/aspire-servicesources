using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Tests.Git;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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

        private readonly Dictionary<string, string> _launchSettings = new(StringComparer.Ordinal);

        private readonly Dictionary<string, string[]> _progressLines = new(StringComparer.Ordinal);

        public List<string> Cloned { get; } = [];

        /// <summary>Holds this repository's clone open until the returned gate is set.</summary>
        public ManualResetEventSlim BlockFor(string repositoryUrl) =>
            _blockUntil[repositoryUrl] = new ManualResetEventSlim(false);

        public void FailFor(string repositoryUrl, Exception exception) => _failFor[repositoryUrl] = exception;

        /// <summary>
        /// Gives the cloned repository a <c>Properties/launchSettings.json</c>, which is the file the
        /// AppHost could not read while composing and the whole reason the landed checkout is re-read.
        /// </summary>
        public void WithLaunchSettings(string repositoryUrl, string json) => _launchSettings[repositoryUrl] = json;

        /// <summary>
        /// Progress lines this repository's clone reports, in git's own wording, before it finishes.
        /// Stands in for the stream the real client parses out of git's stderr.
        /// </summary>
        public void ReportProgress(string repositoryUrl, params string[] lines) =>
            _progressLines[repositoryUrl] = lines;

        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
            lock (Cloned)
            {
                Cloned.Add(repositoryUrl);
            }

            if (_progressLines.TryGetValue(repositoryUrl, out var lines))
            {
                Assert.NotNull(progress);

                foreach (var line in lines)
                {
                    progress.Report(line);
                }
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

            if (_launchSettings.TryGetValue(repositoryUrl, out var settings))
            {
                var properties = Directory.CreateDirectory(Path.Combine(destinationPath, "Properties")).FullName;
                File.WriteAllText(Path.Combine(properties, "launchSettings.json"), settings);
            }
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

    private static ServiceDeveloperConfig DevConfig(string? path = null) =>
        new() { Source = "local", Local = new() { Path = path } };

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
    public void OptedIn_PublishMode_ColdCheckout_ResolvesEagerly()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreatePublishingBuilder(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        var service = new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        // Publish mode writes a manifest and exits: no dashboard to reach early, no DCP, no resource
        // lifecycle. A deferred resource there would be described from a .csproj that is not on disk
        // — no launch-profile endpoints, no profile environment — and its start task would wait
        // forever for a NotStarted that only DCP publishes. The clone is worth paying for here.
        Assert.False(builder.ExecutionContext.IsRunMode);
        Assert.False(IsDeferred(service.Resource));
        Assert.True(File.Exists(Path.Combine(ExpectedRepoRoot(dir, "orders"), "Service.csproj")));
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
    public async Task DeferredServiceWithoutDeclaredEndpoints_IsNotRefused()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/orders.git");
        new LocalProjectSource(git).Resolve(builder, "orders", Metadata("orders"), DevConfig());

        // A run-to-completion worker has no applicationUrl on either path, so it cannot declare an
        // endpoint honestly and must not have to. Whether the project actually wanted one is a
        // question the landed checkout answers — see the LaunchProfileEndpointWarning tests — not a
        // reason to refuse the run before the repository is even on disk.
        await TestHelpers.PublishBeforeStartEventAsync(builder);

        gate.Set();
    }

    [Fact]
    public void LaunchProfileEndpointWarning_ProfileDeclaresNoApplicationUrl_SaysNothing()
    {
        var projectFile = WriteProjectWithLaunchProfile(applicationUrl: null);

        // The worker case: no endpoints declared, none wanted, nothing diverges from the warm path.
        Assert.Null(DeferredCheckout.LaunchProfileEndpointWarning(
            "orders", LandedLaunchProfile.Read(projectFile, new ProjectResource("orders")), new ProjectResource("orders")));
    }

    [Fact]
    public void LaunchProfileEndpointWarning_ProfileDeclaresOne_NamesTheUrlAndTheFix()
    {
        var projectFile = WriteProjectWithLaunchProfile("http://localhost:8081");

        var warning = DeferredCheckout.LaunchProfileEndpointWarning(
            "orders", LandedLaunchProfile.Read(projectFile, new ProjectResource("orders")), new ProjectResource("orders"));

        // Reported at the only moment the real URL is knowable, which is why it can quote it.
        Assert.NotNull(warning);
        Assert.Contains("http://localhost:8081", warning);
        Assert.Contains("orders", warning);
        Assert.Contains("WithHttpEndpoint", warning);
    }

    [Fact]
    public void LaunchProfileEndpointWarning_EndpointDeclaredInTheAppHost_SaysNothing()
    {
        var projectFile = WriteProjectWithLaunchProfile("http://localhost:8081");

        var resource = new ProjectResource("orders");
        resource.Annotations.Add(new EndpointAnnotation(ProtocolType.Tcp, name: "http"));

        Assert.Null(DeferredCheckout.LaunchProfileEndpointWarning(
            "orders", LandedLaunchProfile.Read(projectFile, resource), resource));
    }

    [Fact]
    public void LaunchProfileEndpointWarning_NoLaunchSettingsAtAll_SaysNothing()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var projectFile = Path.Combine(dir, "Service.csproj");
        File.WriteAllText(projectFile, "<Project />");

        Assert.Null(DeferredCheckout.LaunchProfileEndpointWarning(
            "orders", LandedLaunchProfile.Read(projectFile, new ProjectResource("orders")), new ProjectResource("orders")));
    }

    [Fact]
    public void LandedLaunchProfile_RecoversTheEnvironmentCompositionCouldNotRead()
    {
        var projectFile = WriteProjectWithLaunchProfile(
            "http://localhost:8081;https://localhost:8443",
            environmentVariables: "\"DOTNET_ENVIRONMENT\": \"Development\", \"FOO\": \"bar\"");

        var profile = LandedLaunchProfile.Read(projectFile, new ProjectResource("orders"));

        // DOTNET_ENVIRONMENT is the one that matters: Host.CreateDefaultBuilder takes the
        // environment name from it, and most repositories set it in the launch profile and nowhere
        // else — so losing it runs a deferred service as Production while every warm run is
        // Development.
        Assert.Equal("Development", profile.EnvironmentVariables["DOTNET_ENVIRONMENT"]);
        Assert.Equal("bar", profile.EnvironmentVariables["FOO"]);

        // applicationUrl is a semicolon-separated list, the shape --urls takes.
        Assert.Equal(["http://localhost:8081", "https://localhost:8443"], profile.ApplicationUrls);
    }

    [Fact]
    public void LandedLaunchProfile_NoLaunchSettings_IsEmptyRatherThanThrowing()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var projectFile = Path.Combine(dir, "Service.csproj");
        File.WriteAllText(projectFile, "<Project />");

        var profile = LandedLaunchProfile.Read(projectFile, new ProjectResource("orders"));

        Assert.Empty(profile.ApplicationUrls);
        Assert.Empty(profile.EnvironmentVariables);
    }

    [Fact]
    public void LandedLaunchProfile_UnparseableFile_IsEmptyRatherThanThrowing()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var projectFile = Path.Combine(dir, "Service.csproj");
        File.WriteAllText(projectFile, "<Project />");
        var properties = Directory.CreateDirectory(Path.Combine(dir, "Properties")).FullName;
        File.WriteAllText(Path.Combine(properties, "launchSettings.json"), "{ not json");

        // This recovers fidelity that would otherwise be silently lost, so failing to recover it
        // must leave the run as it would have been rather than break it.
        var profile = LandedLaunchProfile.Read(projectFile, new ProjectResource("orders"));

        Assert.Empty(profile.ApplicationUrls);
        Assert.Empty(profile.EnvironmentVariables);
    }

    [Fact]
    public void LandedLaunchProfile_SkipsProfilesThatAreNotProjectProfiles()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var projectFile = Path.Combine(dir, "Service.csproj");
        File.WriteAllText(projectFile, "<Project />");
        var properties = Directory.CreateDirectory(Path.Combine(dir, "Properties")).FullName;
        File.WriteAllText(
            Path.Combine(properties, "launchSettings.json"),
            """
            {
              "profiles": {
                "IIS Express": { "commandName": "IISExpress", "applicationUrl": "http://localhost:1111" },
                "Service": { "commandName": "Project", "applicationUrl": "http://localhost:2222" }
              }
            }
            """);

        Assert.Equal(["http://localhost:2222"], LandedLaunchProfile.Read(projectFile, new ProjectResource("orders")).ApplicationUrls);
    }

    [Fact]
    public void LandedLaunchProfile_ProfileWithNoCommandName_IsStillSelected()
    {
        var projectFile = WriteLaunchSettings(
            """
            { "profiles": { "Service": { "applicationUrl": "http://localhost:2222" } } }
            """);

        // Aspire's order selector tests string.IsNullOrEmpty(CommandName) before its allow list, so
        // a profile that omits the property is launchable. Refusing it here would restore no
        // environment at all for a project Aspire runs perfectly well — silently reinstating the
        // DOTNET_ENVIRONMENT loss this whole path exists to prevent.
        var profile = LandedLaunchProfile.Read(projectFile, new ProjectResource("orders"));

        Assert.Equal("Service", profile.Name);
        Assert.Equal(["http://localhost:2222"], profile.ApplicationUrls);
    }

    [Fact]
    public void LandedLaunchProfile_ExecutableProfile_IsStillSelected()
    {
        var projectFile = WriteLaunchSettings(
            """
            { "profiles": { "Service": { "commandName": "Executable", "applicationUrl": "http://localhost:2222" } } }
            """);

        // "Executable" is the other half of Aspire's allow list.
        Assert.Equal("Service", LandedLaunchProfile.Read(projectFile, new ProjectResource("orders")).Name);
    }

    [Fact]
    public void LandedLaunchProfile_DefaultLaunchProfileAnnotation_SelectsThatProfileRatherThanTheFirst()
    {
        var projectFile = WriteLaunchSettings(
            """
            {
              "profiles": {
                "http": { "commandName": "Project", "environmentVariables": { "WHICH": "http" } },
                "https": { "commandName": "Project", "environmentVariables": { "WHICH": "https" } }
              }
            }
            """);

        var resource = new ProjectResource("orders");
        resource.Annotations.Add(new DefaultLaunchProfileAnnotation("https"));

        // WithProjectDefaults stamps this from AppHost:DefaultLaunchProfileName or
        // DOTNET_LAUNCH_PROFILE, which is set whenever the AppHost itself was launched with a
        // profile — the Aspire template's normal case. Aspire selects the named profile when it
        // builds the executable's arguments, after the clone, so taking the first "Project" profile
        // here would hand the process one profile's environment and another's arguments and URLs.
        var profile = LandedLaunchProfile.Read(projectFile, resource);

        Assert.Equal("https", profile.Name);
        Assert.Equal("https", profile.EnvironmentVariables["WHICH"]);
    }

    [Fact]
    public void LandedLaunchProfile_DefaultLaunchProfileAnnotationNamingNothing_FallsBackToOrder()
    {
        var projectFile = WriteLaunchSettings(
            """
            { "profiles": { "Service": { "commandName": "Project", "environmentVariables": { "WHICH": "first" } } } }
            """);

        var resource = new ProjectResource("orders");
        resource.Annotations.Add(new DefaultLaunchProfileAnnotation("no-such-profile"));

        // The default-annotation selector declines when the file has no such profile, and Aspire
        // moves on to the next selector rather than ending with none.
        var profile = LandedLaunchProfile.Read(projectFile, resource);

        Assert.Equal("Service", profile.Name);
        Assert.Equal("first", profile.EnvironmentVariables["WHICH"]);
    }

    [Fact]
    public void LandedLaunchProfile_LaunchProfileAnnotation_WinsOverEverything()
    {
        var projectFile = WriteLaunchSettings(
            """
            {
              "profiles": {
                "http": { "commandName": "Project", "environmentVariables": { "WHICH": "http" } },
                "named": { "commandName": "Project", "environmentVariables": { "WHICH": "named" } }
              }
            }
            """);

        var resource = new ProjectResource("orders");
        resource.Annotations.Add(new DefaultLaunchProfileAnnotation("http"));
        resource.Annotations.Add(new LaunchProfileAnnotation("named"));

        Assert.Equal("named", LandedLaunchProfile.Read(projectFile, resource).EnvironmentVariables["WHICH"]);
    }

    [Fact]
    public void LandedLaunchProfile_LaunchProfileAnnotationNamingNothing_SelectsNothing()
    {
        var projectFile = WriteLaunchSettings(
            """
            { "profiles": { "Service": { "commandName": "Project", "environmentVariables": { "WHICH": "first" } } } }
            """);

        var resource = new ProjectResource("orders");
        resource.Annotations.Add(new LaunchProfileAnnotation("no-such-profile"));

        // Unlike the default annotation, an explicitly named profile does not fall through: Aspire
        // returns the name, fails to find it and ends with no effective profile. Quietly using the
        // first profile instead would apply an environment the warm path never would.
        var profile = LandedLaunchProfile.Read(projectFile, resource);

        Assert.Null(profile.Name);
        Assert.Empty(profile.EnvironmentVariables);
    }

    [Fact]
    public void LandedLaunchProfile_ExcludeLaunchProfileAnnotation_SelectsNothing()
    {
        var projectFile = WriteLaunchSettings(
            """
            { "profiles": { "Service": { "commandName": "Project", "environmentVariables": { "WHICH": "first" } } } }
            """);

        var resource = new ProjectResource("orders");
        resource.Annotations.Add(new ExcludeLaunchProfileAnnotation());

        // The profile is deliberately discarded here, not merely unfound — restoring it anyway
        // would defeat the annotation.
        Assert.Null(LandedLaunchProfile.Read(projectFile, resource).Name);
    }

    private static string WriteLaunchSettings(string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var projectFile = Path.Combine(dir, "Service.csproj");
        File.WriteAllText(projectFile, "<Project />");

        var properties = Directory.CreateDirectory(Path.Combine(dir, "Properties")).FullName;
        File.WriteAllText(Path.Combine(properties, "launchSettings.json"), json);

        return projectFile;
    }

    private static string WriteProjectWithLaunchProfile(
        string? applicationUrl, string? environmentVariables = null)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var projectFile = Path.Combine(dir, "Service.csproj");
        File.WriteAllText(projectFile, "<Project />");

        var properties = Directory.CreateDirectory(Path.Combine(dir, "Properties")).FullName;

        var url = applicationUrl is null
            ? ""
            : ", \"applicationUrl\": \"" + applicationUrl + "\"";

        var env = environmentVariables is null
            ? ""
            : ", \"environmentVariables\": { " + environmentVariables + " }";

        File.WriteAllText(
            Path.Combine(properties, "launchSettings.json"),
            "{ \"profiles\": { \"Service\": { \"commandName\": \"Project\"" + url + env + " } } }");

        return projectFile;
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
    public async Task BeforeStart_RunsTheCheckoutThatCompositionSkipped()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        var gate = git.BlockFor("https://example.com/orders.git");
        var orders = new LocalProjectSource(git)
            .Resolve(builder, "orders", Metadata("orders"), DevConfig())
            .WithHttpEndpoint();

        var repoRoot = ExpectedRepoRoot(dir, "orders");
        Assert.False(Directory.Exists(repoRoot));

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));

        // The event handler hands the wait to a background task and returns, so the host is not held
        // while the clone runs — releasing the gate only now is what proves it.
        await PublishNotStartedAsync(services, orders.Resource);
        gate.Set();

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(File.Exists(Path.Combine(repoRoot, "Service.csproj")));
    }

    [Fact]
    public async Task StartPath_DoesNotDependOnAfterResourcesCreated()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        var orders = new LocalProjectSource(git)
            .Resolve(builder, "orders", Metadata("orders"), DevConfig())
            .WithHttpEndpoint();

        var repoRoot = ExpectedRepoRoot(dir, "orders");

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, orders.Resource);

        // AfterResourcesCreatedEvent is deliberately never published here, because in a real host it
        // may never be: it fires only once every resource has been created, and a resource with an
        // unsatisfied WaitFor annotation is not created until that wait resolves. Anything that
        // WaitFors a deferred service is therefore in front of the event that would start it, and
        // hanging the start off that event deadlocks the graph. Each task waits for its own resource.
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
        var orders = new LocalProjectSource(git)
            .Resolve(builder, "orders", Metadata("orders"), DevConfig())
            .WithHttpEndpoint();

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, orders.Resource);

        // A clone that fails after startup costs one service. Reported as resource state and
        // resource logs — never as an exception on a task nobody awaits, which would take the host
        // down and undo the isolation deferral exists for.
        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task HostShutdownWhileWaitingForDcp_EndsTheStartTaskRatherThanWaitingForever()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        new LocalProjectSource(git)
            .Resolve(builder, "orders", Metadata("orders"), DevConfig())
            .WithHttpEndpoint();

        var services = builder.Services.BuildServiceProvider();

        // No token, which is the token a real AppHost supplies: the template ends in Run(), and
        // that is RunAsync().Wait() with the default. Anything hanging off BeforeStartEvent's own
        // token would never be cancelled at all.
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)),
            CancellationToken.None);

        // Ctrl-C before DCP ever published NotStarted, so the state the task is waiting for is now
        // never coming. ApplicationStopping is the only signal that says so.
        services.GetRequiredService<IHostApplicationLifetime>().StopApplication();

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task LandedProfileEnvironment_IsRestoredExpanded_AlongsideTheProfileName()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        // A real variable to expand against, named uniquely so parallel tests cannot collide on it.
        var marker = "SERVICESOURCES_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(marker, "expanded");

        try
        {
            var git = new FakeGitClient();
            git.WithLaunchSettings(
                "https://example.com/orders.git",
                $$"""
                {
                  "profiles": {
                    "http": {
                      "commandName": "Project",
                      "environmentVariables": {
                        "DOTNET_ENVIRONMENT": "Development",
                        "CERT_PATH": "%{{marker}}%/certs"
                      }
                    }
                  }
                }
                """);

            var orders = new LocalProjectSource(git)
                .Resolve(builder, "orders", Metadata("orders"), DevConfig())
                .WithHttpEndpoint();

            var services = builder.Services.BuildServiceProvider();
            await builder.Eventing.PublishAsync(
                new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
            await PublishNotStartedAsync(services, orders.Resource);

            await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

            // The restore appends its callback after the clone, so it is the last one on the
            // resource — and the only one under test here. The rest belong to WithProjectDefaults.
            var restore = Assert.IsType<EnvironmentCallbackAnnotation>(
                orders.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Last());

            var context = new EnvironmentCallbackContext(
                new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), orders.Resource);
            await restore.Callback(context);

            // The environment Aspire could not read while composing, put back the way
            // WithProjectDefaults puts it back on every warm run.
            Assert.Equal("Development", context.EnvironmentVariables["DOTNET_ENVIRONMENT"]);

            // Expanded, not literal. Aspire runs every profile value through
            // Environment.ExpandEnvironmentVariables, so leaving it raw here would send the child
            // process a different value on the first run than on every run after it.
            Assert.Equal("expanded/certs", context.EnvironmentVariables["CERT_PATH"]);

            // Set for consistency with "dotnet run" and "dotnet watch", as WithProjectDefaults does.
            Assert.Equal("http", context.EnvironmentVariables["DOTNET_LAUNCH_PROFILE"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(marker, null);
        }
    }

    [Fact]
    public async Task LandedProfileEnvironment_NeverOverridesWhatTheAppHostSet()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        git.WithLaunchSettings(
            "https://example.com/orders.git",
            """
            {
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "environmentVariables": { "DOTNET_ENVIRONMENT": "Development" }
                }
              }
            }
            """);

        var orders = new LocalProjectSource(git)
            .Resolve(builder, "orders", Metadata("orders"), DevConfig())
            .WithHttpEndpoint();

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, orders.Resource);

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        var restore = orders.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Last();

        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), orders.Resource);
        context.EnvironmentVariables["DOTNET_ENVIRONMENT"] = "Staging";
        context.EnvironmentVariables["DOTNET_LAUNCH_PROFILE"] = "chosen-by-the-apphost";

        await restore.Callback(context);

        // The profile is the lowest precedence, which is the precedence Aspire gives it: anything
        // the AppHost set explicitly stands.
        Assert.Equal("Staging", context.EnvironmentVariables["DOTNET_ENVIRONMENT"]);
        Assert.Equal("chosen-by-the-apphost", context.EnvironmentVariables["DOTNET_LAUNCH_PROFILE"]);
    }

    [Fact]
    public async Task LandedProfileDeclaringItsOwnLaunchProfileName_LosesToTheSelectedProfile()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        git.WithLaunchSettings(
            "https://example.com/orders.git",
            """
            {
              "profiles": {
                "http": {
                  "commandName": "Project",
                  "environmentVariables": { "DOTNET_LAUNCH_PROFILE": "legacy" }
                }
              }
            }
            """);

        var orders = new LocalProjectSource(git)
            .Resolve(builder, "orders", Metadata("orders"), DevConfig())
            .WithHttpEndpoint();

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, orders.Resource);

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        var restore = orders.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().Last();

        var context = new EnvironmentCallbackContext(
            new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run), orders.Resource);
        await restore.Callback(context);

        // WithProjectDefaults writes the selected profile's name before it writes the profile's own
        // variables, and both writes are TryAdd — so the name wins and the profile's own value for
        // the same key is dropped. Restoring it the other way round would tell the process it was
        // launched under a profile that was not selected.
        Assert.Equal("http", context.EnvironmentVariables["DOTNET_LAUNCH_PROFILE"]);
    }

    [Fact]
    public async Task CloneProgress_ReachesTheStateColumnAndThenGivesWayToCheckingOut()
    {
        var dir = CreateAppHostDirectory("orders");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        git.ReportProgress(
            "https://example.com/orders.git",
            "Cloning into '/x'...",
            "remote: Counting objects:  50% (1/2)",
            "Receiving objects:  48% (6864/14091), 18.54 MiB | 18.38 MiB/s");

        // The clone is held open past the progress it reports, so the state it produced can be
        // observed while it is still true rather than raced against the clone finishing.
        var gate = git.BlockFor("https://example.com/orders.git");

        var orders = new LocalProjectSource(git)
            .Resolve(builder, "orders", Metadata("orders"), DevConfig())
            .WithHttpEndpoint();

        var services = builder.Services.BuildServiceProvider();

        var states = new List<string>();
        var reachedProgress = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backToCheckingOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watching = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var watcher = Task.Run(
            async () =>
            {
                await foreach (var published in services.GetRequiredService<ResourceNotificationService>()
                                   .WatchAsync(watching.Token))
                {
                    if (!string.Equals(published.Resource.Name, "orders", StringComparison.Ordinal)
                        || published.Snapshot.State?.Text is not { } text)
                    {
                        continue;
                    }

                    lock (states)
                    {
                        states.Add(text);

                        if (text.StartsWith("Receiving objects", StringComparison.Ordinal))
                        {
                            reachedProgress.TrySetResult();
                        }
                        else if (text == "Checking out" && reachedProgress.Task.IsCompleted)
                        {
                            backToCheckingOut.TrySetResult();
                        }
                    }
                }
            },
            watching.Token);

        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, orders.Resource);

        await reachedProgress.Task.WaitAsync(TimeSpan.FromSeconds(30));

        gate.Set();

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));
        await backToCheckingOut.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await watching.CancelAsync();
        await watcher.ContinueWith(_ => { }, TaskScheduler.Default);

        string[] observed;
        lock (states)
        {
            observed = [.. states];
        }

        // git's phase and its own percentage, with the bytes it reported alongside them — not a
        // weighted aggregate across phases, which would invent numbers no clone can honour.
        Assert.Contains("Counting objects 50%", observed);
        Assert.Contains("Receiving objects 48% · 18.54 MiB", observed);

        // And back to "Checking out" once the clone's stream ends, because what follows it — putting
        // the checkout on its configured ref — reports nothing, and a percentage left standing over
        // it would read as a transfer that had stalled.
        Assert.Equal(
            "Checking out",
            observed[(Array.LastIndexOf(observed, "Receiving objects 48% · 18.54 MiB") + 1)..].First());
    }

    [Fact]
    public async Task RealClone_ReportsItsPhasesAndItsOutputToTheResource()
    {
        var origin = TestRepository.CreateOrigin();
        origin.Commit("Service.csproj", "<Project />", "add project");

        // Addressed as a URL rather than as the path it sits at: a clone from a path hardlinks the
        // object store and reports no progress at all, so it would leave nothing to observe.
        var repository = new Uri(origin.Path).AbsoluteUri;

        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(
            Path.Combine(dir, "servicesources.yaml"),
            $"services:\n  orders:\n    repository: {repository}\n    project: Service.csproj\n");
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "orders": { "source": "local" } } }""");

        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        // The real client, so what reaches the dashboard is what git actually wrote rather than what
        // a double says it would have.
        var orders = new LocalProjectSource(new GitCliClient(TestRepository.IsolatedEnvironment()))
            .Resolve(
                builder,
                "orders",
                new ServiceMetadata { Repository = repository, Project = "Service.csproj" },
                DevConfig())
            .WithHttpEndpoint();

        var services = builder.Services.BuildServiceProvider();

        var states = new List<string>();
        using var watching = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var watcher = Task.Run(
            async () =>
            {
                await foreach (var published in services.GetRequiredService<ResourceNotificationService>()
                                   .WatchAsync(watching.Token))
                {
                    if (string.Equals(published.Resource.Name, "orders", StringComparison.Ordinal)
                        && published.Snapshot.State?.Text is { } text)
                    {
                        lock (states)
                        {
                            states.Add(text);
                        }
                    }
                }
            },
            watching.Token);

        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, orders.Resource);

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(60));

        await watching.CancelAsync();
        await watcher.ContinueWith(_ => { }, TaskScheduler.Default);

        string[] observed;
        lock (states)
        {
            observed = [.. states];
        }

        Assert.True(
            File.Exists(Path.Combine(ExpectedRepoRoot(dir, "orders"), "Service.csproj")),
            "The checkout did not land.");

        // A phase git named, whichever ones this repository turned out to be big enough to produce.
        // The clone is over long before the resource has a state to publish to, which is the case
        // that matters most here: the stream is buffered, so what git said is replayed rather than
        // lost.
        string[] phases =
            ["Counting objects", "Compressing objects", "Receiving objects", "Resolving deltas", "Updating files"];

        Assert.Contains(
            observed,
            text => phases.Any(phase => text.StartsWith(phase + " ", StringComparison.Ordinal)));

        // And back to "Checking out" afterwards, so nothing is left claiming a transfer is in
        // flight while the checkout is reconciled onto its ref.
        Assert.Contains("Checking out", observed);
    }

    [Fact]
    public async Task CloneProgress_IsReportedForAServiceThePrefetchNeverEnumerated()
    {
        // The catalog describes "orders" but the developer configuration does not name it, so the
        // prefetch has nothing to speculate about and the checkout is resolved on the start task
        // instead. That path can run a whole cold clone, so it is not one to leave unwatched — and
        // it is the path a deferred service takes whenever the prefetch declines to claim it.
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(
            Path.Combine(dir, "servicesources.yaml"),
            "services:\n  orders:\n    repository: https://example.com/orders.git\n    project: Service.csproj\n");
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), """{ "services": { } }""");

        var builder = TestHelpers.CreateBuilderThatCanStart(dir);
        builder.UseDeferredCheckout();

        var git = new FakeGitClient();
        git.ReportProgress(
            "https://example.com/orders.git", "Receiving objects:  48% (6864/14091), 18.54 MiB | 18.38 MiB/s");

        var orders = new LocalProjectSource(git)
            .Resolve(builder, "orders", Metadata("orders"), DevConfig())
            .WithHttpEndpoint();

        // Nothing was cloned while composing: this service is not in the prefetch set at all.
        Assert.Empty(git.Cloned);

        var services = builder.Services.BuildServiceProvider();

        var states = new List<string>();
        using var watching = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var watcher = Task.Run(
            async () =>
            {
                await foreach (var published in services.GetRequiredService<ResourceNotificationService>()
                                   .WatchAsync(watching.Token))
                {
                    if (string.Equals(published.Resource.Name, "orders", StringComparison.Ordinal)
                        && published.Snapshot.State?.Text is { } text)
                    {
                        lock (states)
                        {
                            states.Add(text);
                        }
                    }
                }
            },
            watching.Token);

        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));
        await PublishNotStartedAsync(services, orders.Resource);

        await Task.WhenAll(DeferredCheckout.For(builder).StartTasks).WaitAsync(TimeSpan.FromSeconds(30));

        await watching.CancelAsync();
        await watcher.ContinueWith(_ => { }, TaskScheduler.Default);

        string[] observed;
        lock (states)
        {
            observed = [.. states];
        }

        Assert.Contains("https://example.com/orders.git", git.Cloned);
        Assert.Contains("Receiving objects 48% · 18.54 MiB", observed);

        // And gives way to "Checking out" here too, rather than holding the last percentage through
        // whatever the checkout still has to do after the clone.
        Assert.Equal(
            "Checking out",
            observed[(Array.LastIndexOf(observed, "Receiving objects 48% · 18.54 MiB") + 1)..].First());
    }

    /// <summary>
    /// Stands in for DCP, which publishes <c>NotStarted</c> when it withholds an explicit-start
    /// executable. That state is what each deferred task waits for before it touches the resource.
    /// </summary>
    private static Task PublishNotStartedAsync(IServiceProvider services, IResource resource) =>
        services.GetRequiredService<ResourceNotificationService>()
            .PublishUpdateAsync(resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.NotStarted, null),
            });

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
