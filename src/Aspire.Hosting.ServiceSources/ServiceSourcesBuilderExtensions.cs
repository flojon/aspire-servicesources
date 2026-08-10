using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources;

public static class ServiceSourcesBuilderExtensions
{
    private static readonly Dictionary<string, IServiceSource> Sources = new()
    {
        ["local"] = new LocalProjectSource(new LibGit2SharpGitClient()),
    };

    public static IResourceBuilder<IResourceWithServiceDiscovery> AddService(
        this IDistributedApplicationBuilder builder, string name)
    {
        var (metadata, developerConfig) = ServiceSourcesConfigCache.ResolveService(builder, name);

        if (!Sources.TryGetValue(developerConfig.Source, out var source))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{name}' has source '{developerConfig.Source}', which is not implemented yet.");
        }

        return source.Resolve(builder, name, metadata, developerConfig);
    }
}
