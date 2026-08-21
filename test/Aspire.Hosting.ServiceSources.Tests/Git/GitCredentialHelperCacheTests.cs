using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class GitCredentialHelperCacheTests
{
    [Fact]
    public void Get_SameHostTwice_RunsTheCredentialHelperOnce()
    {
        var fills = 0;
        var cache = CreateCache(() => fills++);

        cache.Get(GitUrl.Parse("https://example.invalid/org/repo"));
        cache.Get(GitUrl.Parse("https://example.invalid/other/repo"));

        Assert.Equal(1, fills);
    }

    [Fact]
    public void Get_DifferentHosts_RunsTheCredentialHelperPerHost()
    {
        var fills = 0;
        var cache = CreateCache(() => fills++);

        cache.Get(GitUrl.Parse("https://example.invalid/org/repo"));
        cache.Get(GitUrl.Parse("https://other.invalid/org/repo"));

        Assert.Equal(2, fills);
    }

    [Fact]
    public void Get_AfterForget_RunsTheCredentialHelperAgain()
    {
        var fills = 0;
        var cache = CreateCache(() => fills++);
        var url = GitUrl.Parse("https://example.invalid/org/repo");

        cache.Get(url);
        cache.Forget(url);
        cache.Get(url);

        Assert.Equal(2, fills);
    }

    [Fact]
    public void Get_AfterForget_ReturnsWhatTheHelperNowHolds()
    {
        var passwords = new Queue<string>(["stale-token", "rotated-token"]);
        var cache = new GitCredentialHelperCache((_, _) => new HelperCredentials("alice", passwords.Dequeue()));
        var url = GitUrl.Parse("https://example.invalid/org/repo");

        cache.Get(url);
        cache.Forget(url);

        Assert.Equal("rotated-token", cache.Get(url)?.Password);
    }

    [Fact]
    public void Get_HostWithExplicitPort_IsCachedSeparatelyFromTheSameHostWithout()
    {
        var fills = 0;
        var cache = CreateCache(() => fills++);

        cache.Get(GitUrl.Parse("https://example.invalid/org/repo"));
        cache.Get(GitUrl.Parse("https://example.invalid:8443/org/repo"));

        Assert.Equal(2, fills);
    }

    [Fact]
    public void Forget_HostThatWasNeverFilled_DoesNothing()
    {
        var cache = CreateCache(() => { });

        cache.Forget(GitUrl.Parse("https://example.invalid/org/repo"));
    }

    private static GitCredentialHelperCache CreateCache(Action onFill) =>
        new((_, _) =>
        {
            onFill();
            return new HelperCredentials("alice", "s3cr3t");
        });
}
