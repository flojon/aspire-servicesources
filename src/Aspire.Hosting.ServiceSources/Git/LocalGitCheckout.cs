using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Resolves a service's local checkout directory — cloning it if necessary, checking out the
/// configured ref — shared by every local-source kind (the built-in <c>dotnet</c> kind and any
/// kind registered via <see cref="ILocalResourceKind"/>). Language-agnostic: this never looks at
/// how the resulting checkout is actually run.
/// </summary>
internal static class LocalGitCheckout
{
    public static string ResolveRepoRoot(
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

            // Only the built-in dotnet kind goes on to look for a project file underneath this
            // directory; every other kind hands repoRoot straight to its handler, so without this
            // check a typo'd override surfaces as an obscure failure inside that handler (or as a
            // resource with a nonsensical working directory) rather than as a named config error.
            if (!Directory.Exists(repoRoot))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': the 'path' override points at '{repoRoot}', which does not exist. " +
                    "'path' must name an existing local directory.");
            }
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

        return repoRoot;
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
