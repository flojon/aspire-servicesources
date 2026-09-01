using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceSourcesConfigCache
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, ConfigLoader> Cache = new();

    /// <summary>
    /// The whole loaded configuration, for callers that work across services rather than resolving
    /// one — the parallel checkout prefetch, which needs the full set of <c>"local"</c> services
    /// before any of them has been asked for by name.
    /// </summary>
    public static LoadedConfig LoadedFor(IDistributedApplicationBuilder builder) =>
        // The factory has to stay free of side effects: ConditionalWeakTable.GetValue may run it
        // concurrently for the same key and keep only one of the results, so anything it did on the
        // builder would be done twice while only one instance survived to say it had happened.
        // Loading behind a lock on the instance that actually won is the shape LocalCheckoutPrefetch
        // uses, for the same reason. Registering servicesources.local.json is guarded that way
        // too — see DeveloperConfigFileSource, which owns it because an entry point registers it
        // before any of this runs.
        Cache.GetValue(builder, static _ => new ConfigLoader()).Load(builder);

    public static (ServiceMetadata Metadata, ServiceDeveloperConfig DeveloperConfig) ResolveService(
        IDistributedApplicationBuilder builder, string serviceName)
    {
        var loaded = LoadedFor(builder);

        if (!loaded.Catalog.Services.TryGetValue(serviceName, out var metadata))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' was not found in 'servicesources.yaml'.");
        }

        if (!loaded.DeveloperConfig.Services.TryGetValue(serviceName, out var developerConfig))
        {
            throw loaded.DeveloperConfig.NotConfiguredError(serviceName);
        }

        return (metadata, developerConfig);
    }

    /// <summary>
    /// One builder's slot in <see cref="Cache"/>. Holding the load behind this rather than in the
    /// table's factory is what makes it happen exactly once per builder.
    /// </summary>
    private sealed class ConfigLoader
    {
        // Plain object rather than System.Threading.Lock: this package still targets net8.0.
        private readonly object _gate = new();

        private LoadedConfig? _loaded;

        /// <summary>
        /// A load that throws leaves the slot empty, so the next caller tries again and fails the
        /// same way — a configuration error has to keep being reported to whoever asks, rather than
        /// being cached as a success that never happened or silently swallowed after the first call.
        /// </summary>
        public LoadedConfig Load(IDistributedApplicationBuilder builder)
        {
            lock (_gate)
            {
                return _loaded ??= LoadedConfig.Load(builder);
            }
        }
    }

    internal sealed class LoadedConfig
    {
        public required ServiceCatalog Catalog { get; init; }

        public required DeveloperConfiguration DeveloperConfig { get; init; }

        public static LoadedConfig Load(IDistributedApplicationBuilder builder)
        {
            var catalog = ServiceCatalogLoader.Load(Path.Combine(builder.AppHostDirectory, "servicesources.yaml"));

            // The catalog first, and its names handed over: it decides how a service is spelled, and
            // the developer config's keys arrive from providers that may spell it differently.
            return new LoadedConfig
            {
                Catalog = catalog,
                DeveloperConfig = DeveloperConfiguration.ReadFrom(builder, catalog.Services.Keys),
            };
        }
    }
}
