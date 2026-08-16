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
        var projectPath = ResolveProjectPath(serviceName, metadata, config, cacheDirectory, builder.AppHostDirectory, gitClient);

        var projectBuilder = builder.AddProject(serviceName, projectPath);
        return ServiceResource.CreateFacade(builder, serviceName, projectBuilder);
    }

    internal static string ResolveProjectPath(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        string cacheDirectory,
        string appHostDirectory,
        IGitClient gitClient)
    {
        string repoRoot;

        if (config.Path is not null)
        {
            if (config.Ref is not null)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': 'ref' cannot be combined with 'path' — 'path' points directly at " +
                    "an existing checkout, and 'ref' only applies when this tool manages the clone.");
            }

            // Anchor a relative `path` override to the AppHost directory (matching Aspire's own
            // AddProject behavior), not to the process's current working directory.
            // Path.GetFullPath is a no-op when config.Path is already absolute.
            repoRoot = Path.GetFullPath(config.Path, appHostDirectory);
        }
        else
        {
            var repoName = GetRepositoryName(metadata.Repository);
            repoRoot = Path.Combine(cacheDirectory, repoName);

            if (!Directory.Exists(repoRoot))
            {
                try
                {
                    gitClient.Clone(metadata.Repository, repoRoot);
                }
                catch (Exception ex)
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{serviceName}': failed to clone repository '{metadata.Repository}' into '{repoRoot}'.", ex);
                }

                var reference = config.Ref ?? metadata.DefaultRef;
                if (reference is not null)
                {
                    try
                    {
                        gitClient.Checkout(repoRoot, reference);
                    }
                    catch (Exception ex)
                    {
                        throw new ServiceSourcesConfigurationException(
                            $"Service '{serviceName}': failed to checkout ref '{reference}' of repository '{metadata.Repository}' at '{repoRoot}'.", ex);
                    }
                }
            }
            else
            {
                // No auto-pull/update of an already-cloned repo: only read the existing clone's
                // "origin" remote URL (no fetch/pull) to guard against a basename collision
                // between different repositories (e.g. same repo name under different orgs/hosts).
                var existingOrigin = gitClient.GetOriginUrl(repoRoot);
                if (existingOrigin is not null && !RepositoryUrlsMatch(existingOrigin, metadata.Repository))
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{serviceName}': cache directory '{repoRoot}' already contains a clone of " +
                        $"'{existingOrigin}', which does not match the configured repository '{metadata.Repository}'. " +
                        "Remove the cache directory or fix the configured repository URL.");
                }
            }
        }

        var projectPath = Path.Combine(repoRoot, metadata.Project);
        if (!File.Exists(projectPath))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': project file '{metadata.Project}' was not found under '{repoRoot}'.");
        }

        return projectPath;
    }

    private static bool RepositoryUrlsMatch(string a, string b) =>
        string.Equals(NormalizeRepositoryUrl(a), NormalizeRepositoryUrl(b), StringComparison.Ordinal);

    private static string NormalizeRepositoryUrl(string repositoryUrl)
    {
        var trimmed = repositoryUrl.TrimEnd('/');
        return trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }

    private static string GetRepositoryName(string repositoryUrl)
    {
        var trimmed = repositoryUrl.TrimEnd('/');
        var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return lastSegment.EndsWith(".git") ? lastSegment[..^4] : lastSegment;
    }
}
