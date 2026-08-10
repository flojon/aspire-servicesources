using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Git;
using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class LibGit2SharpGitClientTests
{
    private static string CreateOriginRepo()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Repository.Init(dir);

        using var repo = new Repository(dir);
        var defaultBranchName = repo.Head.FriendlyName;
        File.WriteAllText(Path.Combine(dir, "file.txt"), "main content");
        Commands.Stage(repo, "file.txt");

        var signature = new Signature("test", "test@test.com", DateTimeOffset.Now);
        repo.Commit("main commit", signature, signature);
        repo.ApplyTag("v1.0.0");

        var featureBranch = repo.CreateBranch("feature/x");
        Commands.Checkout(repo, featureBranch);
        File.WriteAllText(Path.Combine(dir, "file.txt"), "feature content");
        Commands.Stage(repo, "file.txt");
        repo.Commit("feature commit", signature, signature);

        Commands.Checkout(repo, defaultBranchName);

        return dir;
    }

    [Fact]
    public void Clone_CopiesRepositoryToDestination()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");

        new LibGit2SharpGitClient().Clone(origin, destination);

        Assert.True(File.Exists(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public void Checkout_Tag_UpdatesWorkingTreeToTaggedCommit()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        client.Checkout(destination, "v1.0.0");

        Assert.Equal("main content", File.ReadAllText(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public void Checkout_RemoteOnlyBranch_CreatesLocalTrackingBranchAndUpdatesWorkingTree()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        client.Checkout(destination, "feature/x");

        Assert.Equal("feature content", File.ReadAllText(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public void Checkout_UnknownRef_ThrowsNamingRef()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => client.Checkout(destination, "does-not-exist"));

        Assert.Contains("does-not-exist", ex.Message);
    }
}
