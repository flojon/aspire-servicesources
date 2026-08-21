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
}
