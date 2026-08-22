using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Git;

internal sealed class LibGit2SharpGitClient : IGitClient
{
    public void Clone(string repositoryUrl, string destinationPath)
    {
        // The missing SSH transport is a property of this implementation, so enforce it here
        // rather than relying on every caller to remember the check.
        GitUrlValidator.EnsureSupported(repositoryUrl);

        var options = new CloneOptions
        {
            FetchOptions = { CredentialsProvider = GitCredentialResolver.CreateProvider(repositoryUrl) },
        };

        WithAuthFailureDetection(repositoryUrl, () => Repository.Clone(repositoryUrl, destinationPath, options));
    }

    public void Checkout(string repositoryPath, string reference)
    {
        using var repo = new Repository(repositoryPath);

        var branch = repo.Branches[reference] ?? repo.Branches[$"origin/{reference}"];
        if (branch is not null)
        {
            if (!branch.IsRemote)
            {
                Commands.Checkout(repo, branch);
                return;
            }

            var localBranch = repo.CreateBranch(reference, branch.Tip);
            repo.Branches.Update(localBranch, b => b.TrackedBranch = branch.CanonicalName);
            Commands.Checkout(repo, localBranch);
            return;
        }

        var tag = repo.Tags[reference];
        if (tag is not null)
        {
            Commands.Checkout(repo, tag.Target.Sha);
            return;
        }

        var commit = repo.Lookup<Commit>(reference);
        if (commit is not null)
        {
            Commands.Checkout(repo, commit.Sha);
            return;
        }

        throw new ServiceSourcesConfigurationException(
            $"Ref '{reference}' was not found in repository at '{repositoryPath}'.");
    }

    public void Fetch(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);

        var remote = repo.Network.Remotes["origin"];
        if (remote is null)
        {
            return;
        }

        GitUrlValidator.EnsureSupported(remote.Url);

        var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
        var fetchOptions = new FetchOptions
        {
            CredentialsProvider = GitCredentialResolver.CreateProvider(remote.Url),
        };

        WithAuthFailureDetection(remote.Url, () => Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, null));
    }

    /// <summary>
    /// Runs a network operation, translating what looks like a rejected or missing credential into
    /// <see cref="GitAuthenticationFailedException"/> so callers can name authentication as the
    /// likely cause instead of reporting a generic clone/fetch failure.
    /// </summary>
    private static void WithAuthFailureDetection(string repositoryUrl, Action operation) =>
        WithAuthFailureDetection(repositoryUrl, operation, GitCredentialResolver.ForgetCachedCredentials);

    /// <summary>
    /// Test seam: takes the credential-cache eviction as a parameter, so what a failure does to the
    /// cached credential can be observed without a real credential helper behind it.
    /// </summary>
    internal static void WithAuthFailureDetection(
        string repositoryUrl,
        Action operation,
        Action<string> forgetCachedCredentials)
    {
        try
        {
            operation();
        }
        catch (LibGit2SharpException ex) when (LooksLikeAuthFailure(ex.Message))
        {
            // Whatever the credential helper last gave us for this host didn't get us in, so drop
            // the cached copy: the next attempt re-reads the developer's credential store instead
            // of replaying a stale token for the lifetime of the AppHost process. Deliberately not
            // `git credential reject` — a "not found" reaching here is at least as likely to be a
            // repository this credential genuinely can't see as a bad credential, and erasing a
            // working entry over that would be worse than keeping it.
            forgetCachedCredentials(repositoryUrl);
            throw new GitAuthenticationFailedException(ex.Message, ex);
        }
    }

    internal static bool LooksLikeAuthFailure(string message) =>
        message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
        || message.Contains("credentials", StringComparison.OrdinalIgnoreCase)
        || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
        || HasHttpStatus(message, "401")
        // A 403 is "authenticated, but not allowed", which over git-on-HTTPS is usually a token
        // missing a scope or an SSO session the developer hasn't authorized — a credential problem,
        // and one worth naming. Being throttled answers with that same status while saying nothing
        // about the credential, so those are excluded: pointing at authentication there sends the
        // developer after the wrong cause and drops a credential that works.
        || (HasHttpStatus(message, "403") && !LooksLikeThrottling(message))
        // GitHub, GitLab and Azure DevOps all answer an unauthenticated request for a private
        // repository with 404 rather than 401, so as not to leak whether it exists. A remote
        // "not found" is therefore far more often a missing credential than an absent repository
        // — which is exactly the case this detection exists to explain. The caller's message is
        // worded to cover both readings.
        || HasHttpStatus(message, "404")
        || message.Contains("repository not found", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the message reports a request turned away for coming too often, rather than for the
    /// credential it carried. Only the host's own wording can tell the two apart, so this catches
    /// what the major hosts say when they throttle and leaves the rest reading as a credential
    /// problem.
    /// </summary>
    private static bool LooksLikeThrottling(string message) =>
        message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || message.Contains("rate-limit", StringComparison.OrdinalIgnoreCase)
        || message.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)
        || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
        || message.Contains("throttl", StringComparison.OrdinalIgnoreCase)
        || message.Contains("try again later", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the message reports the given HTTP status, as libgit2's HTTP transports word it
    /// ("request failed with status code: 404"). Anchoring on the phrase rather than the bare digits
    /// keeps a port number, an object id or a byte count that happens to contain them from being read
    /// as a rejected credential — a false positive costs the developer a wrong diagnosis and evicts a
    /// working credential from the cache.
    /// </summary>
    private static bool HasHttpStatus(string message, string statusCode) =>
        message.Contains($"status code: {statusCode}", StringComparison.OrdinalIgnoreCase)
        || message.Contains($"status code {statusCode}", StringComparison.OrdinalIgnoreCase)
        || message.Contains($"HTTP {statusCode}", StringComparison.OrdinalIgnoreCase);

    public bool HasUncommittedChanges(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);

        // Unlike RepositoryStatus.IsDirty, deliberately excludes untracked files: build output
        // (e.g. bin/obj) left behind by a plain `dotnet build` shouldn't make an otherwise-clean
        // checkout look permanently dirty.
        return repo.RetrieveStatus().Any(entry =>
            entry.State is not (FileStatus.Ignored or FileStatus.Unaltered or FileStatus.NewInWorkdir));
    }

    public bool IsRefCheckedOut(string repositoryPath, string reference)
    {
        using var repo = new Repository(repositoryPath);

        var headSha = repo.Head.Tip?.Sha;
        if (headSha is null)
        {
            return false;
        }

        var branch = repo.Branches[reference] ?? repo.Branches[$"origin/{reference}"];
        if (branch is not null)
        {
            return branch.Tip.Sha == headSha;
        }

        var tag = repo.Tags[reference];
        if (tag is not null)
        {
            return tag.PeeledTarget.Sha == headSha;
        }

        var commit = repo.Lookup<Commit>(reference);
        return commit is not null && commit.Sha == headSha;
    }

    public string? GetOriginUrl(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);
        return repo.Network.Remotes["origin"]?.Url;
    }
}
