using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// Pins the libgit2 behaviour the credential fallback ladder is built on: that the credentials
/// callback is invoked again for a host only when the credential it was last handed was refused.
/// The ladder steps down a rung on every re-invocation and reports the refused credential to
/// <c>git credential reject</c>, so if a libgit2 upgrade ever started asking speculatively it would
/// erase credentials that actually work — these tests are what would catch that.
/// </summary>
public class LibGit2CredentialCallbackContractTests
{
    [Fact]
    public void CredentialsCallback_ServerRefusesTheCredential_IsInvokedAgainForTheSameHost()
    {
        using var server = StubGitServer.RefusingEverything();

        var requests = CloneCountingCredentialRequests(server);

        Assert.True(requests.Count > 1, $"expected a retry, saw {requests.Count} credential request(s)");
    }

    [Fact]
    public void CredentialsCallback_ServerAcceptsTheCredential_IsNotInvokedAgain()
    {
        using var server = StubGitServer.Accepting("u", "p");

        var requests = CloneCountingCredentialRequests(server);

        Assert.Single(requests);
    }

    /// <summary>
    /// A clone makes two HTTP requests — the ref advertisement, then the pack POST — and each one is
    /// a chance for libgit2 to come back to the callback without the credential having been refused.
    /// The ladder would read that as a rejection and erase a working credential from the developer's
    /// helper, so it is asserted over the whole clone and not just the first request. The pack
    /// request is asserted to have happened: if the stub's advertisement ever stops parsing, the
    /// clone would end after one request and the single-ask assertion would pass without having
    /// tested anything.
    /// </summary>
    [Fact]
    public void CredentialsCallback_AcceptedCredentialCarriesThePackRequestToo_IsAskedOnlyOnce()
    {
        using var server = StubGitServer.Accepting("u", "p");

        var requests = CloneCountingCredentialRequests(server);

        Assert.Contains(StubGitServer.PackRequest, server.Requests);
        Assert.Single(requests);
    }

    [Fact]
    public void CredentialsCallback_IsOnlyAskedAfterAnUnauthenticatedAttemptWasRefused()
    {
        using var server = StubGitServer.Accepting("u", "p");

        CloneCountingCredentialRequests(server);

        // libgit2 asks for credentials in response to a 401, never before one, so the first rung of
        // the ladder is only ever spent on a server that actually demanded authentication.
        Assert.Equal(StubGitServer.NoAuthorization, server.Authorizations[0]);
    }

    private static List<string> CloneCountingCredentialRequests(StubGitServer server)
    {
        var requests = new List<string>();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");

        try
        {
            Repository.Clone(server.RepositoryUrl, destination, new CloneOptions
            {
                FetchOptions =
                {
                    CredentialsProvider = (url, _, _) =>
                    {
                        lock (requests)
                        {
                            requests.Add(url);
                        }

                        return new UsernamePasswordCredentials { Username = "u", Password = "p" };
                    },
                },
            });
        }
        catch (LibGit2SharpException)
        {
            // No stub serves a real repository; only the credential handshake is under test.
        }

        return requests;
    }
}
