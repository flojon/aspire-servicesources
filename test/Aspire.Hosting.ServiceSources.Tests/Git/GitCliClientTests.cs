using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// Drives <see cref="GitCliClient"/> against real repositories on disk, so every method is checked
/// against the git it actually shells out to rather than against a description of it.
/// </summary>
public class GitCliClientTests
{
    /// <summary>
    /// A client that can only see the config and credentials the test gives it — see
    /// <see cref="TestRepository.IsolatedEnvironment"/>.
    /// </summary>
    private static GitCliClient Client() => new(TestRepository.IsolatedEnvironment());

    private static (GitCliClient Client, TestRepository Origin, string Destination) ClonedRepository()
    {
        var origin = TestRepository.CreateOrigin();
        var destination = TestRepository.EmptyDestination();
        var client = Client();

        client.Clone(origin.Path, destination);

        return (client, origin, destination);
    }

    [Fact]
    public void EnsureAvailable_GitOnPath_DoesNotThrow() => Client().EnsureAvailable();

    [Fact]
    public void Clone_CopiesRepositoryToDestination()
    {
        var (_, _, destination) = ClonedRepository();

        Assert.Equal("main content", TestRepository.At(destination).Read("file.txt"));
    }

    [Fact]
    public void Clone_UnreachableRepository_ThrowsAndLeavesNoDestinationBehind()
    {
        var destination = TestRepository.EmptyDestination();

        Assert.ThrowsAny<Exception>(
            () => Client().Clone(Path.Combine(Path.GetTempPath(), "servicesources-no-such-repo"), destination));

        // The caller renames this directory into place on success, so a failed clone that left
        // debris behind would make the next attempt collide with it.
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public void Checkout_Tag_UpdatesWorkingTreeToTaggedCommit()
    {
        var (client, _, destination) = ClonedRepository();

        client.Checkout(destination, "v1.0.0");

        Assert.Equal("main content", TestRepository.At(destination).Read("file.txt"));
    }

    [Fact]
    public void Checkout_AnnotatedTag_UpdatesWorkingTreeToTheCommitItPointsAt()
    {
        var (client, origin, destination) = ClonedRepository();
        origin.Git("tag", "-a", "v2.0.0", "-m", "release message", "feature/x");
        client.Fetch(destination);

        client.Checkout(destination, "v2.0.0");

        // The tag object itself is not a commit, so this only works if it was peeled.
        Assert.Equal("feature content", TestRepository.At(destination).Read("file.txt"));
    }

    [Fact]
    public void Checkout_CommitSha_UpdatesWorkingTreeToThatCommit()
    {
        var (client, origin, destination) = ClonedRepository();
        var sha = origin.Git("rev-parse", "feature/x");

        client.Checkout(destination, sha);

        Assert.Equal("feature content", TestRepository.At(destination).Read("file.txt"));
    }

    [Fact]
    public void Checkout_RemoteOnlyBranch_CreatesLocalTrackingBranchAndUpdatesWorkingTree()
    {
        var (client, _, destination) = ClonedRepository();

        client.Checkout(destination, "feature/x");

        var clone = TestRepository.At(destination);
        Assert.Equal("feature content", clone.Read("file.txt"));
        // A local branch, not a detached HEAD, so committing on it and pushing works.
        Assert.Equal("feature/x", clone.Git("rev-parse", "--abbrev-ref", "HEAD"));
        Assert.Equal("origin/feature/x", clone.Git("rev-parse", "--abbrev-ref", "feature/x@{upstream}"));
    }

    [Fact]
    public void Checkout_LocalBranchThatAlreadyExists_ChecksItOutRatherThanFailingToCreateIt()
    {
        var (client, _, destination) = ClonedRepository();
        client.Checkout(destination, "feature/x");
        client.Checkout(destination, "main");

        client.Checkout(destination, "feature/x");

        Assert.Equal("feature content", TestRepository.At(destination).Read("file.txt"));
    }

    [Fact]
    public void Checkout_UnknownRef_ThrowsNamingRef()
    {
        var (client, _, destination) = ClonedRepository();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => client.Checkout(destination, "does-not-exist"));

        Assert.Contains("does-not-exist", ex.Message);
    }

