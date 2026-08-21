using Aspire.Hosting.ServiceSources.Git;
using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class GitCredentialResolverTests
{
    [Fact]
    public void CreateProvider_NoHelperNoEnvironmentVariables_ReturnsDefaultCredentials()
    {
        WithEnvironmentVariables(username: null, token: null, () =>
        {
            var provider = GitCredentialResolver.CreateProvider("https://example.invalid/org/repo");

            var credentials = provider("https://example.invalid/org/repo", null, SupportedCredentialTypes.UsernamePassword);

            Assert.IsType<DefaultCredentials>(credentials);
        });
    }

    [Fact]
    public void CreateProvider_TokenEnvironmentVariableSet_UsesItAsPasswordWithDefaultUsername()
    {
        WithEnvironmentVariables(username: null, token: "s3cr3t", () =>
        {
            var provider = GitCredentialResolver.CreateProvider("https://example.invalid/org/repo");

            var credentials = Assert.IsType<UsernamePasswordCredentials>(
                provider("https://example.invalid/org/repo", null, SupportedCredentialTypes.UsernamePassword));

            Assert.Equal("git", credentials.Username);
            Assert.Equal("s3cr3t", credentials.Password);
        });
    }

    [Fact]
    public void CreateProvider_UsernameAndTokenEnvironmentVariablesSet_UsesBoth()
    {
        WithEnvironmentVariables(username: "alice", token: "s3cr3t", () =>
        {
            var provider = GitCredentialResolver.CreateProvider("https://example.invalid/org/repo");

            var credentials = Assert.IsType<UsernamePasswordCredentials>(
                provider("https://example.invalid/org/repo", null, SupportedCredentialTypes.UsernamePassword));

            Assert.Equal("alice", credentials.Username);
            Assert.Equal("s3cr3t", credentials.Password);
        });
    }

    private static void WithEnvironmentVariables(string? username, string? token, Action action)
    {
        const string UsernameVariable = "SERVICESOURCES_GIT_USERNAME";
        const string TokenVariable = "SERVICESOURCES_GIT_TOKEN";

        var originalUsername = Environment.GetEnvironmentVariable(UsernameVariable);
        var originalToken = Environment.GetEnvironmentVariable(TokenVariable);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable(UsernameVariable, username);
            Environment.SetEnvironmentVariable(TokenVariable, token);
            // Make `git credential fill` unresolvable so these tests exercise only the
            // environment-variable fallback, not whatever credential helper happens to be
            // configured on the machine running the tests.
            Environment.SetEnvironmentVariable("PATH", "");

            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(UsernameVariable, originalUsername);
            Environment.SetEnvironmentVariable(TokenVariable, originalToken);
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
