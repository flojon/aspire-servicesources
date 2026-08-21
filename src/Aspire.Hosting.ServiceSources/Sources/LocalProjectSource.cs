using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class LocalProjectSource(IGitClient gitClient) : IServiceSource
{
    public IReadOnlySet<string> RelevantFields { get; } = new HashSet<string> { "path", "ref" };

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
                GitUrlValidator.EnsureSupported(serviceName, metadata.Repository);

                try
                {
                    gitClient.Clone(metadata.Repository, repoRoot);
                }
                catch (GitAuthenticationFailedException ex)
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{serviceName}': failed to clone repository '{metadata.Repository}' into " +
                        $"'{repoRoot}' — authentication failed. Configure credentials via a git credential " +
                        "helper (`git credential fill` must resolve them for this host) or the " +
                        "SERVICESOURCES_GIT_USERNAME/SERVICESOURCES_GIT_TOKEN environment variables.", ex);
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
        catch (GitAuthenticationFailedException ex)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': failed to fetch repository '{metadata.Repository}' at '{repoRoot}' " +
                $"while resolving ref '{reference}' — authentication failed. Configure credentials via a git " +
                "credential helper (`git credential fill` must resolve them for this host) or the " +
                "SERVICESOURCES_GIT_USERNAME/SERVICESOURCES_GIT_TOKEN environment variables.", ex);
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
        var trimmed = repositoryUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        // Normalize both URL forms (https://host/path) and scp-like SSH syntax
        // ([user@]host:path, e.g. git@github.com:example/orders) down to "host/path"
        // so an HTTPS remote and an SSH remote for the same repository compare equal.
        var schemeIndex = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            trimmed = trimmed[(schemeIndex + 3)..];
            var slashIndex = trimmed.IndexOf('/');
            var atIndex = trimmed.IndexOf('@');
            if (atIndex >= 0 && (slashIndex < 0 || atIndex < slashIndex))
            {
                trimmed = trimmed[(atIndex + 1)..];
            }
        }
        else
        {
            var colonIndex = trimmed.IndexOf(':');
            var slashIndex = trimmed.IndexOf('/');
            if (colonIndex >= 0 && (slashIndex < 0 || colonIndex < slashIndex))
            {
                var host = trimmed[..colonIndex];
                var atIndex = host.IndexOf('@');
                if (atIndex >= 0)
                {
                    host = host[(atIndex + 1)..];
                }

                trimmed = $"{host}/{trimmed[(colonIndex + 1)..]}";
            }
        }

        return trimmed.TrimEnd('/');
    }
}
