using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// What a <see cref="PortForwardSupervisor"/> needs from Aspire, so the decisions can be tested
/// without a host and without <c>kubectl</c>.
/// </summary>
internal interface IPortForwardControl
{
    /// <summary>
    /// Asks Aspire to start the exited tunnel again. Returns <see langword="null"/> when the request
    /// was accepted — which says nothing about whether the new process holds — or why it was not.
    /// </summary>
    Task<string?> StartAsync(CancellationToken cancellationToken);

    /// <summary>Publishes <c>FailedToStart</c> for the tunnel, carrying kubectl's own words if known.</summary>
    Task FailToStartAsync(string? kubectlError, CancellationToken cancellationToken);

    /// <summary>The last line kubectl wrote to its error stream (or, failing that, any stream).</summary>
    string? LastOutput { get; }
}

/// <summary>
/// Owns the lifetime of a <c>"kubernetes"</c> service's <c>kubectl port-forward</c> after it has
/// started (#490): the two things Aspire does not do for an executable that is a tunnel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Exit before the tunnel was ever up is a failed start, and is not retried.</b> The process is
/// <c>Running</c> as soon as it has launched, so a Service that does not exist, a context that
/// cannot authenticate and a port the Service does not expose all end in <c>Finished</c> with exit
/// code 1 — Aspire's word for a program that ran and returned an error, not for a resource that
/// never came up. This publishes <c>FailedToStart</c> instead, which is the state a consumer's
/// <c>WaitFor</c> fails on with a reason, and logs kubectl's last line. These are configuration
/// errors that a retry reproduces, so the first one is final until the developer changes something
/// and restarts the resource.
/// </para>
/// <para>
/// <b>Exit after the tunnel was up is restarted, with a backoff, a log line per attempt, and a
/// limit.</b> A tunnel is not supposed to end: when the pod behind it is replaced, kubectl's stream
/// breaks and it exits, and without this everything that references the service loses it for the
/// rest of the run. The restart is Aspire's own Start command on the same resource, so it keeps the
/// name, the port and the console. It is bounded on purpose: <see cref="MaxRestarts"/> attempts in
/// a row, spaced 1s, 2s, 4s … up to <see cref="MaxBackoff"/>, counted from the last time the tunnel
/// was healthy. A cluster that stays unreachable is a problem for the developer, and an endless
/// loop would turn it into noise in two logs. Nothing is restarted after the developer stopped the
/// resource, or once the AppHost is shutting down.
/// </para>
/// <para>
/// Not verified against a real cluster or a real DCP: the decisions are tested through
/// <see cref="IPortForwardControl"/>, and the Aspire-facing half (<see cref="AspireControl"/>) is a
/// thin wrapper over documented APIs.
/// </para>
/// </remarks>
internal sealed class PortForwardSupervisor(
    string serviceName,
    ILogger logger,
    IPortForwardControl control,
    TimeProvider timeProvider,
    TimeSpan outputSettle)
{
    /// <summary>How many restarts in a row are tried before the tunnel is left down.</summary>
    internal const int MaxRestarts = 8;

    /// <summary>The longest wait between two restart attempts.</summary>
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a tunnel has to stay healthy before an outage after it counts as a fresh one. A
    /// health report cannot be told from a stale one left by the previous process, so a single
    /// healthy snapshot is not proof that the restart worked.
    /// </summary>
    internal static readonly TimeSpan StableUptime = TimeSpan.FromSeconds(30);

    private const string LogCategory = "Aspire.Hosting.ServiceSources";

    private static readonly ConditionalWeakTable<IResource, object> Installed = new();

    private readonly object _gate = new();

    // The tunnel's own state, kept between snapshots. One instance: the resource is never replicated.
    private bool _ready;
    private bool _stopRequested;
    private bool _ended;
    private bool _gaveUp;
    private int _restarts;
    private DateTimeOffset? _healthySince;
    private CancellationTokenSource? _pendingRestart;
    private Task _restartTask = Task.CompletedTask;

    /// <summary>The wait before restart attempt <paramref name="attempt"/> (1-based): 1s, 2s, 4s … capped.</summary>
    internal static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt - 1), MaxBackoff.TotalSeconds));

    /// <summary>
    /// Supervises <paramref name="resource"/> from <c>BeforeStartEvent</c>. Run mode only: publish
    /// mode starts nothing.
    /// </summary>
    public static void Install(IDistributedApplicationBuilder builder, IResource resource, string serviceName)
    {
        if (!builder.ExecutionContext.IsRunMode || !Installed.TryAdd(resource, new object()))
        {
            return;
        }

        builder.Eventing.Subscribe<BeforeStartEvent>((@event, cancellationToken) =>
        {
            Start(@event.Services, resource, serviceName, cancellationToken);
            return Task.CompletedTask;
        });
    }

    private static void Start(
        IServiceProvider services, IResource resource, string serviceName, CancellationToken cancellationToken)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(LogCategory);
        var notifications = services.GetService<ResourceNotificationService>();
        var commands = services.GetService<ResourceCommandService>();
        var loggers = services.GetService<ResourceLoggerService>();

        if (logger is null || notifications is null || commands is null || loggers is null)
        {
            return;
        }

        var lifetime = services.GetService<IHostApplicationLifetime>();

        // Deliberately not awaited: host startup awaits BeforeStartEvent, and this runs as long as the
        // AppHost does. ApplicationStopping, as in ServiceStartupFailureNotices, so that the exits of
        // an orderly shutdown are never read as a tunnel dying.
        _ = Task.Run(async () =>
        {
            using var stopping = lifetime is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.ApplicationStopping);

            try
            {
                var output = new TunnelOutput();
                var watching = output.ConsumeAsync(loggers.WatchAsync(resource), stopping.Token);
                var supervisor = new PortForwardSupervisor(
                    serviceName, logger, new AspireControl(resource, notifications, commands, output),
                    TimeProvider.System, TimeSpan.FromMilliseconds(500));

                await supervisor.SuperviseAsync(
                    notifications.WatchAsync(stopping.Token).Where(e => e.Resource == resource),
                    stopping.Token).ConfigureAwait(false);

                await watching.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                try
                {
                    ServiceSourcesLog.Debug(
                        logger, ex, $"{Raw.Literal("Supervising the port-forward stopped early")}: {Raw.Cause(ex)}");
                }
                catch (Exception)
                {
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>Reads the tunnel's snapshots until the stream or <paramref name="cancellationToken"/> ends.</summary>
    internal async Task SuperviseAsync(IAsyncEnumerable<ResourceEvent> events, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var published in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await OnSnapshotAsync(published.Snapshot, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down: nothing left to supervise.
        }
        finally
        {
            CancelPendingRestart();
            await _restartTask.ConfigureAwait(false);
        }
    }

    private async Task OnSnapshotAsync(CustomResourceSnapshot snapshot, CancellationToken cancellationToken)
    {
        // A snapshot with no state says nothing either way.
        if (snapshot.State?.Text is not { Length: > 0 } state)
        {
            return;
        }

        if (Is(state, KnownResourceStates.Starting) || Is(state, KnownResourceStates.NotStarted))
        {
            // Not a reason to cancel a pending restart: the supervisor's own Start produces this
            // snapshot too, often before the Start call has returned.
            _ready = false;
            _stopRequested = false;
            _ended = false;
            _healthySince = null;

            lock (_gate)
            {
                // A start after the supervisor gave up is the developer's: it gets a full allowance.
                if (_gaveUp)
                {
                    _gaveUp = false;
                    _restarts = 0;
                }
            }
        }
        else if (Is(state, KnownResourceStates.Running))
        {
            CancelPendingRestart();
            _ended = false;

            // A report has to be there to read: Aspire calls a Running resource with no checks Healthy,
            // and "Running" is exactly what this class exists to not take for "the tunnel is up".
            if (snapshot.HealthReports.Length > 0 && snapshot.HealthStatus == HealthStatus.Healthy)
            {
                _ready = true;
                _healthySince ??= timeProvider.GetUtcNow();
            }
        }
        else if (Is(state, KnownResourceStates.Stopping))
        {
            _stopRequested = true;
        }
        else if (Is(state, KnownResourceStates.FailedToStart))
        {
            // Aspire's own verdict (kubectl not on PATH), or ours from a moment ago. Either way the
            // start attempt is over and a retry would repeat it.
            _ended = true;
        }
        else if ((Is(state, KnownResourceStates.Exited) || Is(state, KnownResourceStates.Finished)) && !_ended)
        {
            // Acted on the way into the state, not while in it: an ended resource goes on
            // republishing the state it ended in.
            _ended = true;

            if (_stopRequested)
            {
                _stopRequested = false;
                return;
            }

            bool firstFailure;

            lock (_gate)
            {
                // A fresh outage is one that follows a tunnel that held, not one that reported healthy once.
                if (_healthySince is { } since && timeProvider.GetUtcNow() - since >= StableUptime)
                {
                    _restarts = 0;
                }

                firstFailure = !_ready && _restarts == 0;
            }

            if (firstFailure)
            {
                await FailedBeforeReadyAsync(snapshot.ExitCode, cancellationToken).ConfigureAwait(false);
                return;
            }

            var wasReady = _ready;
            var exitCode = snapshot.ExitCode;

            CancelPendingRestart();
            await _restartTask.ConfigureAwait(false);

            // Off the snapshot loop, so that a Running or Starting snapshot arriving during the
            // backoff is still read, and overrules the restart.
            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pendingRestart = cts;
            _restartTask = Task.Run(() => RestartAsync(exitCode, wasReady, cts.Token, cancellationToken), CancellationToken.None);
        }
    }

    private void CancelPendingRestart() => Interlocked.Exchange(ref _pendingRestart, null)?.Cancel();

    private async Task FailedBeforeReadyAsync(int? exitCode, CancellationToken cancellationToken)
    {
        var said = await KubectlSaidAsync(cancellationToken).ConfigureAwait(false);

        ServiceSourcesLog.Error(
            logger,
            $"Service '{new Name(serviceName)}': the kubectl port-forward exited{ExitCode(exitCode)} before the tunnel was "
            + $"up, so the service is marked FailedToStart and anything that waits for it will not start. "
            + $"{said}It is not retried: this is usually the Service name, the namespace, the context or the "
            + $"credentials, which a retry would repeat. Fix it and restart the resource.");

        await control.FailToStartAsync(control.LastOutput, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestartAsync(
        int? exitCode, bool wasReady, CancellationToken cancellationToken, CancellationToken hostStopping)
    {
        try
        {
            var said = await KubectlSaidAsync(cancellationToken).ConfigureAwait(false);
            var why = wasReady
                ? Raw.Compose($"exited{ExitCode(exitCode)} after it had been up")
                : Raw.Compose($"exited{ExitCode(exitCode)} again before it was up");

            while (true)
            {
                int attempt;

                lock (_gate)
                {
                    if (_restarts >= MaxRestarts)
                    {
                        _gaveUp = true;
                        attempt = 0;
                    }
                    else
                    {
                        attempt = ++_restarts;
                    }
                }

                if (attempt == 0)
                {
                    ServiceSourcesLog.Error(
                        logger,
                        $"Service '{new Name(serviceName)}': the kubectl port-forward {why} and {MaxRestarts} restarts in a row "
                        + $"did not bring it back, so it is left stopped. {said}Everything referencing this service has lost "
                        + $"it; once the cluster is reachable again, start the resource from the dashboard.");
                    return;
                }

                var delay = Backoff(attempt);

                ServiceSourcesLog.Warning(
                    logger,
                    $"Service '{new Name(serviceName)}': the kubectl port-forward {why}. {said}Restarting it in "
                    + $"{(int)delay.TotalSeconds}s (attempt {attempt} of {MaxRestarts}).");

                await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);

                // Only the host stopping may abort the call: cancelling a Start midway leaves the resource in
                // Starting with no process.
                var failure = await control.StartAsync(hostStopping).ConfigureAwait(false);

                if (failure is null)
                {
                    // Requested. What the new process does is for the snapshots that follow to say.
                    return;
                }

                ServiceSourcesLog.Warning(
                    logger,
                    $"Service '{new Name(serviceName)}': restarting the kubectl port-forward was refused: {Raw.Output(failure)}");
            }
        }
        catch (OperationCanceledException)
        {
            // Overruled by a Running or Starting snapshot, or the host is shutting down.
        }
    }

    /// <summary>What kubectl said last, as a sentence fragment for a log line — empty when it said nothing.</summary>
    private async Task<Raw> KubectlSaidAsync(CancellationToken cancellationToken)
    {
        // The log stream and the state stream are independent, so kubectl's last words can arrive a
        // moment after the exit they explain.
        if (outputSettle > TimeSpan.Zero)
        {
            await Task.Delay(outputSettle, timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return control.LastOutput is { Length: > 0 } line
            ? Raw.Compose($"kubectl said: {Raw.Output(line)}. ")
            : Raw.Literal("");
    }

    private static Raw ExitCode(int? exitCode) =>
        exitCode is { } code ? Raw.Compose($" with exit code {code}") : Raw.Literal("");

    private static bool Is(string state, string known) => string.Equals(state, known, StringComparison.Ordinal);

    /// <summary>The last thing kubectl wrote, preferring its error stream.</summary>
    internal sealed class TunnelOutput
    {
        private volatile string? _lastError;
        private volatile string? _lastAny;

        public string? Last => _lastError ?? _lastAny;

        public async Task ConsumeAsync(IAsyncEnumerable<IReadOnlyList<LogLine>> batches, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (var batch in batches.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    foreach (var line in batch)
                    {
                        if (string.IsNullOrWhiteSpace(line.Content))
                        {
                            continue;
                        }

                        _lastAny = line.Content;

                        if (line.IsErrorMessage)
                        {
                            _lastError = line.Content;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class AspireControl(
        IResource resource,
        ResourceNotificationService notifications,
        ResourceCommandService commands,
        TunnelOutput output) : IPortForwardControl
    {
        public string? LastOutput => output.Last;

        public async Task<string?> StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                var result = await commands
                    .ExecuteCommandAsync(resource, KnownResourceCommands.StartCommand, cancellationToken)
                    .ConfigureAwait(false);

                return result.Success ? null : result.Message ?? "no reason given";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return ex.Message;
            }
        }

        public Task FailToStartAsync(string? kubectlError, CancellationToken cancellationToken) =>
            notifications.PublishUpdateAsync(resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error),
                Properties = kubectlError is null
                    ? snapshot.Properties
                    : snapshot.Properties.Add(new ResourcePropertySnapshot("kubectl.error", kubectlError)),
            });
    }
}
