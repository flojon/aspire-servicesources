using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Reports, in the AppHost's own console, that a resource this package added is not running —
/// naming the service, the state that was reported for it, and the dashboard as the place that
/// says why.
/// </summary>
/// <remarks>
/// <para>
/// A <c>"local"</c> checkout that will not compile has nothing to say for itself in the AppHost's
/// console (#150). Aspire's build of a checkout is <c>dotnet run</c>'s own, launched by DCP with the
/// checkout as its working directory, so the compiler's output belongs to that process and reaches
/// the developer only through that resource's console in the dashboard. Without a line of this
/// package's own, the service simply never appears there.
/// </para>
/// <para>
/// That matters more for a service this package added than for a project the developer added
/// themselves. They never wrote the project into their AppHost — they wrote a name in
/// <c>servicesources.local.json</c> — and they did not choose where its code lives, so a resource
/// that quietly fails to appear is one they may not know to look for. It is the same reasoning
/// <see cref="Sources.LocalCheckoutPrefetch"/> already applies to a clone that failed for a service
/// nothing waits on, one step later in the same sequence, and it is reported through the same
/// channel: the package's own logger, in the AppHost's console.
/// </para>
/// <para>
/// What is reported is only <em>that</em> the resource is not running, and the state Aspire
/// reported for it. The output that says why is not ours to relay — it belongs to the launched
/// process and this package does not own its streams — so the notice points at the console that has
/// it rather than paraphrasing a failure it cannot see. A checkout that will not compile is the
/// case in point: measured against Aspire 13.5.2 it reports <c>Finished</c> with exit code 1, not
/// <c>FailedToStart</c>, because <c>dotnet run</c> did launch and it was the build inside it that
/// failed.
/// </para>
/// <para>
/// The division of labour is the one the prepare-step design settles for #118: detail belongs to
/// the resource — its own log lines and its own state, which is where a failed clone and a failed
/// bootstrap already land — and the AppHost's console carries one line saying a service failed and
/// where to look. Because this reads resource <em>state</em> rather than any one failure path, it
/// covers every way a service can fail to start: a build that will not compile, a clone that never
/// landed, and whatever a prepare step does when it exits non-zero.
/// </para>
/// </remarks>
internal sealed class ServiceStartupFailureNotices
{
    /// <summary>
    /// The package's own log category, shared with <see cref="ServiceConfigurationWarnings"/> and
    /// <see cref="Sources.LocalCheckoutPrefetch"/> so that everything this package says in the
    /// AppHost's console can be filtered — or silenced — as one thing.
    /// </summary>
    private const string LogCategory = "Aspire.Hosting.ServiceSources";

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, ServiceStartupFailureNotices>
        Cache = new();

    /// <summary>
    /// What has been reported for each running <em>instance</em>, keyed by
    /// <see cref="ResourceEvent.ResourceId"/>.
    /// </summary>
    /// <remarks>
    /// The id rather than the resource's name, because replicas share one <see cref="IResource"/>
    /// and are told apart only by their id — and their snapshots interleave in the single stream
    /// this reads. Keyed by name, one replica's <c>Running</c> would clear the failure another
    /// replica had just reported, and that replica's next snapshot would report it again.
    /// </remarks>
    private readonly Dictionary<string, Instance> _instances = new(StringComparer.Ordinal);

    /// <summary>
    /// What this report remembers about one instance between snapshots.
    /// </summary>
    private sealed class Instance
    {
        /// <summary>
        /// Whether the last state seen was a failure, so that one failure is one line: a failed
        /// instance goes on producing snapshots — health reports, URLs, the state it died in — for
        /// as long as the host runs.
        /// </summary>
        public bool Failing { get; set; }

        /// <summary>
        /// Whether this instance has been asked to stop since it last began starting. A stop the
        /// developer asked for ends in a terminal state like any other, and must not be reported as
        /// a failure to start.
        /// </summary>
        public bool StopRequested { get; set; }
    }

    // Plain object rather than System.Threading.Lock: this package still targets net8.0.
    private readonly object _gate = new();

    private bool _subscribed;

    /// <summary>
    /// The loop reading resource state, for tests, and <see langword="null"/> until
    /// <c>BeforeStartEvent</c> starts it — which never happens in publish mode.
    /// </summary>
    public Task? ReportTask { get; private set; }

    public static ServiceStartupFailureNotices For(IDistributedApplicationBuilder builder)
    {
        // The factory stays free of side effects: ConditionalWeakTable.GetValue may run it
        // concurrently for the same key and keep only one of the results, so subscribing in there
        // could leave a discarded instance's subscription behind. Same shape as
        // ServiceConfigurationWarnings.
        var notices = Cache.GetValue(builder, static _ => new ServiceStartupFailureNotices());

        notices.EnsureSubscribed(builder);

        return notices;
    }

