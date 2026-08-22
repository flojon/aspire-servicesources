using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceSourcesConfigCache
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, LoadedConfig> Cache = new();

    /// <summary>
    /// The whole loaded configuration, for callers that work across services rather than resolving
    /// one — the parallel checkout prefetch, which needs the full set of <c>"local"</c> services
    /// before any of them has been asked for by name.
    /// </summary>
    public static LoadedConfig LoadedFor(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static b => LoadedConfig.Load(b.AppHostDirectory));

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
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' was not found in 'servicesources.local.json'.");
        }

        return (metadata, developerConfig);
    }

    internal sealed class LoadedConfig
    {
        public required ServiceCatalog Catalog { get; init; }

        public required DeveloperConfigFile DeveloperConfig { get; init; }

        public static LoadedConfig Load(string appHostDirectory)
        {
            var catalog = ServiceCatalogLoader.Load(Path.Combine(appHostDirectory, "servicesources.yaml"));
            var developerConfig = DeveloperConfigLoader.Load(Path.Combine(appHostDirectory, "servicesources.local.json"));
            return new LoadedConfig { Catalog = catalog, DeveloperConfig = developerConfig };
        }
    }
}
