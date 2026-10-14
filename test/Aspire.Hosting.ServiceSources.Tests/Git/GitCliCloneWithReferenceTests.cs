using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

[Trait("IO", "true")]
public class GitCliCloneWithReferenceTests
{
    // A URL rather than a path: git hardlinks from a plain path, which would not exercise borrowing.
    private static string CloneUrl(string path) => new Uri(path).AbsoluteUri;

    private static GitCliClient Client() => new(TestRepository.IsolatedEnvironment());

    private static string[] RemoteRefs(string checkout) =>
        TestRepository.At(checkout).Git("for-each-ref", "--format=%(refname)", "refs/remotes")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    [Fact]
    public void CloneWithReference_LeavesAnIndependentCloneOfTheRealRemote()
    {
        var origin = TestRepository.CreateOrigin();
        var reference = TestRepository.EmptyDestination("reference");
        Client().Clone(CloneUrl(origin.Path), reference);
        TestRepository.At(reference).Git("branch", "home-only");
        var destination = TestRepository.EmptyDestination();

        Client().CloneWithReference(CloneUrl(origin.Path), destination, reference);

        Assert.False(File.Exists(Path.Combine(destination, ".git", "objects", "info", "alternates")));
        Assert.Equal(CloneUrl(origin.Path), Client().GetOriginUrl(destination));
        Assert.Contains("refs/remotes/origin/main", RemoteRefs(destination));
        Assert.Contains("refs/remotes/origin/feature/x", RemoteRefs(destination));
        Assert.DoesNotContain("refs/remotes/origin/home-only", RemoteRefs(destination));
        Assert.Equal("main content", TestRepository.At(destination).Read("file.txt"));
    }

    [Fact]
    public void CloneWithReference_ReferenceGone_ClonesNormally()
    {
        var origin = TestRepository.CreateOrigin();
        var missing = TestRepository.EmptyDestination("gone");
        var destination = TestRepository.EmptyDestination();

        Client().CloneWithReference(CloneUrl(origin.Path), destination, missing);

        Assert.Equal("main content", TestRepository.At(destination).Read("file.txt"));
    }

    [Fact]
    public void CloneWithReference_UnreachableRemote_IsNotAReferenceFailure()
    {
        var missingRemote = CloneUrl(TestRepository.EmptyDestination("no-such-remote.git"));
        var missingReference = TestRepository.EmptyDestination("gone");

        var failure = Record.Exception(() =>
            Client().CloneWithReference(missingRemote, TestRepository.EmptyDestination(), missingReference));

        Assert.NotNull(failure);
        Assert.IsNotType<GitReferenceFailedException>(failure);
    }

    [Theory]
    [InlineData("fatal: reference repository '/home/x/orders' is shallow", true)]
    [InlineData("error: unable to normalize alternate object path: /tmp/objects", true)]
    [InlineData("fatal: cannot repack to clean up", true)]
    [InlineData("error: object file /home/x/orders/.git/objects/ab/cdef is empty", true)]
    [InlineData("fatal: unable to access 'https://example.com/orders.git/': Could not resolve host: example.com", false)]
    [InlineData("info: Could not add alternate for '/home/x/orders': reference repository '/home/x/orders' does not exist\nfatal: unable to access 'https://example.com/orders.git/': Could not resolve host", false)]
    public void LooksLikeReferenceFailure_BlamesTheReferenceOnlyWhenGitDoes(string standardError, bool expected) =>
        Assert.Equal(expected, GitCliClient.LooksLikeReferenceFailure(standardError, "/home/x/orders"));
}
