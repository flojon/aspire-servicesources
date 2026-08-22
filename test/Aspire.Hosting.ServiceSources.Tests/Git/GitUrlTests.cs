using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class GitUrlTests
{
    [Theory]
    [InlineData("ssh://git@github.com/company/orders.git")]
    [InlineData("SSH://git@github.com/company/orders")]
    [InlineData("git@github.com:company/orders.git")]
    // scp-like syntax with the implicit current user — no '@' to key off, but still SSH.
    [InlineData("gitserver:company/orders.git")]
    public void IsSsh_SshForms_ReturnsTrue(string repositoryUrl) =>
        Assert.True(GitUrl.Parse(repositoryUrl).IsSsh);

    [Theory]
    [InlineData("https://github.com/company/orders.git")]
    [InlineData("http://gitserver/company/orders")]
    [InlineData("https://alice@github.com/company/orders")]
    // A Windows drive path's single-character prefix can never be a hostname.
    [InlineData(@"C:\repos\orders")]
    [InlineData("/home/alice/repos/orders")]
    [InlineData("../sibling/orders")]
    // A colon inside a path segment is not a host/path delimiter.
    [InlineData("/mnt/my:dir/orders")]
    public void IsSsh_NonSshForms_ReturnsFalse(string repositoryUrl) =>
        Assert.False(GitUrl.Parse(repositoryUrl).IsSsh);

    [Theory]
    [InlineData("https://github.com/company/orders")]
    [InlineData("https://github.com/company/orders.git")]
    [InlineData("https://github.com/company/orders/")]
    [InlineData("https://alice@github.com/company/orders")]
    [InlineData("git@github.com:company/orders.git")]
    [InlineData("ssh://git@github.com/company/orders")]
    [InlineData("  https://github.com/company/orders  ")]
    public void Identity_EquivalentFormsOfTheSameRepository_AreEqual(string repositoryUrl) =>
        Assert.Equal("github.com/company/orders", GitUrl.Parse(repositoryUrl).Identity);

    [Fact]
    public void Identity_DifferentRepositoriesOnTheSameHost_Differ() =>
        Assert.NotEqual(
            GitUrl.Parse("https://github.com/company/orders").Identity,
            GitUrl.Parse("https://github.com/company/other-repo").Identity);

    [Fact]
    public void Parse_ExplicitPort_KeepsItInTheHostAsGitCredentialExpects()
    {
        var url = GitUrl.Parse("https://gitserver:8443/company/orders.git");

        Assert.Equal("gitserver:8443", url.Host);
        Assert.Equal("company/orders", url.Path);
        Assert.True(url.IsHttp);
    }

    [Fact]
    public void Parse_HttpsUrlWithUserInfo_StripsItFromTheHost() =>
        Assert.Equal("github.com", GitUrl.Parse("https://alice:pat@github.com/company/orders").Host);

    [Fact]
    public void Parse_UserInfoContainingAnAtSign_StripsAllOfItFromTheHost()
    {
        // A personal access token pasted straight into the URL can itself contain '@'. Splitting on
        // the first one would leave the tail of the token in the host, which both misses the
        // credential-helper cache entry for the real host and asks `git credential` about a host
        // that doesn't exist.
        var url = GitUrl.Parse("https://alice:pa@ss@github.com/company/orders");

        Assert.Equal("github.com", url.Host);
        Assert.Equal("company/orders", url.Path);
    }

    [Fact]
    public void Parse_AtSignInThePathOnly_IsNotTreatedAsUserInfo()
    {
        var url = GitUrl.Parse("https://gitserver/company/orders@v2");

        Assert.Equal("gitserver", url.Host);
        Assert.Equal("company/orders@v2", url.Path);
    }

    [Fact]
    public void Parse_LocalPath_HasNoHostAndIsNotHttp()
    {
        var url = GitUrl.Parse("/home/alice/repos/orders");

        Assert.Null(url.Host);
        Assert.False(url.IsHttp);
        Assert.Equal("/home/alice/repos/orders", url.Identity);
    }

    [Fact]
    public void Parse_ScpSyntax_SplitsHostFromPath()
    {
        var url = GitUrl.Parse("git@github.com:company/orders.git");

        Assert.Equal("github.com", url.Host);
        Assert.Equal("company/orders", url.Path);
        Assert.True(url.IsScpSyntax);
        Assert.Null(url.Scheme);
    }

    [Theory]
    // A token pasted in as the password.
    [InlineData("https://alice:ghp_secret@github.com/company/orders.git", "https://github.com/company/orders.git")]
    // A token pasted in as the username, with no password at all — just as common, and just as secret,
    // so the whole userinfo goes rather than only the part after a ':'.
    [InlineData("https://ghp_secret@github.com/company/orders", "https://github.com/company/orders")]
    // A token containing '@' — split on the last one, as GitUrl.Parse does.
    [InlineData("https://alice:pa@ss@github.com/company/orders", "https://github.com/company/orders")]
    [InlineData("http://alice:secret@gitserver:8443/company/orders", "http://gitserver:8443/company/orders")]
    [InlineData("ssh://alice:secret@gitserver/company/orders", "ssh://gitserver/company/orders")]
    public void Redact_UrlCarryingCredentials_RemovesTheWholeUserInfo(string repositoryUrl, string expected) =>
        Assert.Equal(expected, GitUrl.Redact(repositoryUrl));

    [Theory]
    [InlineData("https://github.com/company/orders.git")]
    [InlineData("https://github.com/company/orders/")]
    // scp-like syntax has no password component, so its user is an SSH account name, not a secret.
    [InlineData("git@github.com:company/orders.git")]
    [InlineData("/home/alice/repos/orders")]
    [InlineData(@"C:\repos\orders")]
    // An '@' after the authority belongs to the path.
    [InlineData("https://gitserver/company/orders@v2")]
    public void Redact_NothingSecretToRemove_ReturnsTheUrlByteForByte(string repositoryUrl) =>
        Assert.Equal(repositoryUrl, GitUrl.Redact(repositoryUrl));

    [Fact]
    public void Redact_UrlWithNoPathAtAll_StillRemovesTheUserInfo() =>
        Assert.Equal("https://gitserver", GitUrl.Redact("https://alice:secret@gitserver"));
}
