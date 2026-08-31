using System.Diagnostics;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// What one <c>git</c> invocation produced.
/// </summary>
/// <param name="ExitCode">The process exit code; 0 on success.</param>
/// <param name="StandardOutput">Everything git wrote to stdout.</param>
/// <param name="StandardError">
/// Everything git wrote to stderr, with any URL userinfo already removed — see
/// <see cref="GitUrl.RedactAll"/>. A failing remote operation names the URL it was working on, so
/// without that a token embedded in a <c>repository</c> URL would travel from the catalog into the
/// exception message and every log sink the AppHost is wired to.
/// </param>
internal readonly record struct GitCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// Standard output as a single trimmed line, which is the shape of every plumbing command this
    /// package reads (<c>rev-parse</c>, <c>remote get-url</c>).
    /// </summary>
    public string FirstLine => StandardOutput.AsSpan().Trim().ToString();
}

/// <summary>
/// Runs the <c>git</c> executable. Every git operation this package performs goes through here, so
/// the environment git runs under is decided in exactly one place.
/// </summary>
/// <remarks>
/// Holds no mutable state of its own: <see cref="Sources.LocalCheckoutPrefetch"/> starts every
/// "local" service's checkout at once, so several of these run concurrently.
/// </remarks>
internal static class GitCommand
{
    /// <summary>
    /// Runs <c>git</c> with <paramref name="arguments"/> and waits for it to exit.
    /// </summary>
    /// <param name="arguments">
    /// Arguments passed through <see cref="ProcessStartInfo.ArgumentList"/>, so each one reaches git
    /// exactly as written — no shell, and nothing to escape.
    /// </param>
    /// <param name="environmentOverrides">
    /// Variables to set on top of the ones below, or to remove from git's environment when the
    /// value is <see langword="null"/>. This is the whole of what decides which credentials git
    /// can reach, so a test can hand it an isolated environment instead of the developer's own.
    /// </param>
    /// <exception cref="GitUnavailableException">
    /// <c>git</c> could not be launched at all.
    /// </exception>
    public static GitCommandResult Run(
        IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // Never block an AppHost's startup on a human. Without this a private repository whose
        // credentials don't resolve stops at an interactive username prompt instead of failing,
        // and `builder.AddService()` hangs. Credential *helpers* are unaffected — this disables
        // only prompting at the terminal.
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

        // Only --porcelain output is ever parsed, but error messages are matched against for the
        // authentication detection below, so pin git's messages to one language.
        startInfo.Environment["LC_ALL"] = "C";
        startInfo.Environment["LANG"] = "C";

        // The same "never wait for a human" rule for SSH remotes: BatchMode turns a passphrase or
        // unknown-host-key prompt into an immediate failure. An ssh-agent, a key with no
        // passphrase, and a keychain-backed key all still work. Only set when the developer hasn't
        // chosen their own command, since theirs may already carry the options they need.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GIT_SSH_COMMAND")))
        {
            startInfo.Environment["GIT_SSH_COMMAND"] = "ssh -o BatchMode=yes";
        }

        if (environmentOverrides is not null)
        {
            foreach (var (name, value) in environmentOverrides)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        using var process = Start(startInfo);

        // Read both pipes concurrently. A clone writes progress to stderr while the pack arrives on
        // stdout, so draining them in sequence would fill one pipe's buffer and deadlock.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        // git reads prompts from the terminal rather than stdin, but close it anyway so nothing
        // downstream of it can wait on input that will never come.
        process.StandardInput.Close();

        Task.WaitAll(stdoutTask, stderrTask);
        process.WaitForExit();

        return new GitCommandResult(process.ExitCode, stdoutTask.Result, GitUrl.RedactAll(stderrTask.Result));
    }

    private static Process Start(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new GitUnavailableException("Starting 'git' returned no process.", innerException: null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException
                                      or InvalidOperationException or ObjectDisposedException)
        {
            throw new GitUnavailableException($"'git' could not be started: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Thrown when the <c>git</c> executable itself could not be launched, as opposed to a git command
/// that ran and failed. Kept apart so <see cref="GitCliClient.EnsureAvailable"/> can turn "git is
/// not installed" into a message about the developer's machine rather than about their catalog.
/// </summary>
internal sealed class GitUnavailableException(string message, Exception? innerException)
    : Exception(message, innerException);

/// <summary>
/// Thrown when a <c>git</c> command ran and exited non-zero. Carries git's own stderr as the
/// message so the wrapping <see cref="ServiceSourcesConfigurationException"/> can show what git
/// actually said underneath this package's own wording.
/// </summary>
internal sealed class GitCommandFailedException(string message) : Exception(message);
