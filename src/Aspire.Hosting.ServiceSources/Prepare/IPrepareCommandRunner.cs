namespace Aspire.Hosting.ServiceSources.Prepare;

/// <summary>
/// Launches a <c>prepare</c> command. The seam a test substitutes, so no test in this repository
/// spawns a process — the same shape <see cref="Git.IGitClient"/> gives cloning.
/// </summary>
internal interface IPrepareCommandRunner
{
    /// <summary>
    /// Runs <paramref name="command"/> in <paramref name="workingDirectory"/>, handing each line of
    /// its output to <paramref name="onLine"/> as it arrives, and returns its exit code.
    /// </summary>
    /// <param name="command">
    /// The argv. Its first element is a program name to resolve through <c>PATH</c>, or a path
    /// relative to <paramref name="workingDirectory"/> — already confined to it by
    /// <see cref="PrepareStep"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the command. An implementation has to end the <em>process</em> and not merely the wait
    /// for it: a bootstrap can legitimately run for an hour, so interrupting is how a long one
    /// ordinarily ends, and a runner that only stopped waiting would leave a download or a
    /// country-sized import running with no host left to belong to.
    /// </param>
    /// <param name="onLine">
    /// Called for each line, and called concurrently: a process has two streams and they are read
    /// independently, so an implementation is free to invoke this from more than one thread and the
    /// caller guards accordingly.
    /// </param>
    /// <exception cref="PrepareLaunchException">
    /// The command could not be started at all, which is a different thing to tell a developer than
    /// a command that ran and failed.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> fired and the command was stopped.
    /// </exception>
    int Run(
        string workingDirectory,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken,
        Action<string> onLine);
}

/// <summary>
/// A <c>prepare</c> command that never started: a missing program, a script without the execute
/// bit, a POSIX script on Windows with no <c>windowsCommand</c> variant declared.
/// </summary>
internal sealed class PrepareLaunchException(string message, Exception? innerException = null)
    : Exception(message, innerException);
