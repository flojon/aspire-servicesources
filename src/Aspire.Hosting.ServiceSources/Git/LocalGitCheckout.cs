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
            var checkoutsRoot = Path.Combine(appHostDirectory, ".servicesources", "checkouts");
            repoRoot = Path.Combine(checkoutsRoot, serviceName);
            var reference = config.Ref ?? metadata.DefaultRef;

            if (!Directory.Exists(Path.Combine(repoRoot, ".git")))
            {
                CloneIntoPlace(serviceName, metadata, checkoutsRoot, repoRoot, gitClient);

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

    /// <summary>
    /// Clones into a scratch directory alongside the destination and renames it into place, so an
    /// interrupted clone can never leave a half-populated <c>checkouts/&lt;service&gt;</c> behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cloning straight into the destination is not recoverable. A clone is not one filesystem
    /// operation, and nothing that runs it is guaranteed to finish — checkouts are prefetched on
    /// background threads (see <see cref="Sources.LocalCheckoutPrefetch"/>), so a Ctrl-C, or an
    /// unrelated <c>AddService</c> that throws and takes the host down with it, can stop a clone
    /// halfway. What survives is a directory with content but no <c>.git</c>: exactly the state
    /// that sends the next run back down this branch, where libgit2 refuses to clone into a
    /// non-empty directory ("exists and is not an empty directory"). The service would then be
    /// unresolvable on every subsequent run until someone deleted the directory by hand.
    /// </para>
    /// <para>
    /// Renaming sidesteps that. Scratch and destination share a parent, so the move is a single
    /// rename(2)/MoveFileEx and <c>checkouts/&lt;service&gt;</c> is only ever absent or a complete
    /// clone — never something in between.
    /// </para>
    /// </remarks>
    private static void CloneIntoPlace(
        string serviceName, ServiceMetadata metadata, string checkoutsRoot, string repoRoot, IGitClient gitClient)
    {
        Directory.CreateDirectory(checkoutsRoot);

        // Debris from a version that predates this method, or from a crash between here and the
        // rename below. Everything under .servicesources is tool-managed and gitignored, and a
        // checkout with no ".git" holds nothing worth keeping.
        if (Directory.Exists(repoRoot))
        {
            try
            {
                Directory.Delete(repoRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': the checkout at '{repoRoot}' is not a git repository — it is left over " +
                    "from an interrupted clone — and could not be removed automatically. Delete it and re-run.", ex);
            }
        }

        // Unique per attempt: two builders resolving the same service concurrently (xUnit does
        // exactly that) must not clone into a shared scratch directory.
        var scratch = Path.Combine(checkoutsRoot, $".incoming-{serviceName}-{Guid.NewGuid():N}");

        try
        {
            try
            {
                gitClient.Clone(metadata.Repository, scratch);
            }
            catch (Exception ex)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': failed to clone repository '{metadata.Repository}' into '{repoRoot}'.", ex);
            }

            try
            {
                Directory.Move(scratch, repoRoot);
            }
            catch (IOException) when (Directory.Exists(Path.Combine(repoRoot, ".git")))
            {
                // A concurrent resolution of the same service landed its clone first. Ours is
                // redundant rather than wrong — discard it below and use theirs.
            }
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                try
                {
                    Directory.Delete(scratch, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort, deliberately not fatal. A leaked scratch directory costs disk
                    // inside a gitignored, tool-managed tree and can never block a later clone:
                    // its name is unique per attempt and the destination name is untouched.
                    // Sweeping these on startup would be worse than leaking them — a second
                    // AppHost running concurrently would have its in-flight clone deleted.
                }
            }
        }
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
