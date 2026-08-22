using System.Runtime.CompilerServices;
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
/// service the AppHost adds. It is therefore <b>speculative</b> — it may cover a service this
/// AppHost never calls <c>AddService()</c> for — so it must never invent a failure: a service
/// missing from the catalog is skipped, and a checkout that throws has its exception stored and
/// re-thrown only if that service is actually requested.
/// </para>
/// </remarks>
internal sealed class LocalCheckoutPrefetch
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, LocalCheckoutPrefetch> Cache = new();

    private readonly Dictionary<string, CheckoutResult> _results = new(StringComparer.Ordinal);

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
        if (!_results.TryGetValue(serviceName, out var result))
        {
            // Not in the prefetch set — the developer config was loaded before this service was
            // added to it, or the service is being resolved through a path the prefetch doesn't
            // enumerate. Resolve it directly rather than failing.
            return LocalGitCheckout.ResolveRepoRoot(serviceName, metadata, config, appHostDirectory, gitClient);
        }

        if (result.Exception is not null)
        {
            throw result.Exception;
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

        var checkouts = Task.WhenAll(candidates.Select(candidate => Task.Run(() =>
        {
            try
            {
                var repoRoot = LocalGitCheckout.ResolveRepoRoot(
                    candidate.Name, candidate.Metadata, candidate.Config, appHostDirectory, gitClient);
                return new CheckoutResult(candidate.Name, repoRoot, null);
            }
            catch (Exception ex)
            {
                // Stored, not thrown: this service may never be requested.
                return new CheckoutResult(candidate.Name, null, ex);
            }
        }))).GetAwaiter().GetResult();

        foreach (var checkout in checkouts)
        {
            _results[checkout.ServiceName] = checkout;
        }
    }

    private sealed record CheckoutResult(string ServiceName, string? RepoRoot, Exception? Exception);
}
