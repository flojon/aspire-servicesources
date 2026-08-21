using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Git;

internal sealed class LibGit2SharpGitClient : IGitClient
{
    public void Clone(string repositoryUrl, string destinationPath)
    {
        var options = new CloneOptions
        {
            FetchOptions = { CredentialsProvider = GitCredentialResolver.CreateProvider(repositoryUrl) },
        };

        try
        {
            Repository.Clone(repositoryUrl, destinationPath, options);
        }
        catch (Exception ex) when (LooksLikeAuthFailure(ex))
        {
            throw new GitAuthenticationFailedException(ex.Message, ex);
        }
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

        var refSpecs = remote.FetchRefSpecs.Select(r => r.Specification);
        var fetchOptions = new FetchOptions
        {
            CredentialsProvider = GitCredentialResolver.CreateProvider(remote.Url),
        };

        try
        {
            Commands.Fetch(repo, remote.Name, refSpecs, fetchOptions, null);
        }
        catch (Exception ex) when (LooksLikeAuthFailure(ex))
        {
            throw new GitAuthenticationFailedException(ex.Message, ex);
        }
    }

    private static bool LooksLikeAuthFailure(Exception ex)
    {
        if (ex is not LibGit2SharpException)
        {
            return false;
        }

        var message = ex.Message;
        return message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || message.Contains("credentials", StringComparison.OrdinalIgnoreCase)
            || message.Contains("401", StringComparison.Ordinal)
            || message.Contains("403", StringComparison.Ordinal)
            || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase);
    }

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
