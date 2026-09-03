using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// Drives the credential ladder through real <c>git</c> against a server that demands Basic auth,
/// so what the AppHost actually puts on the wire — and in what order — is checked end to end.
/// </summary>
/// <remarks>
/// git is given an empty global and system config, so the only helper it can reach is the one a
/// test configures. Without that these assertions would depend on whatever credential helper the
/// machine running the suite happens to have set up for <c>127.0.0.1</c>.
/// </remarks>
public class GitCliCredentialLadderTests
{
    private const string EnvironmentToken = "env-token";
    private const string HelperUsername = "alice";
    private const string HelperPassword = "helper-token";

    [Fact]
    public void NoHelperConfigured_TheEnvironmentTokenIsSent()
    {
        using var server = StubGitServer.Accepting("git", EnvironmentToken);

        Clone(server, token: EnvironmentToken);

        Assert.Contains(StubGitServer.BasicAuthorization("git", EnvironmentToken), server.Authorizations);
    }

    [Fact]
    public void NoHelperConfigured_TheEnvironmentUsernameIsUsedWhenSet()
    {
        using var server = StubGitServer.Accepting("bob", EnvironmentToken);

        Clone(server, token: EnvironmentToken, username: "bob");

        Assert.Contains(StubGitServer.BasicAuthorization("bob", EnvironmentToken), server.Authorizations);
    }

    [Fact]
    public void AConfiguredHelperAnswers_ItsCredentialIsUsedAndTheEnvironmentTokenIsNot()
    {
        using var server = StubGitServer.Accepting(HelperUsername, HelperPassword);

        Clone(server, token: EnvironmentToken, configureHelper: true);

        // The developer's own credential store is the first rung: an environment token is a
        // fallback for when it yields nothing, not an override of it.
        Assert.Contains(StubGitServer.BasicAuthorization(HelperUsername, HelperPassword), server.Authorizations);
        Assert.DoesNotContain(StubGitServer.BasicAuthorization("git", EnvironmentToken), server.Authorizations);
    }

    [Fact]
    public void HelperCredentialRefused_TheEnvironmentTokenIsSentOnTheRetry()
    {
        using var server = StubGitServer.Accepting("git", EnvironmentToken);

        // The helper answers first and the server refuses it, which ends that git process. The
        // environment token only gets its turn because the command is re-run with the configured
        // helpers cleared.
        Clone(server, token: EnvironmentToken, configureHelper: true);

        // Distinct, because a clone makes two authenticated requests — the ref advertisement and
        // the pack POST — so the credential that is finally accepted is sent on both. What matters
        // is which credentials were tried, and in what order.
        Assert.Equal(
            [
                StubGitServer.BasicAuthorization(HelperUsername, HelperPassword),
                StubGitServer.BasicAuthorization("git", EnvironmentToken),
            ],
            OfferedCredentials(server));
    }

    [Fact]
    public void HelperCredentialRefusedAndNoEnvironmentToken_IsNotRetried()
    {
        using var server = StubGitServer.RefusingEverything();

        Clone(server, configureHelper: true);

        // Nothing to step down to, so re-running would only ask the same refused question again.
        Assert.Equal([StubGitServer.BasicAuthorization(HelperUsername, HelperPassword)], OfferedCredentials(server));
    }

    [Fact]
    public void EveryCredentialRefused_ReportsAnAuthenticationFailure()
    {
        using var server = StubGitServer.RefusingEverything();

        var exception = CloneExpectingAuthFailure(server, token: EnvironmentToken, configureHelper: true);

        // A credential was offered and turned down, so the remediation is about the credential's
        // contents — not about there being no credential to find.
        Assert.False(exception.NoCredentialsResolved);
    }

    [Fact]
    public void NoCredentialAnywhere_SaysNoneWasResolvedRatherThanBlamingTheCredential()
    {
        using var server = StubGitServer.RefusingEverything();

        var exception = CloneExpectingAuthFailure(server);

        // Nothing was ever offered, so nothing was refused: the fix is to make a credential
        // resolvable at all, not to check an existing one.
        Assert.True(exception.NoCredentialsResolved);
    }

    [Fact]
    public void AnEnvironmentTokenContainingShellMetacharacters_IsSentVerbatim()
    {
        // The environment-variable rung is a shell credential helper, so a token that reads as
        // shell syntax is exactly what would break it — and a mangled token fails as an
        // authentication error, which looks like the developer's token being wrong.
        const string AwkwardToken = "p@ss w\"rd\\x$(id)`whoami`;#";
        using var server = StubGitServer.Accepting("git", AwkwardToken);

        Clone(server, token: AwkwardToken);

        Assert.Contains(StubGitServer.BasicAuthorization("git", AwkwardToken), server.Authorizations);
    }

