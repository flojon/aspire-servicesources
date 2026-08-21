using System.Collections.Concurrent;
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

    /// <summary>
    /// Cached <c>git credential fill</c> results, keyed by "protocol://host". libgit2 can invoke
    /// the credentials callback several times for a single clone or fetch (an anonymous attempt
    /// followed by a 401-driven retry, a redirect to another host), and an AppHost resolves its
    /// services in parallel, so several services sharing one host would otherwise each re-run the
    /// subprocess. The helper is deterministic per host, so caching changes nothing but the cost.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Lazy<HelperCredentials?>> HelperCache =
        new(StringComparer.Ordinal);

    public static CredentialsHandler CreateProvider(string repositoryUrl) =>
        CreateProvider(repositoryUrl, Environment.GetEnvironmentVariable, CachedGitCredentialFill);

    /// <summary>
    /// Test seam: <paramref name="environment"/> and <paramref name="credentialHelper"/> stand in
    /// for the process environment and the <c>git credential fill</c> subprocess, so the fallback
    /// order can be exercised without mutating process-wide state.
    /// </summary>
    internal static CredentialsHandler CreateProvider(
        string repositoryUrl,
        Func<string, string?> environment,
        Func<GitUrl, HelperCredentials?> credentialHelper) =>
        // libgit2 passes the URL it is actually authenticating against, which need not be the one
        // we were configured with (a redirect, or a submodule remote), so prefer it when supplied.
        (url, _, _) => Resolve(string.IsNullOrEmpty(url) ? repositoryUrl : url, environment, credentialHelper);

    private static Credentials Resolve(
        string repositoryUrl,
        Func<string, string?> environment,
        Func<GitUrl, HelperCredentials?> credentialHelper)
    {
        var parsed = GitUrl.Parse(repositoryUrl);
        if (parsed is { IsHttp: true, Host: not null } && credentialHelper(parsed) is { } fromHelper)
        {
            return new UsernamePasswordCredentials { Username = fromHelper.Username, Password = fromHelper.Password };
        }

        var token = environment(TokenEnvironmentVariable);
        if (!string.IsNullOrEmpty(token))
        {
            return new UsernamePasswordCredentials
            {
                Username = environment(UsernameEnvironmentVariable) ?? "git",
                Password = token,
            };
        }

        return new DefaultCredentials();
    }

    private static HelperCredentials? CachedGitCredentialFill(GitUrl url) =>
        HelperCache.GetOrAdd(
            $"{url.Scheme}://{url.Host}",
            _ => new Lazy<HelperCredentials?>(
                () => GitCredentialFill(url.Scheme!, url.Host!),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static HelperCredentials? GitCredentialFill(string protocol, string host)
    {
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
            // Drain stderr too. A helper that writes diagnostics there (verbose GCM builds, a
            // locked keychain) would otherwise fill the pipe buffer, block mid-write, and never
            // exit — stalling resolution for the full timeout on every clone and fetch.
            var stderrTask = process.StandardError.ReadToEndAsync();

            process.StandardInput.Write($"protocol={protocol}\nhost={host}\n\n");
            process.StandardInput.Flush();
            process.StandardInput.Close();

            // Task.Wait would rethrow a faulted read wrapped in an AggregateException, which the
            // catch filter below does not match — so it would escape across libgit2's native
            // callback boundary. Task.WhenAny never faults, which keeps a crashed helper on the
            // silent fall-back path.
            var reads = Task.WhenAll(stdoutTask, stderrTask);
            if (Task.WhenAny(reads, Task.Delay(CredentialHelperTimeout)).GetAwaiter().GetResult() != reads
                || !stdoutTask.IsCompletedSuccessfully)
            {
                TryKill(process);
                return null;
            }

            // Both pipes are at EOF by now, so the process is on its way out — but never block the
            // AppHost indefinitely on a helper that refuses to exit.
            if (!process.WaitForExit((int)CredentialHelperTimeout.TotalMilliseconds))
            {
                TryKill(process);
                return null;
            }

            return process.ExitCode == 0 ? ParseCredentials(stdoutTask.Result) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
                                      or InvalidOperationException or ObjectDisposedException)
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

    internal static HelperCredentials? ParseCredentials(string output)
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

        return username is not null && password is not null ? new HelperCredentials(username, password) : null;
    }
}

/// <summary>
/// A username/password pair resolved from a credential helper. Kept separate from LibGit2Sharp's
/// <see cref="UsernamePasswordCredentials"/> so the cached value is plain data and every callback
/// invocation hands libgit2 a fresh credentials instance to marshal.
/// </summary>
internal sealed record HelperCredentials(string Username, string Password);
