using Aspire.Hosting.ServiceSources.Git;
using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// Drives the real fallback ladder through real libgit2 against a server that refuses credentials,
/// so what the AppHost actually puts on the wire after a rejection is checked end to end rather
/// than only through the resolver's test seam.
/// </summary>
public class GitCredentialLadderOverHttpTests
{
    [Fact]
    public void HelperCredentialRefused_TheEnvironmentTokenIsSentOnTheRetry()
    {
        using var server = StubGitServer.Accepting("git", "env-token");

        Clone(server, helper: _ => new HelperCredentials("alice", "helper-token"), token: "env-token");

        Assert.Contains(StubGitServer.BasicAuthorization("git", "env-token"), server.Authorizations);
    }

    [Fact]
    public void HelperCredentialRefused_IsHandedBackToTheHelperForErasure()
    {
        using var server = StubGitServer.RefusingEverything();
        var forgotten = new List<HelperCredentials>();

        Clone(
            server,
            helper: _ => new HelperCredentials("alice", "helper-token"),
            forget: (_, credentials) => forgotten.Add(credentials));

        Assert.Equal(new HelperCredentials("alice", "helper-token"), Assert.Single(forgotten));
    }

    [Fact]
    public void HelperCredentialAccepted_IsLeftAloneInTheHelper()
    {
        using var server = StubGitServer.Accepting("alice", "helper-token");
        var forgotten = new List<HelperCredentials>();

        Clone(
            server,
            helper: _ => new HelperCredentials("alice", "helper-token"),
            forget: (_, credentials) => forgotten.Add(credentials));

        Assert.Empty(forgotten);
    }

    [Fact]
    public void EveryCredentialRefused_StopsReplayingThemAndFallsBackToAnonymous()
    {
        using var server = StubGitServer.RefusingEverything();

        Clone(server, helper: _ => new HelperCredentials("alice", "helper-token"), token: "env-token");

        // libgit2 keeps retrying until it gives up, but each credential is offered exactly once:
        // everything after the ladder runs out goes back to being an unauthenticated attempt.
        Assert.Equal(
            [StubGitServer.BasicAuthorization("alice", "helper-token"), StubGitServer.BasicAuthorization("git", "env-token")],
            server.Authorizations.Where(authorization => authorization != StubGitServer.NoAuthorization));
    }

    private static void Clone(
        StubGitServer server,
        Func<GitUrl, HelperCredentials?> helper,
        string? token = null,
        Action<GitUrl, HelperCredentials>? forget = null)
    {
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var provider = GitCredentialResolver.CreateProvider(
            server.RepositoryUrl,
            name => name == "SERVICESOURCES_GIT_TOKEN" ? token : null,
            helper,
            forget ?? ((_, _) => { }));

        try
        {
            Repository.Clone(
                server.RepositoryUrl,
                destination,
                new CloneOptions { FetchOptions = { CredentialsProvider = provider } });
        }
        catch (LibGit2SharpException)
        {
            // No stub serves a real repository; only the credential handshake is under test.
        }
    }
}
