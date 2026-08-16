using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Git;

internal sealed class LibGit2SharpGitClient : IGitClient
{
    public void Clone(string repositoryUrl, string destinationPath)
    {
        Repository.Clone(repositoryUrl, destinationPath);
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
        Commands.Fetch(repo, remote.Name, refSpecs, null, null);
    }

    public bool HasUncommittedChanges(string repositoryPath)
    {
        using var repo = new Repository(repositoryPath);
        return repo.RetrieveStatus().IsDirty;
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
            return tag.Target.Sha == headSha;
        }

        var commit = repo.Lookup<Commit>(reference);
        return commit is not null && commit.Sha == headSha;
    }
}
