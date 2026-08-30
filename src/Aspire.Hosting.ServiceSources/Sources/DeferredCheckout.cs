using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Microsoft.Extensions.DependencyInjection;
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
/// checkout <em>will</em> have, marked <c>WithExplicitStart()</c>, and started from
/// <c>AfterResourcesCreatedEvent</c> once the clone finishes. DCP withholds an explicit-start
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
/// Endpoints are the one thing deferral cannot preserve, so they are required instead — see
/// <see cref="ValidateEndpoints"/>.
/// </para>
/// </remarks>
internal sealed class DeferredCheckout
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, DeferredCheckout> Cache = new();

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
    public bool ShouldDefer(string appHostDirectory, string serviceName, ServiceDeveloperConfig config)
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                return false;
            }
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
        return !Directory.Exists(LocalGitCheckout.ManagedRepoRoot(appHostDirectory, serviceName));
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

        builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            ValidateEndpoints();
            return Task.CompletedTask;
        });

        builder.Eventing.Subscribe<AfterResourcesCreatedEvent>((@event, cancellationToken) =>
        {
            StartAll(@event.Services, cancellationToken);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The message for a deferred service that declares no endpoints, or <see langword="null"/>
    /// when every deferred service declares at least one. Exposed for tests.
    /// </summary>
    public string? MissingEndpointsMessage
    {
        get
        {
            Deferred[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _deferred];
            }

            var missing = snapshot
                .Where(deferred => !deferred.Resource.Annotations.OfType<EndpointAnnotation>().Any())
                .Select(deferred => deferred.ServiceName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            if (missing.Length == 0)
            {
                return null;
            }

            var subject = missing.Length == 1
                ? $"Service '{missing[0]}' declares no endpoints"
                : $"Services {string.Join(", ", missing.Select(name => $"'{name}'"))} declare no endpoints";

            return $"{subject}, and its checkout is being cloned during this run rather than before it. " +
                   "A project's endpoints come from the 'applicationUrl' of its launch profile, which Aspire " +
                   "reads while composing the AppHost — before the repository is on disk — so there is nothing " +
                   "to read and the service would come up unreachable. Declare them in the AppHost instead: " +
                   $"builder.AddService(\"{missing[0]}\").WithHttpEndpoint(). The same line is correct on a warm " +
                   "checkout too — WithHttpEndpoint updates an existing endpoint of the same name with its " +
                   "non-null arguments only, and it has none — so there is no second code path to maintain.";
        }
    }

    /// <summary>
    /// Fails the run when a deferred service declares no endpoints of its own.
    /// </summary>
    /// <remarks>
    /// The alternative was to synthesise a default <c>http</c> endpoint on the cold path. An
    /// endpoint invented here silently disagrees with whatever the repository's own
    /// <c>launchSettings.json</c> says the moment the checkout is warm, which is a subtler bug than
    /// a named error — and this one is a one-time cost paid when opting in, not a surprise on
    /// somebody's first run.
    /// <para>
    /// It does mean an AppHost can pass on a machine where every checkout is warm and fail on a
    /// fresh clone, because a warm resource's endpoints come from the launch profile and are
    /// indistinguishable here from declared ones — <c>WithHttpEndpoint</c> updates the annotation
    /// the launch profile already created rather than leaving a mark of its own. The error names
    /// the fix, and the fix is correct on both paths.
    /// </para>
    /// </remarks>
    private void ValidateEndpoints()
    {
        if (MissingEndpointsMessage is { } message)
        {
            throw new ServiceSourcesConfigurationException(message);
        }
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
            // Deliberately not awaited: AfterResourcesCreatedEvent is awaited by host startup, and
            // waiting for the checkout here would put the block back exactly where this class exists
            // to take it out of. StartDeferredAsync never throws, so nothing is left unobserved.
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

            LocalProjectSource.ResolveProjectFile(deferred.ServiceName, repoRoot, deferred.Metadata.Project);

            logger.LogInformation("Checkout ready at {RepoRoot}. Starting.", repoRoot);

            var result = await services.GetRequiredService<ResourceCommandService>()
                .ExecuteCommandAsync(deferred.Resource, KnownResourceCommands.StartCommand, cancellationToken)
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
            // The host is shutting down while the clone was still running. There is nothing to
            // start and nobody to tell.
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