    private void EnsureSubscribed(IDistributedApplicationBuilder builder)
    {
        // Publish mode composes the model, writes the manifest and exits without starting a
        // resource, so there is no start to fail and no console watching for one.
        if (!builder.ExecutionContext.IsRunMode)
        {
            return;
        }

        lock (_gate)
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;

            builder.Eventing.Subscribe<BeforeStartEvent>((@event, cancellationToken) =>
            {
                Start(@event.Services, cancellationToken);
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// Starts reading resource state on a task of its own.
    /// </summary>
    /// <remarks>
    /// <c>BeforeStartEvent</c> rather than <c>AfterResourcesCreatedEvent</c>, for the same reason
    /// <see cref="Sources.DeferredCheckout"/> uses it: the later event runs behind the wait graph,
    /// and a service held up by a failing dependency is part of that graph. Deliberately not
    /// awaited — host startup awaits the event, and this loop runs for as long as the AppHost does.
    /// </remarks>
    private void Start(IServiceProvider services, CancellationToken cancellationToken)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);
        var notifications = services.GetService<ResourceNotificationService>();

        if (logger is null || notifications is null)
        {
            return;
        }

        ReportTask = Task.Run(
            () => WatchAsync(notifications, logger, services, cancellationToken), CancellationToken.None);
    }

    private async Task WatchAsync(
        ResourceNotificationService notifications,
        ILogger logger,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        // Everything here runs on a task nobody awaits, so a throw would be an unobserved exception
        // with nothing said about it — which is why the lifetime is resolved defensively rather than
        // required, and why building the token source is inside the guard rather than in front of
        // it. Same reasoning as the null checks in Start.
        CancellationTokenSource? stopping = null;

        try
        {
            // The token BeforeStartEvent carries is whatever was handed to RunAsync, and the token
            // an AppHost from the template supplies is none: it ends in Run(), which is
            // RunAsync().Wait() with the default. ApplicationStopping is the signal that does fire,
            // and it fires before DCP stops anything — so the exits of an orderly shutdown are never
            // read as failures to start. The event's own token is kept alongside it because a host
            // given a real one means it.
            var lifetime = services.GetService<IHostApplicationLifetime>();

            stopping = lifetime is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, lifetime.ApplicationStopping);

            await ReportAsync(notifications.WatchAsync(stopping.Token), logger, stopping.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ReportAsync swallows its own; this is the prologue's, and a provider already disposed
            // when a late BeforeStartEvent reaches it is the way it happens.
            try
            {
                logger.LogDebug(ex, "Reporting service start failures could not start: {Message}", ex.Message);
            }
            catch (Exception)
            {
                // The logger is one of the things that can be torn down under us, so noting the
                // problem must not become the problem.
            }
        }
        finally
        {
            stopping?.Dispose();
        }
    }

    /// <summary>
    /// Writes a notice for each resource of ours that reaches a failed state in
    /// <paramref name="events"/>, and returns when the stream ends.
    /// </summary>
    /// <remarks>
    /// Never throws. It runs on a task nobody awaits, so an exception here would be unobserved
    /// rather than reported — and the host tearing down its logging and notification services under
    /// us is one of the ordinary ways this ends.
    /// </remarks>
    internal async Task ReportAsync(
        IAsyncEnumerable<ResourceEvent> events, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var published in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (Notice(published) is not { } notice)
                {
                    continue;
                }

                // An error rather than a warning, and deliberately louder than the prefetch's
                // notices: a service the AppHost actually added and that is not running is not a
                // cost to be aware of, it is the run being wrong.
                logger.LogError("{ServiceSourcesNotice}", notice);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down. Nothing left to report and nowhere to report it.
        }
        catch (Exception ex)
        {
            try
            {
                logger.LogDebug(ex, "Reporting service start failures stopped early: {Message}", ex.Message);
            }
            catch (Exception)
            {
                // The logger is one of the things that can be torn down under us, so noting the
                // problem must not become the problem.
            }
        }
    }