    [Fact]
    public void ATokenEmbeddedInTheRepositoryUrl_IsNotRepeatedInTheError()
    {
        const string UrlToken = "supersecrettokenvalue";
        using var server = StubGitServer.RefusingEverything();
        var urlWithToken = server.RepositoryUrl.Replace("http://", $"http://{UrlToken}@", StringComparison.Ordinal);

        var exception = Assert.ThrowsAny<Exception>(
            () => new GitCliClient(Environment()).Clone(urlWithToken, TestRepository.EmptyDestination()));

        // git names the URL it failed on, and that message becomes an exception message that
        // reaches the console and every log sink the AppHost is wired to.
        Assert.DoesNotContain(UrlToken, exception.Message, StringComparison.Ordinal);
        Assert.Contains("127.0.0.1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HelperCredentialRefused_TheRetryReportsToTheSameProgressStream()
    {
        using var server = StubGitServer.Accepting("git", EnvironmentToken);
        var progress = new RecordingProgressSink();

        // The ladder's second rung re-runs the whole command, so this stream is written by two
        // separate git processes one after the other.
        Clone(server, token: EnvironmentToken, configureHelper: true, progress: progress);

        // Both of them announced themselves, so the stream carries the second attempt rather than
        // ending with the first. Each invocation gets a reader of its own, so a line left unfinished
        // when one process exits cannot run into the next one's first line.
        Assert.Equal(2, progress.CountStartingWith("Cloning into"));

        // Which is also what makes the restart legible: the failure that caused it is in the stream
        // between the two, so a percentage that starts over has its reason directly above it.
        Assert.Contains(progress.Lines, line => line.Contains("Authentication failed", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNothingToRetryWith_TheProgressStreamCarriesTheOneAttempt()
    {
        using var server = StubGitServer.RefusingEverything();
        var progress = new RecordingProgressSink();

        Clone(server, configureHelper: true, progress: progress);

        Assert.Equal(1, progress.CountStartingWith("Cloning into"));
    }

    /// <summary>
    /// The distinct credentials the client offered, in the order it first offered them. Requests
    /// carrying none are dropped: git always makes an unauthenticated attempt first, and each
    /// re-run of the command makes its own.
    /// </summary>
    private static IEnumerable<string> OfferedCredentials(StubGitServer server) =>
        server.Authorizations
            .Where(authorization => authorization != StubGitServer.NoAuthorization)
            .Distinct();

    /// <summary>
    /// Clones from the stub, swallowing the failure. No stub serves a real repository, so a clone
    /// always ends in an error whatever the credentials — only the handshake is under test.
    /// </summary>
    private static void Clone(
        StubGitServer server,
        string? token = null,
        string? username = null,
        bool configureHelper = false,
        IGitProgressSink? progress = null)
    {
        try
        {
            new GitCliClient(Environment(token, username, configureHelper, server))
                .Clone(server.RepositoryUrl, TestRepository.EmptyDestination(), progress);
        }
        catch (Exception ex) when (ex is GitAuthenticationFailedException or GitCommandFailedException)
        {
        }
    }

    private static GitAuthenticationFailedException CloneExpectingAuthFailure(
        StubGitServer server, string? token = null, bool configureHelper = false) =>
        Assert.Throws<GitAuthenticationFailedException>(
            () => new GitCliClient(Environment(token, username: null, configureHelper, server))
                .Clone(server.RepositoryUrl, TestRepository.EmptyDestination()));

    /// <summary>
    /// An environment holding only what the test asks for: an empty global config, optionally
    /// carrying a <c>credential.helper</c>, and optionally the environment-variable credentials.
    /// </summary>
    private static Dictionary<string, string?> Environment(
        string? token = null, string? username = null, bool configureHelper = false, StubGitServer? server = null)
    {
        var environment = TestRepository.IsolatedEnvironment();

        if (token is not null)
        {
            environment["SERVICESOURCES_GIT_TOKEN"] = token;
        }

        if (username is not null)
        {
            environment["SERVICESOURCES_GIT_USERNAME"] = username;
        }

        if (configureHelper)
        {
            environment["GIT_CONFIG_GLOBAL"] = WriteHelperConfig(server!);
        }

        return environment;
    }

    /// <summary>
    /// A global config whose <c>credential.helper</c> is git's own <c>store</c>, backed by a file
    /// holding one credential for the stub's host. Using a built-in helper rather than a shell
    /// snippet keeps the first rung of the ladder — the developer's own credential store — free of
    /// anything this package supplies, which is the point of the test.
    /// </summary>
    private static string WriteHelperConfig(StubGitServer server)
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        var credentials = Path.Combine(directory, "credentials");
        var host = new Uri(server.RepositoryUrl).Authority;

        File.WriteAllText(credentials, $"http://{HelperUsername}:{HelperPassword}@{host}\n");

        var config = Path.Combine(directory, "gitconfig");
        // Forward slashes even on Windows: a backslash in a config value is an escape character.
        File.WriteAllText(
            config,
            $"[credential]\n\thelper = store --file={credentials.Replace('\\', '/')}\n");

        return config;
    }
}
