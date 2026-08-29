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
    /// <summary>
    /// A checkout directory that exists, and whether it still has to be reconciled against the
    /// configured ref.
    /// </summary>
    /// <remarks>
    /// The two halves are worth separating because only one of them is safe to do on speculation.
    /// <see cref="PrepareRepoRoot"/> creates what is missing and stops at any working tree it did
    /// not create; reconciling that tree is a mutation of somebody's checkout, so it waits for
    /// <see cref="ReconcileRepoRoot"/> — which the prefetch calls only for the services the AppHost
    /// actually adds (see <see cref="Sources.LocalCheckoutPrefetch"/>).
    /// </remarks>
    public readonly record struct PreparedCheckout(string RepoRoot, bool NeedsReconciliation);

    /// <summary>
    /// The fully resolved checkout directory: prepared, then reconciled. For callers already
    /// resolving a service the AppHost asked for, so there is nothing to defer.
    /// </summary>
    public static string ResolveRepoRoot(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        string appHostDirectory,
        IGitClient gitClient) =>
        ReconcileRepoRoot(
            serviceName,
            metadata,
            config,
            PrepareRepoRoot(serviceName, metadata, config, appHostDirectory, gitClient),
            gitClient);

    /// <summary>
    /// Makes sure the checkout directory exists, without touching a working tree this call did not
    /// create: a missing checkout is cloned, and the clone we made is put on its ref. Anything
    /// already there is left exactly as it was, for <see cref="ReconcileRepoRoot"/> to deal with.
    /// </summary>
    public static PreparedCheckout PrepareRepoRoot(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        string appHostDirectory,
        IGitClient gitClient)
    {
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
            var overridden = Path.GetFullPath(config.Path, appHostDirectory);

            // Only the built-in dotnet kind goes on to look for a project file underneath this
            // directory; every other kind hands the checkout straight to its handler, so without
            // this check a typo'd override surfaces as an obscure failure inside that handler (or
            // as a resource with a nonsensical working directory) rather than as a named config
            // error.
            if (!Directory.Exists(overridden))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': the 'path' override points at '{overridden}', which does not exist. " +
                    "'path' must name an existing local directory.");
            }

            // Used as-is: no clone, no checkout, no fetch, ever.
            return new PreparedCheckout(overridden, NeedsReconciliation: false);
        }

        EnsureGitignore(appHostDirectory);
        var checkoutsRoot = Path.Combine(appHostDirectory, ".servicesources", "checkouts");
        var repoRoot = Path.Combine(checkoutsRoot, serviceName);

        if (Directory.Exists(Path.Combine(repoRoot, ".git")))
        {
            // A working tree from an earlier run, or a developer's own. Untouched here.
            return new PreparedCheckout(repoRoot, NeedsReconciliation: true);
        }

        // Validated before any work is done, so an unsupported URL fails fast rather
        // than after a sweep and a directory create.
        GitUrlValidator.EnsureSupported(serviceName, metadata.Repository);

        // A clone that loses the race to a concurrent AppHost leaves us using *their*
        // checkout, not one we just made, so it gets the same treatment as a checkout found
        // there on a later run: theirs may be a clone of another repository, and may hold
        // work in flight that a checkout would discard.
        if (CloneIntoPlace(serviceName, metadata, checkoutsRoot, repoRoot, gitClient))
        {
            return new PreparedCheckout(repoRoot, NeedsReconciliation: true);
        }

        // Our own clone, seconds old and holding nothing anyone could lose, so it is put on the
        // configured ref right here — inside the parallel phase — rather than deferred.
        if (ConfiguredReference(metadata, config) is { } reference)
        {
            CheckoutWithFetchRetry(serviceName, metadata, repoRoot, reference, gitClient);
        }

        return new PreparedCheckout(repoRoot, NeedsReconciliation: false);
    }

    /// <summary>
    /// Validates and reconciles a checkout that <see cref="PrepareRepoRoot"/> deliberately left
    /// alone — the half that mutates a working tree, and therefore the half that must run only for a
    /// service the AppHost really added.
    /// </summary>
    public static string ReconcileRepoRoot(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        PreparedCheckout prepared,
        IGitClient gitClient)
    {
        if (prepared.NeedsReconciliation)
        {
            UseExistingCheckout(
                serviceName, metadata, prepared.RepoRoot, ConfiguredReference(metadata, config), gitClient);
        }

        return prepared.RepoRoot;
    }

    /// <summary>
    /// The ref this checkout should sit on, or <see langword="null"/> when neither the developer nor
    /// the catalog named one — in which case whatever the clone already has checked out stands.
    /// </summary>
    private static string? ConfiguredReference(ServiceMetadata metadata, ServiceDeveloperConfig config) =>
        config.Ref ?? metadata.DefaultRef;

    /// <summary>
    /// Adopts a checkout this call did not create — one left by an earlier run, or one a concurrent
    /// AppHost landed while we were cloning. Both cases are the same problem: the working tree
    /// belongs to someone else, so it is verified to be the right repository and left alone unless
    /// moving it to <paramref name="reference"/> is safe.
    /// </summary>
    private static void UseExistingCheckout(
        string serviceName, ServiceMetadata metadata, string repoRoot, string? reference, IGitClient gitClient)
    {
        var existingOrigin = gitClient.GetOriginUrl(repoRoot);
        if (existingOrigin is not null && !RepositoryUrlsMatch(existingOrigin, metadata.Repository))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': checkout at '{repoRoot}' already contains a clone of " +
                $"'{GitUrl.Redact(existingOrigin)}', which does not match the configured repository " +
                $"'{GitUrl.Redact(metadata.Repository)}'. " +
                "Remove the checkout directory or fix the configured repository URL.");
        }

        if (reference is null)
        {
            return;
        }

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

    /// <summary>
    /// Clones into a scratch directory alongside the destination and renames it into place, so an
    /// interrupted clone can never leave a half-populated <c>checkouts/&lt;service&gt;</c> behind.
    /// Returns <see langword="true"/> when a concurrent resolution won the race and its checkout was
    /// adopted instead — the caller must then treat <c>repoRoot</c> as someone else's working tree
    /// rather than as the clone it asked for.
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
    private static bool CloneIntoPlace(
        string serviceName, ServiceMetadata metadata, string checkoutsRoot, string repoRoot, IGitClient gitClient)
    {
        // See PrepareRepoRoot: the real URL goes to git, the redacted one goes into messages.
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
                        $"into '{repoRoot}'",
                        ex.NoCredentialsResolved),
                    ex);
            }
            catch (Exception ex)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': failed to clone repository '{displayRepository}' into '{repoRoot}'.", ex);
            }

            // What happens to the destination is decided here, after the clone, rather than
            // inherited from the "no .git directory" probe in PrepareRepoRoot. By now that probe is
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
                return true;
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
                catch (DirectoryNotFoundException)
                {
                    // A concurrent resolution of the same service removed the same debris first.
                    // That is exactly what this delete wanted, so carry on to the rename rather than
                    // failing the AppHost with advice to delete a directory that is already gone.
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
            catch (IOException ex)
            {
                // The same collision one instant later: the check above and this rename are not one
                // atomic operation. Ours is redundant rather than wrong — discard it and use theirs.
                if (Directory.Exists(Path.Combine(repoRoot, ".git")))
                {
                    return true;
                }

                // Something else put a non-repository directory there inside the same window (a
                // concurrent AppHost that got as far as creating it). Reported as a named
                // configuration failure because the raw rename error — "Cannot create a file when
                // that file already exists" — says neither which service failed nor what to do.
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': the freshly cloned checkout could not be moved into '{repoRoot}' — " +
                    "something else created that path while the clone was running. Re-run; if it persists, delete " +
                    "the directory and re-run.", ex);
            }

            return false;
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
        // See PrepareRepoRoot: the real URL goes to git, the redacted one goes into messages.
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
                    $"'{repoRoot}' while resolving ref '{reference}'",
                    ex.NoCredentialsResolved),
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
    /// Appends the authentication remediation to <paramref name="failureDescription"/>.
    /// </summary>
    /// <remarks>
    /// The rejected-credential wording covers both readings of the underlying failure: hosts
    /// commonly answer an unauthenticated request for a private repository with "not found" rather
    /// than "unauthorized" (see <see cref="LibGit2SharpGitClient.LooksLikeAuthFailure"/>), so the
    /// message must not assert that credentials were definitely rejected. When no credential was
    /// resolved at all there is no such ambiguity to preserve — libgit2's own message for that case
    /// ("could not find appropriate mechanism for credentials") describes a client-side dead end
    /// that never reached the host, and repeating the rejected-credential remediation for it points
    /// the developer at the wrong half of the problem. That branch is reached only when libgit2
    /// actually asked for a credential (see
    /// <see cref="GitCredentialProvider.ResolvedNoCredentials"/>), so it can state what the ladder
    /// found without having to hedge about whether the host was ever contacted.
    /// </remarks>
    private static string AuthFailureMessage(string failureDescription, bool noCredentialsResolved) =>
        noCredentialsResolved
            // Nothing was ever offered, so nothing was refused. Saying "authentication failed" here
            // sends the developer looking for a rejected or expired token they never had, when the
            // fix is to make a credential resolvable in the first place — and, since the helper is
            // consulted in whatever environment the AppHost runs in, it is worth naming that the
            // helper is where the gap is rather than the credential's contents.
            ? $"{failureDescription} — no git credentials were resolved for this host, so the request " +
              "carried only the machine's integrated credential, which a token-authenticated host " +
              "cannot use. `git credential fill` returned nothing for this host and " +
              "SERVICESOURCES_GIT_TOKEN is unset or empty. Configure a git credential helper (`git " +
              "credential fill` must resolve credentials for this host in the environment the " +
              "AppHost runs in, which is not necessarily your shell) or set the " +
              "SERVICESOURCES_GIT_USERNAME/SERVICESOURCES_GIT_TOKEN environment variables."
            : $"{failureDescription} — authentication failed, or the repository is not visible to the " +
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
