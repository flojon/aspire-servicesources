using System.ComponentModel;
using System.Diagnostics;

namespace Aspire.Hosting.ServiceSources.Prepare;

/// <summary>
/// Runs a <c>prepare</c> command as a child process. There is no shell between the tool and the
/// command, so there are no quoting or word-splitting rules to document or get wrong.
/// </summary>
internal sealed class ProcessPrepareCommandRunner : IPrepareCommandRunner
{
    public static readonly ProcessPrepareCommandRunner Instance = new();

    private ProcessPrepareCommandRunner()
    {
    }

    public int Run(
        string workingDirectory,
        IReadOnlyList<string> command,
        CancellationToken cancellationToken,
        Action<string> onLine)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveProgram(workingDirectory, command[0]),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in command.Skip(1))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Start(startInfo, command[0]);

        // Both streams, line by line as they arrive: the developer is watching a step that can take
        // minutes, and a command's progress is as likely to be on stderr as on stdout. Read through
        // the events rather than by blocking on one stream at a time, which deadlocks as soon as the
        // other one fills its pipe buffer.
        process.OutputDataReceived += (_, e) => Forward(e.Data, onLine);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, onLine);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Wait(process, cancellationToken);

        return process.ExitCode;
    }

    /// <summary>
    /// Waits for the command, and kills it if the wait is cancelled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole process <em>tree</em>, because what a bootstrap does is start other programs: the
    /// motivating case is a shell script that runs <c>curl</c> and then a JVM, and killing the shell
    /// alone would leave the download and the import running with nothing left to stop them.
    /// </para>
    /// <para>
    /// The parameterless <see cref="Process.WaitForExit()"/> follows the cancellable wait, and is
    /// not redundant: it is what waits for the redirected output readers to finish, so every line
    /// the command wrote has reached <c>onLine</c> before this returns. On the cancelled path it
    /// runs after the kill, for the same reason — the tail of what the command managed to say is
    /// still worth having.
    /// </para>
    /// </remarks>
    private static void Wait(Process process, CancellationToken cancellationToken)
    {
        try
        {
            // Blocking on the async wait rather than polling: this method is synchronous by design —
            // both callers already block for as long as the step takes — and there is no
            // synchronization context here to deadlock against.
            process.WaitForExitAsync(cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            process.WaitForExit();

            throw;
        }

        process.WaitForExit();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // It exited between the cancellation and the kill, or this platform cannot enumerate the
            // tree. Either way there is nothing left for this to do, and a shutdown must not fail
            // over the way it was noticed.
        }
    }

    private static void Forward(string? line, Action<string> onLine)
    {
        // The stream's end arrives as a null line.
        if (line is not null)
        {
            onLine(line);
        }
    }

    private static Process Start(ProcessStartInfo startInfo, string writtenProgram)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new PrepareLaunchException($"starting '{writtenProgram}' returned no process.");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or PlatformNotSupportedException)
        {
            throw new PrepareLaunchException($"'{writtenProgram}' could not be started: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// The program to launch: a path resolved against the checkout, or a bare name left for
    /// <c>PATH</c>.
    /// </summary>
    /// <remarks>
    /// Resolved explicitly rather than left to <see cref="ProcessStartInfo.WorkingDirectory"/>,
    /// because a relative <see cref="ProcessStartInfo.FileName"/> is resolved against the
    /// <em>process's</em> working directory rather than the one in the start info — so
    /// <c>./prepare.sh</c> would be looked for beside the AppHost and reported as missing.
    /// <para>
    /// The shape of the value is what decides, and <see cref="PrepareStep"/> has already confined
    /// anything that looks like a path to the checkout, so this only has to join it.
    /// </para>
    /// </remarks>
    private static string ResolveProgram(string workingDirectory, string program) =>
        program.StartsWith('.') || program.Contains(Path.DirectorySeparatorChar) || program.Contains('/')
            ? Path.GetFullPath(program, workingDirectory)
            : program;
}
