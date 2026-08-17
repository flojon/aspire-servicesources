using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class LocalProjectSource(IGitClient gitClient) : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var facade = ServiceResource.CreateEmptyFacade(builder, serviceName);

        PendingLocalResolutions.For(builder).Add(new PendingResolution(serviceName, metadata, config, facade, gitClient));

        return facade;
    }

    internal static string ResolveProjectPath(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
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
            EnsureGitignore(appHostDirectory);
            repoRoot = Path.Combine(appHostDirectory, ".servicesources", "checkouts", serviceName);
            var reference = config.Ref ?? metadata.DefaultRef;

            if (!Directory.Exists(Path.Combine(repoRoot, ".git")))
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

                if (reference is not null)
                {
                    CheckoutWithFetchRetry(serviceName, metadata, repoRoot, reference, gitClient);
                }
            }
            else
            {
                var existingOrigin = gitClient.GetOriginUrl(repoRoot);
                if (existingOrigin is not null && !RepositoryUrlsMatch(existingOrigin, metadata.Repository))
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{serviceName}': checkout at '{repoRoot}' already contains a clone of " +
                        $"'{existingOrigin}', which does not match the configured repository '{metadata.Repository}'. " +
                        "Remove the checkout directory or fix the configured repository URL.");
                }

                if (reference is not null)
                {
                    if (gitClient.HasUncommittedChanges(repoRoot))
                    {
                        if (!gitClient.IsRefCheckedOut(repoRoot, reference))
                        {
                            throw new ServiceSourcesConfigurationException(
                                $"Service '{serviceName}': checkout at '{repoRoot}' has uncommitted changes and is not " +
                                $"on the configured ref '{reference}'. Commit or stash your changes, then re-run.");
                        }
                    }
                    else if (!gitClient.IsRefCheckedOut(repoRoot, reference))
                    {
                        CheckoutWithFetchRetry(serviceName, metadata, repoRoot, reference, gitClient);
                    }
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

    private static void CheckoutWithFetchRetry(
        string serviceName, ServiceMetadata metadata, string repoRoot, string reference, IGitClient gitClient)
    {
        try
        {
            gitClient.Checkout(repoRoot, reference);
            return;
        }
        catch (ServiceSourcesConfigurationException)
        {
            // Ref not resolvable from local data; fall through to fetch-and-retry below.
        }
        catch (Exception ex)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': failed to checkout ref '{reference}' of repository '{metadata.Repository}' at '{repoRoot}'.", ex);
        }

        try
        {
            gitClient.Fetch(repoRoot);
        }
        catch (Exception ex)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': failed to fetch repository '{metadata.Repository}' at '{repoRoot}' " +
                $"while resolving ref '{reference}'.", ex);
        }

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

    private static void EnsureGitignore(string appHostDirectory)
    {
        var dir = Path.Combine(appHostDirectory, ".servicesources");
        Directory.CreateDirectory(dir);

        var gitignorePath = Path.Combine(dir, ".gitignore");
        try
        {
            // FileMode.CreateNew is atomic: it fails if the file already exists, which makes
            // this safe against concurrent resolution of multiple services (see
            // PendingLocalResolutions, which resolves them in parallel) racing to create it.
            using var stream = new FileStream(gitignorePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write("*\n!.gitignore\n");
        }
        catch (IOException)
        {
            // Already created by a concurrent resolution or a prior run — leave it as-is.
        }
    }

    private static bool RepositoryUrlsMatch(string a, string b) =>
        string.Equals(NormalizeRepositoryUrl(a), NormalizeRepositoryUrl(b), StringComparison.Ordinal);

    private static string NormalizeRepositoryUrl(string repositoryUrl)
    {
        var trimmed = repositoryUrl.TrimEnd('/');
        return trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? trimmed[..^4] : trimmed;
    }
}