    [Fact]
    public void Checkout_RefThatWouldBeReadAsAnOption_IsReportedAsAMissingRefRatherThanRunAsAFlag()
    {
        var (client, _, destination) = ClonedRepository();

        // git forbids a ref name starting with '-', so there is nothing to find — but passing it
        // through positionally would have git parse it as an option instead.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => client.Checkout(destination, "--upload-pack=touch pwned"));

        Assert.Contains("--upload-pack", ex.Message);
    }

    [Fact]
    public void Fetch_PullsRefCreatedOnOriginAfterInitialClone()
    {
        var (client, origin, destination) = ClonedRepository();
        origin.Git("checkout", "--quiet", "-b", "feature/late");
        origin.Commit("file.txt", "late content", "late commit");

        client.Fetch(destination);
        client.Checkout(destination, "feature/late");

        Assert.Equal("late content", TestRepository.At(destination).Read("file.txt"));
    }

    [Fact]
    public void Fetch_RepositoryWithNoOriginRemote_IsANoOp()
    {
        var (client, _, destination) = ClonedRepository();
        TestRepository.At(destination).Git("remote", "remove", "origin");

        client.Fetch(destination);

        Assert.Null(client.GetOriginUrl(destination));
    }

    [Fact]
    public void HasUncommittedChanges_CleanCheckout_ReturnsFalse()
    {
        var (client, _, destination) = ClonedRepository();

        Assert.False(client.HasUncommittedChanges(destination));
    }

    [Fact]
    public void HasUncommittedChanges_ModifiedFile_ReturnsTrue()
    {
        var (client, _, destination) = ClonedRepository();
        TestRepository.At(destination).Write("file.txt", "locally edited");

        Assert.True(client.HasUncommittedChanges(destination));
    }

    [Fact]
    public void HasUncommittedChanges_StagedChange_ReturnsTrue()
    {
        var (client, _, destination) = ClonedRepository();
        var clone = TestRepository.At(destination);
        clone.Write("file.txt", "locally edited");
        clone.Git("add", "--", "file.txt");

        Assert.True(client.HasUncommittedChanges(destination));
    }

    [Fact]
    public void HasUncommittedChanges_UntrackedFile_ReturnsFalse()
    {
        var (client, _, destination) = ClonedRepository();

        // Build output from a plain `dotnet build` mustn't make a checkout look permanently dirty.
        TestRepository.At(destination).Write("bin-output.dll", "build output");

        Assert.False(client.HasUncommittedChanges(destination));
    }

    [Fact]
    public void GetOriginUrl_ClonedRepository_ReturnsOriginUrl()
    {
        var (client, origin, destination) = ClonedRepository();

        Assert.Equal(origin.Path, client.GetOriginUrl(destination));
    }

    [Fact]
    public void GetOriginUrl_NoOriginRemote_ReturnsNull()
    {
        var (client, _, destination) = ClonedRepository();
        TestRepository.At(destination).Git("remote", "remove", "origin");

        Assert.Null(client.GetOriginUrl(destination));
    }

    [Fact]
    public void IsRefCheckedOut_MatchingRef_ReturnsTrue()
    {
        var (client, _, destination) = ClonedRepository();
        client.Checkout(destination, "v1.0.0");

        Assert.True(client.IsRefCheckedOut(destination, "v1.0.0"));
    }

    [Fact]
    public void IsRefCheckedOut_MatchingAnnotatedTag_ReturnsTrue()
    {
        var (client, _, destination) = ClonedRepository();
        client.Checkout(destination, "v1.0.0");
        TestRepository.At(destination).Git("tag", "-a", "v1.0.0-annotated", "-m", "release message");

        // Compares the commit the tag points at, not the tag object's own id.
        Assert.True(client.IsRefCheckedOut(destination, "v1.0.0-annotated"));
    }

