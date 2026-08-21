using System.Diagnostics;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Resolves credentials for HTTPS git operations by first asking the local <c>git</c>
/// installation's own credential helper (<c>git credential fill</c>) — reusing whatever
/// Git Credential Manager, <c>osxkeychain</c>, <c>libsecret</c>, cached tokens, etc. the
/// developer already has configured — and falling back to the
/// <c>SERVICESOURCES_GIT_USERNAME</c>/<c>SERVICESOURCES_GIT_TOKEN</c> environment variables
/// when the helper yields nothing (e.g. no helper configured, or <c>git</c> not on PATH).
/// </summary>
internal static class GitCredentialResolver
{
    private const string UsernameEnvironmentVariable = "SERVICESOURCES_GIT_USERNAME";
    private const string TokenEnvironmentVariable = "SERVICESOURCES_GIT_TOKEN";

    private static readonly TimeSpan CredentialHelperTimeout = TimeSpan.FromSeconds(30);

    public static CredentialsHandler CreateProvider(string repositoryUrl) =>
        (_, _, _) => Resolve(repositoryUrl);

    private static Credentials Resolve(string repositoryUrl)
    {
        if (TryGitCredentialFill(repositoryUrl) is { } fromHelper)
        {
            return fromHelper;
        }

        var token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (!string.IsNullOrEmpty(token))
        {
            return new UsernamePasswordCredentials
            {
                Username = Environment.GetEnvironmentVariable(UsernameEnvironmentVariable) ?? "git",
                Password = token,
            };
        }

        return new DefaultCredentials();
    }

    private static UsernamePasswordCredentials? TryGitCredentialFill(string repositoryUrl)
    {
        if (!TryGetProtocolAndHost(repositoryUrl, out var protocol, out var host))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo("git", "credential fill")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();

            process.StandardInput.Write($"protocol={protocol}\nhost={host}\n\n");
            process.StandardInput.Flush();
            process.StandardInput.Close();

            if (!stdoutTask.Wait(CredentialHelperTimeout))
            {
                TryKill(process);
                return null;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                return null;
            }

            return ParseCredentials(stdoutTask.Result);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // `git` isn't on PATH, or the credential helper failed to launch — fall back silently.
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between the timeout check and the kill attempt.
        }
    }

    private static UsernamePasswordCredentials? ParseCredentials(string output)
    {
        string? username = null;
        string? password = null;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex < 0)
            {
                continue;
            }

            var key = line[..separatorIndex];
            var value = line[(separatorIndex + 1)..].TrimEnd('\r');

            if (key == "username")
            {
                username = value;
            }
            else if (key == "password")
            {
                password = value;
            }
        }

        return username is not null && password is not null
            ? new UsernamePasswordCredentials { Username = username, Password = password }
            : null;
    }

    private static bool TryGetProtocolAndHost(string repositoryUrl, out string protocol, out string host)
    {
        protocol = "";
        host = "";

        var schemeIndex = repositoryUrl.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex < 0)
        {
            return false;
        }

        protocol = repositoryUrl[..schemeIndex];
        if (!protocol.Equals("http", StringComparison.OrdinalIgnoreCase) &&
            !protocol.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = repositoryUrl[(schemeIndex + 3)..];
        var atIndex = rest.IndexOf('@');
        var slashIndex = rest.IndexOf('/');
        if (atIndex >= 0 && (slashIndex < 0 || atIndex < slashIndex))
        {
            rest = rest[(atIndex + 1)..];
            slashIndex = rest.IndexOf('/');
        }

        host = slashIndex >= 0 ? rest[..slashIndex] : rest;
        return host.Length > 0;
    }
}
