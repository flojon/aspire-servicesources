using System.Collections.Concurrent;
using System.Diagnostics;
using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Resolves credentials for HTTPS git operations by first asking the local <c>git</c>
/// installation's own credential helper (<c>git credential fill</c>) — reusing whatever
/// Git Credential Manager, <c>osxkeychain</c>, <c>libsecret</c>, cached tokens, etc. the
/// developer already has configured — and falling back to the
/// <c>SERVICESOURCES_GIT_USERNAME</c>/<c>SERVICESOURCES_GIT_TOKEN</c> environment variables
/// when the helper yields nothing (e.g. no helper configured, or <c>git</c> not on PATH).
/// </summary>
/// <remarks>
/// The fallback is a ladder, not a single choice: libgit2 re-invokes the credentials callback for a
/// host only when the credential it was last handed came back refused, so each re-invocation steps
/// down a rung — helper, then environment variables, then anonymous. A refused helper credential is
/// also reported back with <c>git credential reject</c>, the same way git itself does it, so the
/// developer's helper erases its stored copy instead of serving the same refused token forever.
/// </remarks>
internal static class GitCredentialResolver
{
    private const string UsernameEnvironmentVariable = "SERVICESOURCES_GIT_USERNAME";
    private const string TokenEnvironmentVariable = "SERVICESOURCES_GIT_TOKEN";

    private static readonly TimeSpan CredentialHelperTimeout = TimeSpan.FromSeconds(30);

    private static readonly GitCredentialHelperCache HelperCache = new(GitCredentialFill);

    public static GitCredentialProvider CreateProvider(string repositoryUrl) =>
        CreateProvider(repositoryUrl, Environment.GetEnvironmentVariable, HelperCache.Get, ForgetHelperCredentials);

    /// <summary>
    /// Drops the cached credential for the repository's host without erasing anything the developer
    /// has stored. Called when an operation fails for what looks like an authentication reason, so
    /// the next attempt re-reads the helper rather than replaying a credential that has since been
    /// refused or rotated for as long as the AppHost process lives.
    /// </summary>
    internal static void ForgetCachedCredentials(string repositoryUrl) =>
        HelperCache.Forget(GitUrl.Parse(repositoryUrl));

    /// <summary>
    /// Test seam: <paramref name="environment"/>, <paramref name="credentialHelper"/> and
    /// <paramref name="forgetHelperCredentials"/> stand in for the process environment and the
    /// <c>git credential</c> subprocesses, so the fallback ladder can be exercised without mutating
    /// process-wide state or touching the developer's real credential store.
    /// </summary>
    internal static GitCredentialProvider CreateProvider(
        string repositoryUrl,
        Func<string, string?> environment,
        Func<GitUrl, HelperCredentials?> credentialHelper,
        Action<GitUrl, HelperCredentials> forgetHelperCredentials)
    {
        // One ladder per host, scoped to this clone or fetch: a fresh operation starts again at the
        // helper, but within an operation a host never gets the same refused credential twice.
        var ladders = new ConcurrentDictionary<string, CredentialLadder>(StringComparer.Ordinal);

        // libgit2 passes the URL it is actually authenticating against, which need not be the one
        // we were configured with (a redirect, or a submodule remote), so prefer it when supplied.
        return new GitCredentialProvider((url, _, _) =>
        {
            var parsed = GitUrl.Parse(string.IsNullOrEmpty(url) ? repositoryUrl : url);
            return ladders
                .GetOrAdd(
                    $"{parsed.Scheme}://{parsed.Host}",
                    _ => new CredentialLadder(parsed, environment, credentialHelper, forgetHelperCredentials))
                .Next();
        });
    }

    /// <summary>
    /// The ordered credentials for one host within one clone or fetch, handed out a rung at a time.
    /// </summary>
    private sealed class CredentialLadder(
        GitUrl url,
        Func<string, string?> environment,
        Func<GitUrl, HelperCredentials?> credentialHelper,
        Action<GitUrl, HelperCredentials> forgetHelperCredentials)
    {
        private readonly object _gate = new();
        private Queue<Rung>? _remaining;
        private HelperCredentials? _outstandingHelperCredentials;

        public Credentials Next()
        {
            lock (_gate)
            {
                // Built on first use so a host libgit2 never asks about — and an SSH remote, which
                // has no HTTPS credential to look up — never runs the helper subprocess.
                _remaining ??= BuildRungs();

                if (_outstandingHelperCredentials is { } refused)
                {
                    // Being asked again means the credential we just handed over was refused.
                    _outstandingHelperCredentials = null;
                    forgetHelperCredentials(url, refused);
                }

                if (_remaining.Count == 0)
                {
                    // Out of rungs: let libgit2 try anonymously and fail with the server's own
                    // answer, rather than replaying a credential already known to be refused.
                    return new DefaultCredentials();
                }

                var rung = _remaining.Dequeue();
                _outstandingHelperCredentials = rung.FromHelper;
                return new UsernamePasswordCredentials { Username = rung.Username, Password = rung.Password };
            }
        }

        private Queue<Rung> BuildRungs()
        {
            var rungs = new Queue<Rung>();

            if (url is { IsHttp: true, Host: not null } && credentialHelper(url) is { } fromHelper)
            {
                rungs.Enqueue(new Rung(fromHelper.Username, fromHelper.Password, fromHelper));
            }

            if (environment(TokenEnvironmentVariable) is { Length: > 0 } token)
            {
                rungs.Enqueue(new Rung(environment(UsernameEnvironmentVariable) ?? "git", token, FromHelper: null));
            }

            return rungs;
        }

        /// <param name="FromHelper">
        /// The credential helper entry this rung came from, or <see langword="null"/> when it came
        /// from the environment. Only a helper's own entry may be handed back to it for erasure.
        /// </param>
        private readonly record struct Rung(string Username, string Password, HelperCredentials? FromHelper);
    }

    private static void ForgetHelperCredentials(GitUrl url, HelperCredentials credentials)
    {
        HelperCache.Forget(url);

        // `git credential reject` is how git itself reports a refused credential back to the
        // helper, so Git Credential Manager, osxkeychain, libsecret and friends erase their stored
        // copy and resolve afresh next time. Without it a rotated token stays wrong on disk and
        // every future AppHost run re-reads it.
        RunGitCredentialCommand(
            "reject",
            $"protocol={url.Scheme}\nhost={url.Host}\nusername={credentials.Username}\npassword={credentials.Password}\n\n");
    }

    private static HelperCredentials? GitCredentialFill(string protocol, string host) =>
        RunGitCredentialCommand("fill", $"protocol={protocol}\nhost={host}\n\n") is { } output
            ? ParseCredentials(output)
            : null;

    /// <summary>
    /// Runs <c>git credential &lt;operation&gt;</c>, feeding it <paramref name="input"/> and
    /// returning its standard output, or <see langword="null"/> if it failed, timed out, or
    /// couldn't be launched at all.
    /// </summary>
    private static string? RunGitCredentialCommand(string operation, string input)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git", $"credential {operation}")
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

            process.StandardInput.Write(input);
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

            return process.ExitCode == 0 ? stdoutTask.Result : null;
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
