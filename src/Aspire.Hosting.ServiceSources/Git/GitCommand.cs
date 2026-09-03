using System.Diagnostics;
using System.Text;

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
    /// <param name="progress">
    /// Where to report git's progress lines as they arrive, or <see langword="null"/> to read stderr
    /// as one block at the end. The caller that passes one is also responsible for asking git for
    /// progress with <c>--progress</c>; this only decides how stderr is read.
    /// </param>
    /// <exception cref="GitUnavailableException">
    /// <c>git</c> could not be launched at all.
    /// </exception>
    public static GitCommandResult Run(
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null,
        IGitProgressSink? progress = null)
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
        var stderrTask = progress is null
            ? ReadStandardErrorAsync(process.StandardError)
            : ReadStandardErrorAsync(process.StandardError, progress);

        // git reads prompts from the terminal rather than stdin, but close it anyway so nothing
        // downstream of it can wait on input that will never come.
        process.StandardInput.Close();

        Task.WaitAll(stdoutTask, stderrTask);
        process.WaitForExit();

        return new GitCommandResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>
    /// All of stderr, redacted. Nobody is watching, so there is nothing to gain from reading it in
    /// pieces.
    /// </summary>
    private static async Task<string> ReadStandardErrorAsync(StreamReader standardError) =>
        GitUrl.RedactAll(await standardError.ReadToEndAsync().ConfigureAwait(false));

    /// <summary>
    /// Reads stderr as it arrives, reporting each line to <paramref name="progress"/> and returning
    /// what remains for a failure message.
    /// </summary>
    /// <remarks>
    /// One stream carries both, which is why one reader does both jobs: git writes its progress to
    /// stderr and its errors there too, so a failing clone's diagnosis and its progress cannot be
    /// separated by reading a different pipe.
    /// <para>
    /// What it returns is stderr <em>minus</em> the progress. A clone interrupted mid-transfer has
    /// written a line per percent by then, and a failure message made of git's whole stderr would
    /// bury "fatal: early EOF" under a hundred superseded percentages. Only lines
    /// <see cref="GitProgressLine.TryParse"/> recognises are dropped, so everything that is actually
    /// a diagnostic — including the phase lines with no percentage, which carry object counts —
    /// still reaches the developer.
    /// </para>
    /// </remarks>
    private static async Task<string> ReadStandardErrorAsync(
        StreamReader standardError, IGitProgressSink progress)
    {
        var splitter = new ProgressLineSplitter();
        var retained = new StringBuilder();
        var buffer = new char[4096];

        int read;
        while ((read = await standardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
        {
            foreach (var line in splitter.Append(new string(buffer, 0, read)))
            {
                Deliver(line, progress, retained);
            }
        }

        if (splitter.Flush() is { } last)
        {
            Deliver(last, progress, retained);
        }

        return retained.ToString();
    }

    private static void Deliver(string line, IGitProgressSink progress, StringBuilder retained)
    {
        // Redacted per line rather than once at the end: these go straight to a sink that puts them
        // in the resource's logs, so a token in the repository URL would travel there ahead of
        // anything that could remove it.
        var redacted = GitUrl.RedactAll(line);

        progress.Report(redacted);

        if (!GitProgressLine.TryParse(redacted, out _))
        {
            retained.AppendLine(redacted);
        }
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
