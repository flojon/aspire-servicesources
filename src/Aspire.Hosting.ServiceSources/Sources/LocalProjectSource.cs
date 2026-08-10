using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class LocalProjectSource(IGitClient gitClient) : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var cacheDirectory = ServiceSourcesConfigCache.GetCacheDirectory(builder);
        var projectPath = ResolveProjectPath(metadata, config, cacheDirectory, gitClient);

        var projectBuilder = builder.AddProject(serviceName, projectPath);
        return ServiceResource.CreateFacade(builder, serviceName, projectBuilder);
    }

    internal static string ResolveProjectPath(
        ServiceMetadata metadata, ServiceDeveloperConfig config, string cacheDirectory, IGitClient gitClient)
    {
        string repoRoot;

        if (config.Path is not null)
        {
            repoRoot = config.Path;
        }
        else
        {
            var repoName = GetRepositoryName(metadata.Repository);
            repoRoot = Path.Combine(cacheDirectory, repoName);

            if (!Directory.Exists(repoRoot))
            {
                gitClient.Clone(metadata.Repository, repoRoot);

                var reference = config.Ref ?? metadata.DefaultRef;
                if (reference is not null)
                {
                    gitClient.Checkout(repoRoot, reference);
                }
            }
        }

        var projectPath = Path.Combine(repoRoot, metadata.Project);
        if (!File.Exists(projectPath))
        {
            throw new ServiceSourcesConfigurationException(
                $"Project file '{metadata.Project}' was not found under '{repoRoot}'.");
        }

        return projectPath;
    }

    private static string GetRepositoryName(string repositoryUrl)
    {
        var trimmed = repositoryUrl.TrimEnd('/');
        var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return lastSegment.EndsWith(".git") ? lastSegment[..^4] : lastSegment;
    }
}