    /// <summary>
    /// The notice <paramref name="published"/> calls for, or <see langword="null"/> when it is not
    /// about a resource of ours, does not report a failure, or repeats one already reported.
    /// </summary>
    private string? Notice(ResourceEvent published)
    {
        if (published.Resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault()
            is not { } annotation)
        {
            return null;
        }

        // A snapshot with no state says nothing either way, so it neither reports a failure nor
        // clears one already reported.
        if (published.Snapshot.State?.Text is not { Length: > 0 } state)
        {
            return null;
        }

        lock (_gate)
        {
            if (!_instances.TryGetValue(published.ResourceId, out var instance))
            {
                _instances[published.ResourceId] = instance = new Instance();
            }

            // An instance that is beginning again is no longer the one that was asked to stop, so a
            // crash after a restart is reported rather than swallowed by the previous stop.
            if (Is(state, KnownResourceStates.Starting) || Is(state, KnownResourceStates.NotStarted))
            {
                instance.StopRequested = false;
            }
            else if (Is(state, KnownResourceStates.Stopping))
            {
                instance.StopRequested = true;
            }

            var failed = !instance.StopRequested && IsFailure(state, published.Snapshot.ExitCode);
            var wasFailing = instance.Failing;
            instance.Failing = failed;

            // Reported on the way into a failed state rather than while in one. A resource the
            // developer restarts from the dashboard leaves the failed state first, so a second
            // failure is a second notice.
            if (!failed || wasFailing)
            {
                return null;
            }
        }

        return FailureMessage(
            published.Resource,
            annotation.ServiceName,
            published.ResourceId,
            annotation.Source,
            state,
            published.Snapshot.ExitCode);
    }

    /// <summary>
    /// Whether a reported state means the service is not running when it should be.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A terminal state counts only when the exit code says it went badly. A missing exit code says
    /// nothing either way, and reporting it as a failure would invent one — the mistake
    /// <see cref="Sources.LocalCheckoutPrefetch"/>'s speculative reporting is written to avoid.
    /// </para>
    /// <para>
    /// <c>RuntimeUnhealthy</c> is deliberately absent, and was measured before being dropped: it
    /// says the container <em>runtime</em> is unreachable, not that this service failed, and Aspire
    /// retries out of it. An AppHost started while the container runtime is still coming up reports
    /// <c>RuntimeUnhealthy</c> for every container-backed service and then goes on to
    /// <c>Starting</c> and <c>Running</c> when the runtime answers, so reporting it would print an
    /// error for a service that is about to be fine — and there is no way to withdraw a line
    /// already written. Under-reporting a transient beats crying wolf, because a channel that
    /// sometimes lies is one a developer learns to ignore, and being ignored is the failure this
    /// whole report exists to fix.
    /// </para>
    /// </remarks>
    private static bool IsFailure(string state, int? exitCode)
    {
        if (Is(state, KnownResourceStates.FailedToStart))
        {
            return true;
        }

        if (Is(state, KnownResourceStates.Exited) || Is(state, KnownResourceStates.Finished))
        {
            return exitCode is not null and not 0;
        }

        return false;
    }

    /// <summary>
    /// Compares a reported state with a known one.
    /// </summary>
    /// <remarks>
    /// Compared rather than pattern-matched: Aspire declares <see cref="KnownResourceStates"/>'s
    /// members as static readonly fields rather than constants, so they cannot appear in a pattern.
    /// </remarks>
    private static bool Is(string state, string known) =>
        string.Equals(state, known, StringComparison.Ordinal);

    /// <summary>
    /// Says which service, what was reported for it, and where the reason is — without claiming to
    /// know what the reason was.
    /// </summary>
    private static string FailureMessage(
        IResource resourceModel,
        string serviceName,
        string resourceId,
        string source,
        string state,
        int? exitCode)
    {
        // The resource is normally named for the service, and "'orders' ... its resource 'orders'"
        // reads as two things rather than one.
        var resource = string.Equals(serviceName, resourceModel.Name, StringComparison.Ordinal)
            ? "its resource"
            : $"its resource '{resourceModel.Name}'";

        // The instance id only where there is more than one instance to tell apart. DCP gives every
        // instance a suffixed id whether the service is replicated or not, so naming it
        // unconditionally would put an id nobody asked about into the ordinary one-replica line.
        if (resourceModel.Annotations.OfType<ReplicaAnnotation>().LastOrDefault() is { Replicas: > 1 })
        {
            resource += $" replica '{resourceId}'";
        }

        var reported = exitCode is { } code ? $"'{state}' with exit code {code}" : $"'{state}'";

        // Only 'local' runs code from a working tree this package resolved, and only there is the
        // build itself something the developer has no other account of.
        var checkout = string.Equals(source, "local", StringComparison.Ordinal)
            ? " A 'local' service runs from a checkout rather than from a project added to this " +
              "AppHost, and the build of that checkout writes to that same console — so a failure " +
              "to compile is reported nowhere else at all."
            : string.Empty;

        return $"Service '{serviceName}' is configured as '{source}' and {resource} is not running: it " +
               $"reported {reported}. This console does not carry that resource's output, so nothing here " +
               "says why — its own console in the Aspire dashboard does, at the dashboard URL logged " +
               $"above.{checkout}";
    }
}
