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
    /// <exception cref="PrepareLaunchException">
    /// The command could not be started at all, which is a different thing to tell a developer than
    /// a command that ran and failed.
    /// </exception>
    int Run(string workingDirectory, IReadOnlyList<string> command, Action<string> onLine);
}

/// <summary>
/// A <c>prepare</c> command that never started: a missing program, a script without the execute
/// bit, a POSIX script on Windows with no <c>windowsCommand</c> variant declared.
/// </summary>
internal sealed class PrepareLaunchException(string message, Exception? innerException = null)
    : Exception(message, innerException);
