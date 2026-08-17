using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceSourcesConfigCache
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, LoadedConfig> Cache = new();

    public static (ServiceMetadata Metadata, ServiceDeveloperConfig DeveloperConfig) ResolveService(
        IDistributedApplicationBuilder builder, string serviceName)
    {
        var loaded = Cache.GetValue(builder, static b => LoadedConfig.Load(b.AppHostDirectory));

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

    private sealed class LoadedConfig
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
