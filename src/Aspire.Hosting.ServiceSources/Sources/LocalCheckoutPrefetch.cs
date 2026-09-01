using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Resolves the git checkout for every <c>"local"</c>-sourced service in parallel, once, on the
/// first <c>AddService()</c> call.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddService()</c> has to hand back the real resource, so a <c>"local"</c> service can no longer
/// wait for <c>BeforeStartEvent</c> to be resolved. Cloning each service as it is asked for would
/// serialize every cold clone — the tax issue #2 removed. Instead the trigger moves rather than the
/// parallelism: the first call prefetches all of them at once, so wall-clock stays
/// <c>max(checkout)</c>, and every later call finds its checkout already done.
/// </para>
/// <para>
/// The prefetch set comes from <c>servicesources.local.json</c>, which must already list every
/// service the AppHost adds. The converse does not hold — the file may mark services <c>"local"</c>
/// that this AppHost never calls <c>AddService()</c> for — so the prefetch is <b>speculative</b> in
/// both what it does and what it reports. It must never invent a failure: a service missing from the
/// catalog is skipped, and a checkout that throws has its exception stored, re-thrown only if that
/// service is actually requested, and merely logged when it is not.
/// </para>
/// <para>
/// Nothing here blocks on the speculative part. Each checkout is its own task and
/// <see cref="GetRepoRoot"/> waits only on the one it was asked for, so a developer whose config
/// marks ten services <c>"local"</c> while the AppHost adds two waits for those two, not for all
/// ten. There is no way to narrow the set itself: <c>AddService()</c> is called one service at a
/// time and must return the real resource before the next call happens, so the prefetch cannot see
/// the calls that have not happened yet.
/// </para>
/// <para>
/// Free of waiting is not free of cost, and the difference matters. Resolving a checkout is not a
/// read-only operation, so speculation does only the half of it that cannot destroy anything: a
/// service with no checkout yet is <b>cloned</b>, and a checkout that already exists is left exactly
/// as it was found. Moving one onto its configured ref — a fetch and a checkout inside a working
/// tree this run did not create — is deferred to <see cref="GetRepoRoot"/>, which runs only for the
/// services the AppHost really added. Without that split, a developer with committed work on a
/// branch of a service this AppHost never adds would find it checked out back onto the configured
/// ref by a run that never mentioned that service, on the strength of a config entry alone. The
/// price is that ref reconciliation is serial across services where it used to be parallel; the
/// clone, which is the part that actually costs time, stays parallel.
/// </para>
/// <para>
/// What remains speculative is a cold clone of a repository that goes unused, and any failure of
/// one. Both are reported at <c>BeforeStartEvent</c> by <see cref="ReportSpeculativeWork"/> rather
/// than paid silently: the cost, because the remedy is for the developer to drop the entries they
/// never <c>AddService()</c>, and the failures, because nothing else ever mentions them —
/// <see cref="GetRepoRoot"/> re-throws only for a service that was actually asked for.
/// </para>
/// </remarks>
internal sealed class LocalCheckoutPrefetch
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, LocalCheckoutPrefetch> Cache = new();

    private readonly Dictionary<string, Task<CheckoutResult>> _checkouts = new(StringComparer.Ordinal);

    /// <summary>
    /// Services <see cref="GetRepoRoot"/> was actually asked for — the set the prefetch could not
    /// know up front, learned by the time <c>BeforeStartEvent</c> runs.
    /// </summary>
    private readonly HashSet<string> _requested = new(StringComparer.Ordinal);

    // Plain object rather than System.Threading.Lock: this package still targets net8.0.
    private readonly object _gate = new();

    private bool _started;

    public static LocalCheckoutPrefetch For(
        IDistributedApplicationBuilder builder, IGitClient gitClient)
    {
        // The factory has to stay free of side effects. ConditionalWeakTable.GetValue may run it
        // concurrently for the same key and keep only one of the results, so starting the clones
        // in there would let a discarded instance race the surviving one into the same checkout
        // directories. Starting them behind a lock on the instance that actually won is the shape
        // UrlSource's CheckRegistration uses for the same reason.
        var prefetch = Cache.GetValue(builder, _ => new LocalCheckoutPrefetch());

        prefetch.EnsureStarted(builder, gitClient);

        return prefetch;
    }

    /// <summary>
    /// Starts the prefetch once per builder. Returns only after every checkout task has been
    /// created, so <see cref="_checkouts"/> is fully populated — and thereafter read-only — before
    /// any caller can reach <see cref="GetRepoRoot"/>.
    /// </summary>
    private void EnsureStarted(IDistributedApplicationBuilder builder, IGitClient gitClient)
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;

            // Buffered to BeforeStartEvent for the same reason ServiceConfigurationWarnings is:
            // AddService() runs while the AppHost is still being composed, before there is an
            // ILogger to write to. It is also the first point at which the notice can be correct —
            // only once every AddService() call has happened is the unused set known.
            builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
            {
                ReportSpeculativeWork(@event.Services);
                return Task.CompletedTask;
            });

            Run(builder, gitClient);
        }
    }

    /// <summary>
    /// The notice for checkouts that were prefetched but never asked for, or <see langword="null"/>
    /// when the AppHost used everything <c>servicesources.local.json</c> marks <c>"local"</c>.
    /// Exposed for tests — the log itself isn't observable in-process.
    /// </summary>
    public string? UnusedCheckoutsMessage
    {
        get
        {
            var unused = UnusedCheckouts().Select(entry => entry.Name).ToArray();

            if (unused.Length == 0)
            {
                return null;
            }

            return $"servicesources.local.json marks {unused.Length} " +
                   $"{(unused.Length == 1 ? "service" : "services")} as 'local' that this AppHost never " +
                   $"adds ({string.Join(", ", unused)}). Cloning them was paid for anyway: AddService() has to " +
                   "hand back the real resource, so every 'local' entry is prefetched in parallel before the " +
                   "AppHost says which ones it wants. Only the services this AppHost adds are reconciled to " +
                   "their configured ref. Remove the entries you don't call AddService() for to stop paying " +
                   "for them.";
        }
    }

    /// <summary>
    /// Notices for speculative checkouts that failed for services this AppHost never added — the
    /// failures <see cref="GetRepoRoot"/> will never re-throw, because nothing asked for them. Only
    /// checkouts that have already finished are included: waiting on one would undo the deferral
    /// this class exists for. Exposed for tests — the log itself isn't observable in-process.
    /// </summary>
    public IReadOnlyList<string> FailedUnusedCheckoutMessages =>
        UnusedCheckouts()
            .Where(entry => entry.Checkout.IsCompleted && entry.Checkout.Result.Exception is not null)
            .Select(entry => FailedCheckoutMessage(entry.Name, entry.Checkout.Result.Exception!))
            .ToArray();

    /// <summary>
    /// The checkouts the prefetch started that no <c>AddService()</c> call ever asked for. Correct
    /// only once every <c>AddService()</c> call has happened, which is why the report waits for
    /// <c>BeforeStartEvent</c>.
    /// </summary>
    private (string Name, Task<CheckoutResult> Checkout)[] UnusedCheckouts()
    {
        lock (_gate)
        {
            return _checkouts
                .Where(entry => !_requested.Contains(entry.Key))
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => (entry.Key, entry.Value))
                .ToArray();
        }
    }

    /// <summary>
    /// Reports the speculative part of the prefetch: what it cost, and what failed inside it while
    /// nobody was waiting. A first run that quietly clones repositories the AppHost never uses
    /// should read as a cost rather than as a hang, and a clone that failed for one of those
    /// services has no other route to the developer at all.
    /// </summary>
    private void ReportSpeculativeWork(IServiceProvider services)
    {
        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Aspire.Hosting.ServiceSources");

        if (logger is null)
        {
            return;
        }

        if (UnusedCheckoutsMessage is { } message)
        {
            logger.LogInformation("{ServiceSourcesNotice}", message);
        }

        foreach (var (name, checkout) in UnusedCheckouts())
        {
            // A checkout nothing waits on may still be running here, and startup must not block on
            // one — so the failure is reported when it lands instead. ExecuteSynchronously runs the
            // continuation inline for those already finished, which is the common case by now.
            _ = checkout.ContinueWith(
                task => ReportFailedCheckout(logger, name, task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private static void ReportFailedCheckout(ILogger logger, string serviceName, Task<CheckoutResult> checkout)
    {
        // Never faulted — the worker captures its exception into the result — so this cannot throw.
        if (checkout.Result.Exception is not { } exception)
        {
            return;
        }

        try
        {
            logger.LogWarning(exception, "{ServiceSourcesNotice}", FailedCheckoutMessage(serviceName, exception));
        }
        catch (ObjectDisposedException)
        {
            // A continuation for a still-running clone can land after the host has torn its logging
            // down. There is nothing left to report to, and faulting a continuation nobody awaits
            // would be worse than losing the notice.
        }
    }

    private static string FailedCheckoutMessage(string serviceName, Exception exception) =>
        $"servicesources.local.json marks '{serviceName}' as 'local', so its git checkout was prefetched, and " +
        $"the prefetch failed: {exception.Message} This AppHost never adds '{serviceName}', so nothing else " +
        "reports it — remove the entry if you don't use it, or fix what the failure names.";

    /// <summary>
    /// Records that the AppHost really does add <paramref name="serviceName"/>, without waiting for
    /// its checkout.
    /// </summary>
    /// <remarks>
    /// For a service whose checkout is deferred past startup (see <see cref="DeferredCheckout"/>):
    /// its <see cref="GetRepoRoot"/> call happens after <c>BeforeStartEvent</c> has already decided
    /// what to report, so without this it would be named as speculative work nobody asked for.
    /// </remarks>
    public void MarkRequested(string serviceName)
    {
        lock (_gate)
        {
            _requested.Add(serviceName);
        }
    }

    /// <summary>
    /// The checkout directory for <paramref name="serviceName"/>, re-throwing the failure the
    /// parallel phase recorded for it.
    /// </summary>
    public string GetRepoRoot(string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config,
        string appHostDirectory, IGitClient gitClient)
    {
        MarkRequested(serviceName);

        if (!_checkouts.TryGetValue(serviceName, out var checkout))
        {
            // Not in the prefetch set — the developer config was loaded before this service was
            // added to it, or the service is being resolved through a path the prefetch doesn't
            // enumerate. Resolve it directly rather than failing.
            return LocalGitCheckout.ResolveRepoRoot(serviceName, metadata, config, appHostDirectory, gitClient);
        }

        // Waits on this service's checkout only. The other prefetched checkouts keep running in the
        // background; a service this AppHost never asks for is never waited on.
        var result = checkout.GetAwaiter().GetResult();

        if (result.Exception is not null)
        {
            // Capture/Throw rather than `throw result.Exception`, which would overwrite the stack
            // trace from the worker the clone actually failed on with this call site — and would
            // mangle the same instance again if two services share a checkout failure.
            ExceptionDispatchInfo.Capture(result.Exception).Throw();
        }

        // The prefetch stopped at "the checkout exists". Reconciling it onto the configured ref
        // mutates a working tree, so it happens here — on the thread of the AddService call that
        // asked for this service, and never for a service the AppHost turns out not to add.
        return LocalGitCheckout.ReconcileRepoRoot(
            serviceName, metadata, config, result.Checkout!.Value, gitClient);
    }

    private void Run(IDistributedApplicationBuilder builder, IGitClient gitClient)
    {
        ServiceSourcesConfigCache.LoadedConfig config;
        try
        {
            config = ServiceSourcesConfigCache.LoadedFor(builder);
        }
        catch (ServiceSourcesConfigurationException)
        {
            // Speculation must never be the thing that fails the AddService call. A config problem
            // real enough to matter is reported by ResolveService, against the service the AppHost
            // actually asked for; here it just means there is nothing to prefetch.
            return;
        }

        var appHostDirectory = builder.AppHostDirectory;

        var candidates = config.DeveloperConfig.Services
            // Case-insensitive to agree with how AddService resolves the same value: a service
            // spelled "Local" is one AddService will resolve locally, and dropping it here would
            // leave its clone to run alone on the AddService thread rather than with the others.
            .Where(entry => string.Equals(entry.Value.Source, "local", StringComparison.OrdinalIgnoreCase))
            // A service the developer marked "local" but that the catalog doesn't describe can't be
            // checked out and isn't this phase's problem to report — AddService still rejects it
            // properly if the AppHost actually asks for it.
            .Where(entry => config.Catalog.Services.ContainsKey(entry.Key))
            .Select(entry => (Name: entry.Key, Metadata: config.Catalog.Services[entry.Key], Config: entry.Value))
            .ToArray();

        if (candidates.Length == 0)
        {
            return;
        }

        foreach (var candidate in candidates)
        {
            _checkouts[candidate.Name] = Task.Run(() =>
            {
                try
                {
                    // Prepare, not resolve: cloning what is missing is safe to do for a service that
                    // may never be added, but reconciling an existing checkout is not — see
                    // GetRepoRoot, which finishes the job for the services that are.
                    var prepared = LocalGitCheckout.PrepareRepoRoot(
                        candidate.Name, candidate.Metadata, candidate.Config, appHostDirectory, gitClient);
                    return new CheckoutResult(prepared, null);
                }
                catch (Exception ex)
                {
                    // Captured, never thrown from the task itself: this service may never be
                    // requested, and a faulted task nobody awaits is an unobserved exception. If it
                    // is never requested, ReportSpeculativeWork is what surfaces this.
                    return new CheckoutResult(null, ex);
                }
            });
        }
    }

    private sealed record CheckoutResult(LocalGitCheckout.PreparedCheckout? Checkout, Exception? Exception);
}
