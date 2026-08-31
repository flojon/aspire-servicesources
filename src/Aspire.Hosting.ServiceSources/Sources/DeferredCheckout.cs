using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Registers a <c>"local"</c> <c>dotnet</c> service whose managed checkout does not exist yet as a
/// project resource that is held back at startup, and starts it once the clone has landed.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the first <c>AddService()</c> call blocks composition until every <c>"local"</c>
/// service's checkout has resolved. On a cold clone the AppHost never reaches the dashboard —
/// there is nothing to look at while several repositories clone — and one checkout failure throws
/// out of composition, taking the whole AppHost with it including the services that were fine.
/// </para>
/// <para>
/// Deferring turns that into ordinary resource lifecycle. The project is registered at the path its
/// checkout <em>will</em> have, marked <c>WithExplicitStart()</c>, and started from a background
/// task once the clone finishes. DCP withholds an explicit-start
/// executable entirely rather than creating it stopped
/// (<c>ExecutableCreator.IsReadyToCreate</c>), so working directory, certificates and execution
/// configuration are all resolved on demand in <c>CreateObjectAsync</c> — after the checkout —
/// and <c>dotnet run --project</c> builds the project then too, rather than at startup.
/// </para>
/// <para>
/// The clone itself is not started here and is not made any later: <see cref="LocalCheckoutPrefetch"/>
/// still kicks every <c>"local"</c> checkout off on the first <c>AddService()</c> call, on
/// background threads. All that changes is who waits for it — a background task after the host is
/// up, instead of composition.
/// </para>
/// <para>
/// What still has to be final before <c>Build()</c> is the <em>path</em>. DCP freezes it into the
/// executable spec (working directory, <c>--project</c>) in
/// <c>ExecutableCreator.PrepareProjectExecutablesAsync</c>, which runs for every project resource
/// at startup regardless of whether the executable is withheld. That is affordable because the
/// managed checkout root is a pure function of the service name
/// (<see cref="LocalGitCheckout.ManagedRepoRoot"/>), so the path is computable from the committed
/// catalog with no network access.
/// </para>
/// <para>
/// Endpoints are the one thing deferral cannot preserve: they are synthesised from the launch
/// profile's <c>applicationUrl</c> while composing, and nothing re-runs that step later. Rather
/// than demand a declaration up front and refuse to run without one — which a run-to-completion
/// worker, whose profile has no <c>applicationUrl</c> on either path, cannot satisfy honestly —
/// the divergence is checked against the repository's real launch profile once the checkout has
/// landed, and reported then. See <see cref="LaunchProfileEndpointWarning"/>.
/// </para>
/// </remarks>
internal sealed class DeferredCheckout
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, DeferredCheckout> Cache = new();

    /// <summary>
    /// The variable <c>dotnet run</c> and <c>dotnet watch</c> report the active profile in, which
    /// <c>WithProjectDefaults</c> sets on the warm path and the restore has to set here.
    /// </summary>
    private const string LaunchProfileNameVariable = "DOTNET_LAUNCH_PROFILE";

    // Plain object rather than System.Threading.Lock: this package still targets net8.0.
    private readonly object _gate = new();

    private readonly List<Deferred> _deferred = [];

    private bool _enabled;

    private bool _subscribed;

    private sealed record Deferred(
        string ServiceName,
        ProjectResource Resource,
        string RepoRoot,
        ServiceMetadata Metadata,
        ServiceDeveloperConfig Config,
        string AppHostDirectory,
        LocalCheckoutPrefetch Prefetch,
        IGitClient GitClient);

    public static DeferredCheckout For(IDistributedApplicationBuilder builder)
    {
        // The factory stays free of side effects, for the reason spelled out in
        // LocalCheckoutPrefetch.For: ConditionalWeakTable.GetValue may run it concurrently for the
        // same key and keep only one result. Subscribing happens on the instance that won, in
        // EnsureSubscribed.
        return Cache.GetValue(builder, static _ => new DeferredCheckout());
    }

    /// <summary>
    /// Turns deferral on for this builder. Off by default: the behaviour is user-visible — a
    /// service that used to be running by the time <c>Build()</c> returned is now started
    /// afterwards — and the package already has consumers.
    /// </summary>
    public void Enable()
    {
        lock (_gate)
        {
            _enabled = true;
        }
    }

    /// <summary>
    /// Whether <paramref name="serviceName"/> should be registered deferred rather than resolved
    /// eagerly. Scoped tightly on purpose: a warm checkout keeps today's path exactly, with full
    /// launch-profile fidelity, so the blast radius is first-run-only.
    /// </summary>
    public bool ShouldDefer(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDeveloperConfig config)
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                return false;
            }
        }

        // Run mode only. Deferral buys a dashboard to look at while the clone runs, and publish mode
        // has no dashboard, no DCP and no resource lifecycle — it composes the model, writes the
        // manifest and exits. Deferring there would trade the whole point of the manifest for
        // nothing: the resource would be described from a .csproj that is not on disk, so it would
        // carry no launch-profile endpoints and no profile environment, where the eager path clones
        // first and describes the real project. The start task would strand too, waiting on a
        // NotStarted that only DCP publishes.
        //
        // BeforeStartEvent does not distinguish the two — DistributedApplication publishes it in
        // every mode but 'inspect' — so the mode has to be checked here, before anything is
        // registered deferred.
        if (!builder.ExecutionContext.IsRunMode)
        {
            return false;
        }

        // A 'path' override points at a checkout the developer manages themselves. There is nothing
        // to clone, so there is nothing to wait for — and nothing this package is entitled to
        // create at that path if it turns out to be missing.
        if (config.Path is not null)
        {
            return false;
        }

        // Anything already on disk — a complete checkout, or debris from an interrupted clone —
        // goes down the eager path, which is the one that knows how to tell those apart and what to
        // do about each. Deferral only claims the case where there is nothing there at all.
        return !Directory.Exists(LocalGitCheckout.ManagedRepoRoot(builder.AppHostDirectory, serviceName));
    }

    /// <summary>
    /// Registers the project resource for a service whose checkout has not happened yet, and
    /// arranges for the checkout — and the start — to run after the host is up.
    /// </summary>
    public IResourceBuilder<IResourceWithServiceDiscovery> Register(
        IDistributedApplicationBuilder builder,
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        LocalCheckoutPrefetch prefetch,
        IGitClient gitClient)
    {
        var repoRoot = LocalGitCheckout.ManagedRepoRoot(builder.AppHostDirectory, serviceName);
        var projectPath = Path.Combine(repoRoot, metadata.Project);

        var resource = new ProjectResource(serviceName);

        // Aspire's own AddProject, taken apart into the two steps it is made of, because the path
        // this one is given does not exist yet: AddProject would attach its internal ProjectMetadata,
        // whose launch-settings read throws for a missing .csproj. Everything else is identical —
        // WithProjectDefaults is what supplies OTLP export, console logs, certificate trust and
        // debugging support, and it is public from 13.5.0 precisely so it can be reached like this.
#pragma warning disable ASPIREPROJECTS001 // WithProjectDefaults is [Experimental]; it is the only public route to the project defaults an out-of-tree project resource needs.
        var resourceBuilder = builder.AddResource(resource)
            .WithAnnotation<IProjectMetadata>(new DeferredProjectMetadata(projectPath))
            .WithProjectDefaults(new ProjectResourceOptions())
            .WithExplicitStart();
#pragma warning restore ASPIREPROJECTS001

        // The prefetch reports every checkout it started that no AddService() call asked for, at
        // BeforeStartEvent. This service was asked for; it just won't be waited on until later, so
        // it has to say so now or it would be reported as speculative work nobody wanted.
        prefetch.MarkRequested(serviceName);

        lock (_gate)
        {
            _deferred.Add(new Deferred(
                serviceName, resource, repoRoot, metadata, config, builder.AppHostDirectory, prefetch, gitClient));
        }

        EnsureSubscribed(builder);

        return ResolvedService.Tag(resourceBuilder, serviceName, "local");
    }

    private void EnsureSubscribed(IDistributedApplicationBuilder builder)
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
        }

        // BeforeStartEvent, not AfterResourcesCreatedEvent. "After resources created" sounds like
        // the right moment and is not: a resource with an unsatisfied WaitFor annotation is not
        // created until that wait resolves, so the event sits behind the whole wait graph — and
        // anything that waits on a deferred service is itself part of that graph. Subscribing there
        // deadlocks the common case, because the event that would start the service cannot fire
        // until the service that is waiting to be started has started. Each task waits for its own
        // resource instead, in StartDeferredAsync, which needs nothing from the rest of the graph.
        builder.Eventing.Subscribe<BeforeStartEvent>((@event, cancellationToken) =>
        {
            StartAll(@event.Services, cancellationToken);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The warning for a deferred service whose landed launch profile declares an
    /// <c>applicationUrl</c> that the AppHost did not mirror as an endpoint, or
    /// <see langword="null"/> when there is nothing to say. Exposed for tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of what deferral costs, stated at the only moment it can be stated
    /// accurately. Endpoints are synthesised from <c>applicationUrl</c> while composing, when the
    /// repository is not on disk; nothing re-runs that step, so a deferred resource carries only
    /// the endpoints the AppHost declared. If the project turns out to declare none either, that
    /// agrees with the warm path exactly and there is nothing wrong — which is the case a
    /// run-to-completion worker is in, and the case the earlier pre-flight check could not tell
    /// apart from a mistake.
    /// </para>
    /// <para>
    /// When the profile <em>does</em> declare one, the consequence is worth a sentence: the project
    /// still binds that URL itself, because <c>dotnet run</c> applies its own launch profile, but
    /// Aspire allocated no endpoint for it — so the port is not reassigned away from a collision,
    /// no proxy fronts it, service discovery cannot resolve it and the dashboard does not link it.
    /// The message can quote the real URL, which is why it is worth waiting for the checkout to
    /// produce it rather than guessing at composition time.
    /// </para>
    /// </remarks>
    public static string? LaunchProfileEndpointWarning(
        string serviceName, LandedLaunchProfile profile, IResource resource)
    {
        if (resource.Annotations.OfType<EndpointAnnotation>().Any())
        {
            return null;
        }

        var declared = profile.ApplicationUrls;
        if (declared.Count == 0)
        {
            return null;
        }

        return $"Service '{serviceName}' was started from a checkout cloned during this run, and its launch " +
               $"profile declares applicationUrl '{string.Join(", ", declared)}' — but a project's endpoints are " +
               "read while the AppHost composes, before the repository was on disk, so Aspire allocated none. " +
               "The project will bind that URL itself and run, but the port is outside Aspire's management: it " +
               "is not moved off a collision, nothing proxies it, service discovery cannot resolve this service " +
               $"and the dashboard will not link it. Declare it in the AppHost — builder.AddService(\"{serviceName}\")" +
               ".WithHttpEndpoint() — which also takes effect on every later run, where the checkout is warm and " +
               "the endpoint comes from the profile as usual. This is reported after the clone rather than " +
               "refused before it, so a service that has no endpoints on either path costs you nothing.";
    }

    /// <summary>
    /// Puts the landed launch profile's <c>environmentVariables</c> back onto the resource, which
    /// Aspire would have applied during composition had the repository been on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a nicety. <c>Host.CreateDefaultBuilder</c> takes the environment name from
    /// <c>DOTNET_ENVIRONMENT</c>, which for most repositories is set by the launch profile and
    /// nowhere else — so without this a deferred service runs as <c>Production</c> while every warm
    /// run of the same service runs as <c>Development</c>. That is the kind of divergence that
    /// either crashes immediately on a missing <c>appsettings.Development.json</c> or, worse, does
    /// not.
    /// </para>
    /// <para>
    /// Added last and only where the key is absent, so anything the AppHost set — <c>WithEnvironment</c>,
    /// <c>WithReference</c>, a resolved connection string — wins over the profile, which is the
    /// precedence Aspire itself applies. Command-line arguments from <c>commandLineArgs</c> are not
    /// restored here: unlike environment, they are positional, so appending them after whatever the
    /// AppHost added can change their meaning — and they need no restoring anyway, because Aspire
    /// reads the profile again when it builds the executable, after the clone.
    /// </para>
    /// <para>
    /// Values are expanded and <c>DOTNET_LAUNCH_PROFILE</c> is set, both of which
    /// <c>WithProjectDefaults</c> does on the warm path. Skipping either would leave a difference
    /// that only shows up on the first run: a profile value such as <c>%USERPROFILE%\certs</c> would
    /// reach the process literally now and expanded on every run after.
    /// </para>
    /// </remarks>
    private static void RestoreLaunchProfileEnvironment(
        Deferred deferred, LandedLaunchProfile profile, ILogger logger)
    {
        // Keyed off the profile rather than off its variable count: a profile with no
        // environmentVariables still names itself in DOTNET_LAUNCH_PROFILE on the warm path.
        if (profile.Name is not { } profileName)
        {
            return;
        }

        deferred.Resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            // Written before the profile's own variables, which is the order Aspire writes them
            // in: the selected profile's name wins over a DOTNET_LAUNCH_PROFILE the profile
            // happens to declare for itself.
            if (!context.EnvironmentVariables.ContainsKey(LaunchProfileNameVariable))
            {
                context.EnvironmentVariables[LaunchProfileNameVariable] = profileName;
            }

            foreach (var variable in profile.EnvironmentVariables)
            {
                if (!context.EnvironmentVariables.ContainsKey(variable.Key))
                {
                    context.EnvironmentVariables[variable.Key] = Environment.ExpandEnvironmentVariables(variable.Value);
                }
            }
        }));

        // Counted from what the callback above sets, DOTNET_LAUNCH_PROFILE included — a profile
        // with no environmentVariables of its own still restores that one, and a log saying
        // nothing was applied would send a developer looking for a bug that isn't there. A profile
        // that declares the name variable itself is not counted twice, because the callback drops
        // its value for the profile name.
        var applied = new List<string>(profile.EnvironmentVariables.Count + 1) { LaunchProfileNameVariable };
        applied.AddRange(profile.EnvironmentVariables.Keys.Where(
            key => !string.Equals(key, LaunchProfileNameVariable, StringComparison.Ordinal)));

        logger.LogInformation(
            "Applied {Count} environment variable(s) from the checkout's launch profile '{Profile}' ({Names}), "
            + "which Aspire could not read while composing the AppHost.",
            applied.Count,
            profileName,
            string.Join(", ", applied));
    }

    /// <summary>
    /// The per-service checkout-then-start tasks <see cref="StartAll"/> launched. Exposed for tests
    /// — in a real run nothing awaits these, which is the whole point of them.
    /// </summary>
    public IReadOnlyList<Task> StartTasks
    {
        get
        {
            lock (_gate)
            {
                return [.. _startTasks];
            }
        }
    }

    private readonly List<Task> _startTasks = [];

    private void StartAll(IServiceProvider services, CancellationToken cancellationToken)
    {
        Deferred[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _deferred];
        }

        foreach (var deferred in snapshot)
        {
            // Deliberately not awaited: BeforeStartEvent is awaited by host startup, and waiting
            // for the checkout here would put the block back exactly where this class exists to
            // take it out of. StartDeferredAsync never throws, so nothing is left unobserved.
            //
            // Task.Run rather than a bare call because the first thing past the await is
            // GetRepoRoot, which blocks the calling thread on the clone.
            var task = Task.Run(
                () => StartDeferredAsync(deferred, services, cancellationToken), CancellationToken.None);

            lock (_gate)
            {
                _startTasks.Add(task);
            }
        }
    }

    /// <summary>
    /// Waits for one service's checkout and then starts its resource, reporting failure as resource
    /// state and resource logs rather than as an exception.
    /// </summary>
    /// <remarks>
    /// Nothing is left for this to throw at. It runs on a task nobody awaits, long after the call
    /// that would have propagated a configuration error has returned — which is the point: a
    /// checkout that fails now costs one service, not the whole AppHost. The developer's route to
    /// it is the dashboard, so that is where it is put.
    /// </remarks>
    private static async Task StartDeferredAsync(
        Deferred deferred, IServiceProvider services, CancellationToken cancellationToken)
    {
        try
        {
            var notifications = services.GetRequiredService<ResourceNotificationService>();
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(deferred.Resource);

            // BeforeStartEvent carries whatever token was handed to RunAsync, and the token an
            // AppHost actually supplies is none: the template ends in Run(), which is
            // RunAsync().Wait() with the default. Ctrl-C would leave this task waiting on a
            // NotStarted that is no longer coming, and if the clone landed mid-teardown it would go
            // on to start a resource on a stopping host and report the resulting failure as the
            // service's own. ApplicationStopping is the signal that does fire; the event's token is
            // kept alongside it because a host that was given a real one means it.
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

            var stoppingToken = shutdown.Token;

            // This runs from BeforeStartEvent, so the resource is not in the notification service
            // yet — a state published now would be overwritten by the NotStarted that DCP publishes
            // when it withholds the explicit-start executable, and the start command below would
            // have nothing to act on. Waiting for that NotStarted is the whole synchronisation this
            // needs, and it depends on nothing but this one resource.
            await notifications.WaitForResourceAsync(
                deferred.Resource.Name, KnownResourceStates.NotStarted, stoppingToken)
                .ConfigureAwait(false);

            await notifications.PublishUpdateAsync(deferred.Resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot("Checking out", KnownResourceStateStyles.Info),
            }).ConfigureAwait(false);

            logger.LogInformation(
                "Resolving checkout of {Repository} into {RepoRoot} before starting.",
                GitUrl.Redact(deferred.Metadata.Repository),
                deferred.RepoRoot);

            var repoRoot = deferred.Prefetch.GetRepoRoot(
                deferred.ServiceName,
                deferred.Metadata,
                deferred.Config,
                deferred.AppHostDirectory,
                deferred.GitClient);

            // The absolute .csproj path was frozen into the DCP executable spec before this ran, so
            // a checkout that landed anywhere else cannot be started — the resource would run with
            // the wrong working directory and --project argument. It should not be reachable:
            // ManagedRepoRoot is the same pure function both sides call, and deferral is refused
            // for a 'path' override. Checked anyway, because being wrong about it is silent.
            if (!string.Equals(repoRoot, deferred.RepoRoot, StringComparison.Ordinal))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{deferred.ServiceName}': the checkout resolved to '{repoRoot}', but the resource " +
                    $"was registered against '{deferred.RepoRoot}' before the AppHost started and that path " +
                    "cannot be changed afterwards.");
            }

            var projectFile = LocalProjectSource.ResolveProjectFile(
                deferred.ServiceName, repoRoot, deferred.Metadata.Project);

            // The repository is on disk now, so everything Aspire read from the launch profile while
            // composing — and got nothing for — is finally readable.
            var profile = LandedLaunchProfile.Read(projectFile, deferred.Resource);

            RestoreLaunchProfileEnvironment(deferred, profile, logger);

            // Endpoints are the one part that cannot be put back, so the shortfall is reported
            // rather than enforced: refusing to start a service that would have run fine is worse
            // than telling the developer their port is not Aspire's to manage.
            if (LaunchProfileEndpointWarning(deferred.ServiceName, profile, deferred.Resource) is { } warning)
            {
                logger.LogWarning("{Warning}", warning);
            }

            logger.LogInformation("Checkout ready at {RepoRoot}. Starting.", repoRoot);

            var result = await services.GetRequiredService<ResourceCommandService>()
                .ExecuteCommandAsync(deferred.Resource, KnownResourceCommands.StartCommand, stoppingToken)
                .ConfigureAwait(false);

            if (!result.Success && !result.Canceled)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{deferred.ServiceName}': the checkout completed but starting the resource failed. " +
                    (result.Message ?? "No further detail was reported."));
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down. This is reached from the wait for NotStarted or from the
            // start command, never from the clone in between: LocalCheckoutPrefetch takes no
            // cancellation token, so a shutdown that arrives mid-clone is not noticed until that
            // clone has finished on its own. Either way there is nothing left to start and nobody
            // to tell.
        }
        catch (Exception ex)
        {
            await ReportFailureAsync(deferred, services, ex).ConfigureAwait(false);
        }
    }

    private static async Task ReportFailureAsync(
        Deferred deferred, IServiceProvider services, Exception exception)
    {
        try
        {
            var notifications = services.GetRequiredService<ResourceNotificationService>();
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(deferred.Resource);

            logger.LogError(
                exception,
                "Service '{ServiceName}': its checkout was deferred past startup and did not complete, so the " +
                "service was never started. {Message}",
                deferred.ServiceName,
                exception.Message);

            await notifications.PublishUpdateAsync(deferred.Resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error),
            }).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reporting the failure must not become a second, worse failure on a task nobody awaits.
            // The host may already be tearing its logging and notification services down — which is
            // one of the ways the checkout got interrupted in the first place.
        }
    }
}
