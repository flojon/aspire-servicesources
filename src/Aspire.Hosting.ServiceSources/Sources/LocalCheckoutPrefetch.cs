using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

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
/// time and the prefetch cannot see the calls that have not happened yet.
/// </para>
/// </remarks>
internal sealed class LocalCheckoutPrefetch
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, LocalCheckoutPrefetch> Cache = new();

    private readonly Dictionary<string, Task<CheckoutResult>> _checkouts = new(StringComparer.Ordinal);

    public static LocalCheckoutPrefetch For(
        IDistributedApplicationBuilder builder, IGitClient gitClient) =>
        Cache.GetValue(builder, b =>
        {
            var prefetch = new LocalCheckoutPrefetch();
            prefetch.Run(b, gitClient);
            return prefetch;
        });

    /// <summary>
    /// The checkout directory for <paramref name="serviceName"/>, re-throwing the failure the
    /// parallel phase recorded for it.
    /// </summary>
    public string GetRepoRoot(string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config,
        string appHostDirectory, IGitClient gitClient)
    {
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
