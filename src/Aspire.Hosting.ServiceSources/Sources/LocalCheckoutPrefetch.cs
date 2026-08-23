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
/// catalog is skipped, and a checkout that throws has its exception stored and re-thrown only if
/// that service is actually requested.
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
/// read-only operation: a service with no checkout yet is <b>cloned</b>, and an existing
/// tool-managed checkout that is not on its configured ref is <b>fetched and checked out</b> onto
/// it. Speculation therefore spends network and disk, and applies the developer's configured ref,
/// for services this AppHost never adds — bounded only by what
/// <c>servicesources.local.json</c> marks <c>"local"</c>. Two things keep that acceptable rather
/// than merely tolerated: the file is per-developer and gitignored, so its scope is already this
/// developer and this AppHost, and the ref reconciliation is what that same file asked for, refused
/// outright by <see cref="Git.LocalGitCheckout"/> when the checkout has uncommitted changes. What
/// remains — a cold clone of a repository that goes unused — is reported at
/// <c>BeforeStartEvent</c> by <see cref="ReportUnusedCheckouts"/> rather than paid silently, since
/// the remedy is for the developer to drop the entries they never <c>AddService()</c>.
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
                ReportUnusedCheckouts(@event.Services);
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
            lock (_gate)
            {
                var unused = _checkouts.Keys
                    .Where(name => !_requested.Contains(name))
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToArray();

                if (unused.Length == 0)
                {
                    return null;
                }

                return $"servicesources.local.json marks {unused.Length} " +
                       $"{(unused.Length == 1 ? "service" : "services")} as 'local' that this AppHost never " +
                       $"adds ({string.Join(", ", unused)}). Their git checkouts were cloned, and reconciled to " +
                       "their configured ref, anyway: AddService() has to hand back the real resource, so every " +
                       "'local' entry is prefetched in parallel before the AppHost says which ones it wants. " +
                       "Remove the entries you don't call AddService() for to stop paying for them.";
            }
        }
    }

    /// <summary>
    /// Reports the cost of the speculative part of the prefetch, so a first run that quietly clones
    /// repositories the AppHost never uses reads as a cost rather than as a hang.
    /// </summary>
    private void ReportUnusedCheckouts(IServiceProvider services)
    {
        if (UnusedCheckoutsMessage is not { } message)
        {
            return;
        }

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Aspire.Hosting.ServiceSources");

        logger?.LogInformation("{ServiceSourcesNotice}", message);
    }

    /// <summary>
    /// The checkout directory for <paramref name="serviceName"/>, re-throwing the failure the
    /// parallel phase recorded for it.
    /// </summary>
    public string GetRepoRoot(string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config,
        string appHostDirectory, IGitClient gitClient)
    {
        lock (_gate)
        {
            _requested.Add(serviceName);
        }

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

        return result.RepoRoot!;
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
            .Where(entry => string.Equals(entry.Value.Source, "local", StringComparison.Ordinal))
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
                    var repoRoot = LocalGitCheckout.ResolveRepoRoot(
                        candidate.Name, candidate.Metadata, candidate.Config, appHostDirectory, gitClient);
                    return new CheckoutResult(repoRoot, null);
                }
                catch (Exception ex)
                {
                    // Captured, never thrown from the task itself: this service may never be
                    // requested, and a faulted task nobody awaits is an unobserved exception.
                    return new CheckoutResult(null, ex);
                }
            });
        }
    }

    private sealed record CheckoutResult(string? RepoRoot, Exception? Exception);
}
