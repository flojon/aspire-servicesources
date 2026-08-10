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

    public static string GetCacheDirectory(IDistributedApplicationBuilder builder)
    {
        var loaded = Cache.GetValue(builder, static b => LoadedConfig.Load(b.AppHostDirectory));
        var configured = loaded.DeveloperConfig.CacheDirectory ?? "~/.servicesources/repos";
        var expanded = ExpandHome(configured);

        // A `~`-expanded path is already absolute (anchored to the user's home directory) and
        // must not be re-anchored. A genuinely relative path (no `~`) is anchored to the AppHost
        // directory rather than the process's current working directory. Path.GetFullPath is a
        // no-op for a path that is already absolute, so this is safe for both cases.
        return Path.GetFullPath(expanded, builder.AppHostDirectory);
    }

    private static string ExpandHome(string path)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path.TrimStart('~', '/', '\\'));
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
