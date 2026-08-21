using Aspire.Hosting.ServiceSources.Git;
using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class GitCredentialResolverTests
{
    private const string RepositoryUrl = "https://example.invalid/org/repo";

    [Fact]
    public void CreateProvider_NoHelperNoEnvironmentVariables_ReturnsDefaultCredentials()
    {
        var provider = CreateProvider();

        var credentials = provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);

        Assert.IsType<DefaultCredentials>(credentials);
    }

    [Fact]
    public void CreateProvider_TokenEnvironmentVariableSet_UsesItAsPasswordWithDefaultUsername()
    {
        var provider = CreateProvider(token: "s3cr3t");

        var credentials = Assert.IsType<UsernamePasswordCredentials>(
            provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword));

        Assert.Equal("git", credentials.Username);
        Assert.Equal("s3cr3t", credentials.Password);
    }

    [Fact]
    public void CreateProvider_UsernameAndTokenEnvironmentVariablesSet_UsesBoth()
    {
        var provider = CreateProvider(username: "alice", token: "s3cr3t");

        var credentials = Assert.IsType<UsernamePasswordCredentials>(
            provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword));

        Assert.Equal("alice", credentials.Username);
        Assert.Equal("s3cr3t", credentials.Password);
    }

    [Fact]
    public void CreateProvider_HelperResolvesCredentials_PreferredOverEnvironmentVariables()
    {
        var provider = CreateProvider(
            username: "alice",
            token: "s3cr3t",
            helper: _ => new HelperCredentials("from-helper", "helper-token"));

        var credentials = Assert.IsType<UsernamePasswordCredentials>(
            provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword));

        Assert.Equal("from-helper", credentials.Username);
        Assert.Equal("helper-token", credentials.Password);
    }

    [Fact]
    public void CreateProvider_HelperYieldsNothing_FallsBackToEnvironmentVariables()
    {
        var provider = CreateProvider(token: "s3cr3t", helper: _ => null);

        var credentials = Assert.IsType<UsernamePasswordCredentials>(
            provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword));

        Assert.Equal("s3cr3t", credentials.Password);
    }

    [Fact]
    public void CreateProvider_SshRepositoryUrl_DoesNotConsultHelper()
    {
        var helperCalls = 0;
        var provider = CreateProvider(helper: _ =>
        {
            helperCalls++;
            return new HelperCredentials("alice", "s3cr3t");
        });

        provider("git@example.invalid:org/repo.git", null, SupportedCredentialTypes.UsernamePassword);

        Assert.Equal(0, helperCalls);
    }

    [Fact]
    public void CreateProvider_LibGit2PassesDifferentUrl_ResolvesAgainstThatUrlNotTheConfiguredOne()
    {
        GitUrl? seen = null;
        var provider = CreateProvider(helper: url =>
        {
            seen = url;
            return new HelperCredentials("alice", "s3cr3t");
        });

        // libgit2 hands the callback the URL it is actually authenticating against, which can
        // differ from the configured one after a redirect.
        provider("https://redirected.invalid/other/repo", null, SupportedCredentialTypes.UsernamePassword);

        Assert.Equal("redirected.invalid", seen?.Host);
    }

    [Fact]
    public void CreateProvider_LibGit2PassesNoUrl_FallsBackToTheConfiguredUrl()
    {
        GitUrl? seen = null;
        var provider = CreateProvider(helper: url =>
        {
            seen = url;
            return new HelperCredentials("alice", "s3cr3t");
        });

        provider("", null, SupportedCredentialTypes.UsernamePassword);

        Assert.Equal("example.invalid", seen?.Host);
    }

    [Fact]
    public void CreateProvider_HelperCredentialsRejected_FallsBackToEnvironmentVariables()
    {
        var provider = CreateProvider(
            token: "s3cr3t",
            helper: _ => new HelperCredentials("from-helper", "helper-token"));

        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);
        // libgit2 only comes back for the same host when the credential it was handed was refused.
        var credentials = Assert.IsType<UsernamePasswordCredentials>(
            provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword));

        Assert.Equal("s3cr3t", credentials.Password);
    }

    [Fact]
    public void CreateProvider_HelperCredentialsRejected_TellsTheHelperToForgetThem()
    {
        var forgotten = new List<(GitUrl Url, HelperCredentials Credentials)>();
        var provider = CreateProvider(
            helper: _ => new HelperCredentials("from-helper", "helper-token"),
            forget: (url, credentials) => forgotten.Add((url, credentials)));

        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);
        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);

        var (forgottenUrl, forgottenCredentials) = Assert.Single(forgotten);
        Assert.Equal("example.invalid", forgottenUrl.Host);
        Assert.Equal(new HelperCredentials("from-helper", "helper-token"), forgottenCredentials);
    }

    [Fact]
    public void CreateProvider_HelperCredentialsRejected_DoesNotConsultTheHelperAgain()
    {
        var helperCalls = 0;
        var provider = CreateProvider(helper: _ =>
        {
            helperCalls++;
            return new HelperCredentials("from-helper", "helper-token");
        });

        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);
        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);
        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);

        // Re-running `git credential fill` after erasing the entry would pop an interactive prompt
        // in the middle of an AppHost run.
        Assert.Equal(1, helperCalls);
    }

    [Fact]
    public void CreateProvider_HelperCredentialsAccepted_LeavesThemInTheHelper()
    {
        var forgotten = 0;
        var provider = CreateProvider(
            helper: _ => new HelperCredentials("from-helper", "helper-token"),
            forget: (_, _) => forgotten++);

        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);

        Assert.Equal(0, forgotten);
    }

    [Fact]
    public void CreateProvider_EveryCredentialRejected_FallsBackToDefaultCredentials()
    {
        var provider = CreateProvider(
            token: "s3cr3t",
            helper: _ => new HelperCredentials("from-helper", "helper-token"));

        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);
        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);
        var credentials = provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);

        Assert.IsType<DefaultCredentials>(credentials);
    }

    [Fact]
    public void CreateProvider_EnvironmentCredentialsRejected_ForgetsNothing()
    {
        var forgotten = 0;
        var provider = CreateProvider(
            token: "s3cr3t",
            helper: _ => null,
            forget: (_, _) => forgotten++);

        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);
        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);

        // `git credential reject` must only ever name a credential the helper itself handed out.
        Assert.Equal(0, forgotten);
    }

    [Fact]
    public void CreateProvider_RedirectToAnotherHost_StartsThatHostAtTheHelperAgain()
    {
        var provider = CreateProvider(
            token: "s3cr3t",
            helper: url => new HelperCredentials($"from-helper-{url.Host}", "helper-token"));

        provider(RepositoryUrl, null, SupportedCredentialTypes.UsernamePassword);
        var credentials = Assert.IsType<UsernamePasswordCredentials>(
            provider("https://redirected.invalid/other/repo", null, SupportedCredentialTypes.UsernamePassword));

        Assert.Equal("from-helper-redirected.invalid", credentials.Username);
    }

    [Fact]
    public void ParseCredentials_UsernameAndPasswordPresent_ParsesBoth()
    {
        var credentials = GitCredentialResolver.ParseCredentials(
            "protocol=https\nhost=example.invalid\nusername=alice\npassword=s3cr3t\n");

        Assert.Equal("alice", credentials?.Username);
        Assert.Equal("s3cr3t", credentials?.Password);
    }

    [Theory]
    [InlineData("protocol=https\nhost=example.invalid\n")]
    [InlineData("username=alice\n")]
    [InlineData("password=s3cr3t\n")]
    [InlineData("")]
    public void ParseCredentials_IncompleteOutput_ReturnsNull(string output) =>
        Assert.Null(GitCredentialResolver.ParseCredentials(output));

    [Fact]
    public void ParseCredentials_PasswordContainsEqualsSign_KeepsItIntact()
    {
        var credentials = GitCredentialResolver.ParseCredentials("username=alice\npassword=a=b=c\n");

        Assert.Equal("a=b=c", credentials?.Password);
    }

    private static LibGit2Sharp.Handlers.CredentialsHandler CreateProvider(
        string? username = null,
        string? token = null,
        Func<GitUrl, HelperCredentials?>? helper = null,
        Action<GitUrl, HelperCredentials>? forget = null) =>
        GitCredentialResolver.CreateProvider(
            RepositoryUrl,
            name => name switch
            {
                "SERVICESOURCES_GIT_USERNAME" => username,
                "SERVICESOURCES_GIT_TOKEN" => token,
                _ => null,
            },
            helper ?? (_ => null),
            forget ?? ((_, _) => { }));
}
