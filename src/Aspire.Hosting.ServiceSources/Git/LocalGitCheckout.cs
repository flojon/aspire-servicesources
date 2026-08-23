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
        // Every message below names the repository, and a repository URL may carry a token. Redact
        // once here so no message site has to remember to; the real URL still goes to git itself.
        var displayRepository = GitUrl.Redact(metadata.Repository);

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
                // Validated before any work is done, so an unsupported URL fails fast rather than
                // after a sweep and a directory create.
                GitUrlValidator.EnsureSupported(serviceName, metadata.Repository);

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
                        $"'{GitUrl.Redact(existingOrigin)}', which does not match the configured repository " +
                        $"'{displayRepository}'. " +
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
        // See ResolveRepoRoot: the real URL goes to git, the redacted one goes into messages.
        var displayRepository = GitUrl.Redact(metadata.Repository);

        Directory.CreateDirectory(checkoutsRoot);
        SweepAbandonedScratchDirectories(checkoutsRoot);

        // Reached because ".git" is not a *directory*, which is not the same as "no repository":
        // ".git" is a file for a linked worktree ("git worktree add") and for a clone made with
        // --separate-git-dir. Both are complete checkouts that can hold uncommitted work, so the
        // delete further below would destroy one. Only debris — content with no ".git" entry at all
        // — is removable; anything else keeps the non-destructive error this branch used to produce.
        // Refused up here, before the clone, so the download is never paid for: unlike the
        // concurrent-clone collision below, this state is the user's own and cannot appear midway.
        if (File.Exists(Path.Combine(repoRoot, ".git")))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the checkout at '{repoRoot}' has a '.git' file rather than a '.git' " +
                "directory, so it is a linked worktree or a clone made with --separate-git-dir rather than a " +
                "checkout this tool cloned. Move it aside and re-run to have it cloned fresh, or point the " +
                "service at it with the 'path' override in servicesources.local.json.");
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
            catch (GitAuthenticationFailedException ex)
            {
                throw new ServiceSourcesConfigurationException(
                    AuthFailureMessage(
                        $"Service '{serviceName}': failed to clone repository '{displayRepository}' " +
                        $"into '{repoRoot}'"),
                    ex);
            }
            catch (Exception ex)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': failed to clone repository '{displayRepository}' into '{repoRoot}'.", ex);
            }

            // What happens to the destination is decided here, after the clone, rather than
            // inherited from the "no .git directory" probe in ResolveRepoRoot. By now that probe is
            // as old as a clone plus the sweep above — seconds or minutes — and a second AppHost
            // resolving the same service (a restart while the first is still starting, or two
            // "aspire run"s over one AppHost directory) can have landed a complete checkout in the
            // meantime. Deleting on the strength of the stale probe would destroy that checkout,
            // including work only it has. Deciding immediately before the rename narrows the window
            // to these adjacent operations, and means nothing is removed until its replacement is
            // already in hand.
            if (Directory.Exists(Path.Combine(repoRoot, ".git")))
            {
                // A concurrent resolution of the same service landed its clone first. Ours is
                // redundant rather than wrong — discard it in the finally and use theirs.
                return;
            }

            // Debris from a version that predates this method, or from a crash before the rename
            // below. Everything under .servicesources is tool-managed and gitignored, and a
            // checkout with no ".git" entry holds nothing worth keeping.
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

            try
            {
                Directory.Move(scratch, repoRoot);
            }
            catch (IOException) when (Directory.Exists(Path.Combine(repoRoot, ".git")))
            {
                // The same collision one instant later: the check above and this rename are not one
                // atomic operation. Ours is redundant rather than wrong — discard it and use theirs.
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
                    // SweepAbandonedScratchDirectories collects it on a later run.
                }
            }
        }
    }

    /// <summary>
    /// How long a scratch directory must have gone untouched before it counts as abandoned.
    /// </summary>
    /// <remarks>
    /// The <c>finally</c> in <see cref="CloneIntoPlace"/> removes the scratch directory on every
    /// path it controls, but it does not run when the process is killed — and checkouts are cloned
    /// speculatively on background threads for every <c>"local"</c> service in
    /// <c>servicesources.local.json</c>, including ones this AppHost never calls <c>AddService</c>
    /// for (see <see cref="Sources.LocalCheckoutPrefetch"/>). A Ctrl-C during startup, or the host
    /// exiting while an unrequested clone is still in flight, therefore leaks a partial copy of a
    /// repository that nothing would otherwise remove.
    /// <para>
    /// Sweeping on age rather than on liveness is what makes this safe. A concurrent AppHost's
    /// in-flight clone is seconds or minutes old, so it can never be mistaken for debris, and
    /// nothing is still cloning a day later — which is why this is not the "sweeping would delete a
    /// live clone" hazard that an unconditional startup sweep would be.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan AbandonedScratchAge = TimeSpan.FromHours(24);

    /// <summary>
    /// Removes <c>.incoming-*</c> directories left behind by clones that were killed rather than
    /// completed. Best effort throughout: a scratch directory that cannot be removed costs disk in a
    /// gitignored, tool-managed tree and can never block a clone, so failing here must not fail
    /// resolution.
    /// </summary>
    private static void SweepAbandonedScratchDirectories(string checkoutsRoot)
    {
        try
        {
            foreach (var scratch in Directory.EnumerateDirectories(checkoutsRoot, ".incoming-*"))
            {
                try
                {
                    if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(scratch) < AbandonedScratchAge)
                    {
                        continue;
                    }

                    Directory.Delete(scratch, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held open by something, or not ours to delete. Leave it for a later run.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The enumeration itself failed, so there is nothing we can sweep.
        }
    }

    private static void CheckoutWithFetchRetry(
        string serviceName, ServiceMetadata metadata, string repoRoot, string reference, IGitClient gitClient)
    {
        // See ResolveRepoRoot: the real URL goes to git, the redacted one goes into messages.
        var displayRepository = GitUrl.Redact(metadata.Repository);

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
                $"Service '{serviceName}': failed to checkout ref '{reference}' of repository '{displayRepository}' at '{repoRoot}'.", ex);
        }

        // The fetch talks to the checkout's own origin, which need not be the configured
        // `repository` (a pre-existing checkout, or a `repository` edited after the initial clone),
        // so validate the URL actually about to be used — the clone path's check upfront doesn't
        // cover it, and an SSH remote would otherwise fail with an opaque native error.
        GitUrlValidator.EnsureSupported(serviceName, gitClient.GetOriginUrl(repoRoot) ?? metadata.Repository);

        try
        {
            gitClient.Fetch(repoRoot);
        }
        catch (GitAuthenticationFailedException ex)
        {
            throw new ServiceSourcesConfigurationException(
                AuthFailureMessage(
                    $"Service '{serviceName}': failed to fetch repository '{displayRepository}' at " +
                    $"'{repoRoot}' while resolving ref '{reference}'"),
                ex);
        }
        catch (Exception ex)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': failed to fetch repository '{displayRepository}' at '{repoRoot}' " +
                $"while resolving ref '{reference}'.", ex);
        }

        try
        {
            gitClient.Checkout(repoRoot, reference);
        }
        catch (Exception ex)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': failed to checkout ref '{reference}' of repository '{displayRepository}' at '{repoRoot}'.", ex);
        }
    }

    /// <summary>
    /// Appends the shared authentication remediation to <paramref name="failureDescription"/>.
    /// Worded to cover both readings of the underlying failure: hosts commonly answer an
    /// unauthenticated request for a private repository with "not found" rather than "unauthorized"
    /// (see <see cref="LibGit2SharpGitClient.LooksLikeAuthFailure"/>), so the message must not
    /// assert that credentials were definitely rejected.
    /// </summary>
    private static string AuthFailureMessage(string failureDescription) =>
        $"{failureDescription} — authentication failed, or the repository is not visible to the " +
        "credentials in use. Configure credentials via a git credential helper (`git credential " +
        "fill` must resolve them for this host) or the SERVICESOURCES_GIT_USERNAME/" +
        "SERVICESOURCES_GIT_TOKEN environment variables.";

    private static void EnsureGitignore(string appHostDirectory)
    {
        var dir = Path.Combine(appHostDirectory, ".servicesources");
        Directory.CreateDirectory(dir);

        var gitignorePath = Path.Combine(dir, ".gitignore");
        try
        {
            // FileMode.CreateNew is atomic: it fails if the file already exists, which makes
            // this safe against concurrent resolution of multiple services (see
            // Sources.LocalCheckoutPrefetch, which clones them in parallel) racing to create it.
            using var stream = new FileStream(gitignorePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write("*\n!.gitignore\n");
        }
        catch (IOException)
        {
            // Already created by a concurrent resolution or a prior run — leave it as-is.
        }
    }

    // GitUrl.Identity reduces both URL forms (https://host/path) and scp-like SSH syntax
    // ([user@]host:path, e.g. git@github.com:example/orders) down to "host/path", so an HTTPS
    // remote and an SSH remote for the same repository compare equal.
    private static bool RepositoryUrlsMatch(string a, string b) =>
        string.Equals(GitUrl.Parse(a).Identity, GitUrl.Parse(b).Identity, StringComparison.Ordinal);
}
