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

    [Fact]
    public void Fetch_PullsRefCreatedOnOriginAfterInitialClone()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        using (var originRepo = new Repository(origin))
        {
            var signature = new Signature("test", "test@test.com", DateTimeOffset.Now);
            var lateBranch = originRepo.CreateBranch("feature/late");
            Commands.Checkout(originRepo, lateBranch);
            File.WriteAllText(Path.Combine(origin, "file.txt"), "late content");
            Commands.Stage(originRepo, "file.txt");
            originRepo.Commit("late commit", signature, signature);
        }

        client.Fetch(destination);
        client.Checkout(destination, "feature/late");

        Assert.Equal("late content", File.ReadAllText(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public void HasUncommittedChanges_CleanCheckout_ReturnsFalse()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        Assert.False(client.HasUncommittedChanges(destination));
    }

    [Fact]
    public void HasUncommittedChanges_ModifiedFile_ReturnsTrue()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        File.WriteAllText(Path.Combine(destination, "file.txt"), "locally edited");

        Assert.True(client.HasUncommittedChanges(destination));
    }

    [Fact]
    public void HasUncommittedChanges_UntrackedFile_ReturnsFalse()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        File.WriteAllText(Path.Combine(destination, "bin-output.dll"), "build output");

        Assert.False(client.HasUncommittedChanges(destination));
    }

    [Fact]
    public void GetOriginUrl_ClonedRepository_ReturnsOriginUrl()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        Assert.Equal(origin, client.GetOriginUrl(destination));
    }

    [Fact]
    public void IsRefCheckedOut_MatchingRef_ReturnsTrue()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);
        client.Checkout(destination, "v1.0.0");

        Assert.True(client.IsRefCheckedOut(destination, "v1.0.0"));
    }

    [Fact]
    public void IsRefCheckedOut_MatchingAnnotatedTag_ReturnsTrue()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);
        client.Checkout(destination, "v1.0.0");

        using (var repo = new Repository(destination))
        {
            var signature = new Signature("test", "test@test.com", DateTimeOffset.Now);
            repo.ApplyTag("v1.0.0-annotated", signature, "release message");
        }

        Assert.True(client.IsRefCheckedOut(destination, "v1.0.0-annotated"));
    }

    [Fact]
    public void IsRefCheckedOut_DifferentRef_ReturnsFalse()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);
        client.Checkout(destination, "v1.0.0");

        Assert.False(client.IsRefCheckedOut(destination, "feature/x"));
    }

    [Fact]
    public async Task Clone_TwoDifferentRepositoriesConcurrently_BothSucceedWithoutCorruption()
    {
        var originA = CreateOriginRepo();
        var originB = CreateOriginRepo();
        var destinationA = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone-a");
        var destinationB = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone-b");
        var client = new LibGit2SharpGitClient();

        await Task.WhenAll(
            Task.Run(() => client.Clone(originA, destinationA)),
            Task.Run(() => client.Clone(originB, destinationB)));

        Assert.Equal("main content", File.ReadAllText(Path.Combine(destinationA, "file.txt")));
        Assert.Equal("main content", File.ReadAllText(Path.Combine(destinationB, "file.txt")));
    }

    [Theory]
    [InlineData("git@github.com:company/orders.git")]
    [InlineData("ssh://git@github.com/company/orders.git")]
    [InlineData("gitserver:company/orders.git")]
    public void Clone_SshUrl_RejectsItRatherThanFailingWithAnOpaqueNativeError(string repositoryUrl)
    {
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => new LibGit2SharpGitClient().Clone(repositoryUrl, destination));

        Assert.Contains("SSH", ex.Message);
        Assert.Contains("HTTPS", ex.Message);
        Assert.False(Directory.Exists(destination));
    }

    [Theory]
    [InlineData("request failed with status code: 401")]
    [InlineData("too many redirects or authentication replays")]
    [InlineData("callback returned unsupported credentials type")]
    [InlineData("request failed with status code: 403")]
    [InlineData("Unauthorized")]
    // GitHub, GitLab and Azure DevOps answer an unauthenticated request for a private repository
    // with 404, so as not to leak whether it exists — the single most common shape of the failure
    // this detection exists to explain.
    [InlineData("request failed with status code: 404")]
    [InlineData("remote: Repository not found")]
    public void LooksLikeAuthFailure_MessagesIndicatingMissingOrRejectedCredentials_ReturnTrue(string message) =>
        Assert.True(LibGit2SharpGitClient.LooksLikeAuthFailure(message));

    [Theory]
    [InlineData("early EOF")]
    [InlineData("failed to resolve address for gitserver.invalid")]
    [InlineData("the index is locked")]
    public void LooksLikeAuthFailure_UnrelatedFailures_ReturnFalse(string message) =>
        Assert.False(LibGit2SharpGitClient.LooksLikeAuthFailure(message));

    [Fact]
    public void WithAuthFailureDetection_AuthenticationFailure_DropsTheCachedCredentialForTheRepository()
    {
        var forgotten = new List<string>();

        Assert.Throws<GitAuthenticationFailedException>(() => LibGit2SharpGitClient.WithAuthFailureDetection(
            "https://example.invalid/org/repo",
            () => throw new LibGit2SharpException("request failed with status code: 401"),
            forgotten.Add));

        // Otherwise a token rotated mid-session stays shadowed by the cached one until the AppHost
        // process restarts.
        Assert.Equal("https://example.invalid/org/repo", Assert.Single(forgotten));
    }

    [Fact]
    public void WithAuthFailureDetection_UnrelatedFailure_KeepsTheCachedCredential()
    {
        var forgotten = new List<string>();

        Assert.Throws<LibGit2SharpException>(() => LibGit2SharpGitClient.WithAuthFailureDetection(
            "https://example.invalid/org/repo",
            () => throw new LibGit2SharpException("early EOF"),
            forgotten.Add));

        Assert.Empty(forgotten);
    }

    [Fact]
    public void WithAuthFailureDetection_OperationSucceeds_KeepsTheCachedCredential()
    {
        var forgotten = new List<string>();

        LibGit2SharpGitClient.WithAuthFailureDetection(
            "https://example.invalid/org/repo",
            () => { },
            forgotten.Add);

        Assert.Empty(forgotten);
    }
}