    [Fact]
    public void IsRefCheckedOut_RemoteOnlyBranchAtHead_ReturnsTrue()
    {
        var (client, _, destination) = ClonedRepository();

        // Nothing local is called "feature/x" yet, so this only holds if origin/ is consulted.
        client.Checkout(destination, "feature/x");
        TestRepository.At(destination).Git("branch", "--delete", "--force", "main");

        Assert.True(client.IsRefCheckedOut(destination, "feature/x"));
    }

    [Fact]
    public void IsRefCheckedOut_DifferentRef_ReturnsFalse()
    {
        var (client, _, destination) = ClonedRepository();
        client.Checkout(destination, "v1.0.0");

        Assert.False(client.IsRefCheckedOut(destination, "feature/x"));
    }

    [Fact]
    public void IsRefCheckedOut_UnknownRef_ReturnsFalse()
    {
        var (client, _, destination) = ClonedRepository();

        Assert.False(client.IsRefCheckedOut(destination, "does-not-exist"));
    }

    [Fact]
    public void IsRefCheckedOut_BranchAndTagOfTheSameNameDisagree_AgreesWithWhatCheckoutDoes()
    {
        var (client, origin, destination) = ClonedRepository();

        // git's own precedence resolves a bare name to the tag before the branch, so a client that
        // checks out one and compares against the other would report a correct checkout as stale
        // and re-checkout it on every run.
        origin.Git("branch", "release", "feature/x");
        origin.Git("tag", "release", "main");
        client.Fetch(destination);

        client.Checkout(destination, "release");

        Assert.Equal("feature content", TestRepository.At(destination).Read("file.txt"));
        Assert.True(client.IsRefCheckedOut(destination, "release"));
    }

    [Fact]
    public async Task Clone_TwoDifferentRepositoriesConcurrently_BothSucceedWithoutCorruption()
    {
        var originA = TestRepository.CreateOrigin();
        var originB = TestRepository.CreateOrigin();
        var destinationA = TestRepository.EmptyDestination("clone-a");
        var destinationB = TestRepository.EmptyDestination("clone-b");
        var client = Client();

        await Task.WhenAll(
            Task.Run(() => client.Clone(originA.Path, destinationA)),
            Task.Run(() => client.Clone(originB.Path, destinationB)));

        Assert.Equal("main content", TestRepository.At(destinationA).Read("file.txt"));
        Assert.Equal("main content", TestRepository.At(destinationB).Read("file.txt"));
    }

    [Theory]
    // git's own wording when a helper's credential is refused.
    [InlineData("fatal: Authentication failed for 'https://host/org/repo.git/'")]
    [InlineData("remote: HTTP Basic: Access denied")]
    [InlineData("remote: Invalid username or password.")]
    [InlineData("fatal: unable to access 'https://host/o/r/': The requested URL returned error: 401")]
    // A bare 403 over git-on-HTTPS is "authenticated, but not allowed" — typically a token missing
    // a scope, or an SSO session that was never authorized.
    [InlineData("fatal: unable to access 'https://host/o/r/': The requested URL returned error: 403")]
    // GitHub, GitLab and Azure DevOps answer an unauthenticated request for a private repository
    // with 404, so as not to leak whether it exists — the single most common shape of the failure
    // this detection exists to explain.
    [InlineData("fatal: unable to access 'https://host/o/r/': The requested URL returned error: 404")]
    [InlineData("remote: Repository not found.")]
    [InlineData("fatal: repository 'https://host/o/r.git/' not found")]
    // No credential resolved at all: git fell through every helper to asking a human.
    [InlineData("fatal: could not read Username for 'https://host': terminal prompts disabled")]
    // What ssh says when every key it offered was refused.
    [InlineData("git@host: Permission denied (publickey).\nfatal: Could not read from remote repository.")]
    [InlineData("Please make sure you have the correct access rights\nand the repository exists.")]
    public void LooksLikeAuthFailure_MessagesIndicatingMissingOrRejectedCredentials_ReturnTrue(string message) =>
        Assert.True(GitCliClient.LooksLikeAuthFailure(message));

    [Theory]
    [InlineData("fatal: the remote end hung up unexpectedly")]
    [InlineData("fatal: unable to access 'https://host/o/r/': Could not resolve host: host")]
    [InlineData("fatal: Unable to create '/repo/.git/index.lock': File exists.")]
    // An absent local path is git's own wording and is not the remote "not found" above.
    [InlineData("fatal: repository '/nonexistent/repo' does not exist")]
    // A "not found" about a ref or an object is a local lookup miss, not a rejected credential:
    // reporting it as an authentication problem sends the developer after the wrong cause.
    [InlineData("error: pathspec 'feature' did not match any file(s) known to git")]
    [InlineData("fatal: Not a valid object name: 'feature'")]
    // "404" is only a status code when it is written as one. A port, an object id or a byte count
    // that happens to contain those digits says nothing about credentials.
    [InlineData("fatal: unable to access 'https://host:404/o/r/': Failed to connect")]
    [InlineData("fatal: early EOF after 40412 bytes")]
    // Being throttled comes back as a 403 too, but it says nothing about the credential: naming
    // authentication would send the developer after the wrong cause, when the fix is to wait.
    [InlineData("The requested URL returned error: 403 - You have exceeded a secondary rate limit")]
    [InlineData("The requested URL returned error: 403 - too many requests")]
    [InlineData("The requested URL returned error: 403 (request throttled)")]
    [InlineData("The requested URL returned error: 403 - rate-limited, try again later")]
    // An unverified host key is a trust decision the developer fixes in known_hosts, not a
    // credential — and git's own message already says exactly that.
    [InlineData("Host key verification failed.\nfatal: Could not read from remote repository.")]
    public void LooksLikeAuthFailure_UnrelatedFailures_ReturnFalse(string message) =>
        Assert.False(GitCliClient.LooksLikeAuthFailure(message));

    [Theory]
    [InlineData("fatal: could not read Username for 'https://host': terminal prompts disabled")]
    [InlineData("fatal: could not read Password for 'https://git@host': terminal prompts disabled")]
    public void ResolvedNoCredentials_GitFellThroughToAskingAHuman_ReturnsTrue(string message) =>
        Assert.True(GitCliClient.ResolvedNoCredentials(message));

    [Theory]
    // A credential was offered and refused, which is a different problem with a different fix.
    [InlineData("fatal: Authentication failed for 'https://host/org/repo.git/'")]
    [InlineData("remote: Repository not found.")]
    public void ResolvedNoCredentials_ACredentialWasOffered_ReturnsFalse(string message) =>
        Assert.False(GitCliClient.ResolvedNoCredentials(message));

    [Fact]
    public void GetHeadCommitSha_ReturnsTheCommitHeadSitsOn()
    {
        var (client, _, destination) = ClonedRepository();

        var sha = client.GetHeadCommitSha(destination);

        Assert.NotNull(sha);
        Assert.Equal(TestRepository.At(destination).Git("rev-parse", "HEAD").Trim(), sha);
    }

    [Fact]
    public void GetHeadCommitSha_MovesWithTheCheckout()
    {
        var (client, _, destination) = ClonedRepository();

        var before = client.GetHeadCommitSha(destination);
        TestRepository.At(destination).Commit("another.txt", "more", "a second commit");

        Assert.NotEqual(before, client.GetHeadCommitSha(destination));
    }

    /// <summary>
    /// The answer a <c>prepare</c> step's marker has to read as "run it" rather than as "assume
    /// done" — the case a <c>path</c> override pointed at a plain unpacked directory is in.
    /// </summary>
    [Fact]
    public void GetHeadCommitSha_NotARepository_IsNull()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;

        Assert.Null(Client().GetHeadCommitSha(dir));
    }
}
