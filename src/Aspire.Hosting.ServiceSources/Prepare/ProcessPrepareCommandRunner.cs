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

        // The end of each stream, which is a different event from the process exiting — see Drain.
        var stdoutEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Both streams, line by line as they arrive: the developer is watching a step that can take
        // minutes, and a command's progress is as likely to be on stderr as on stdout. Read through
        // the events rather than by blocking on one stream at a time, which deadlocks as soon as the
        // other one fills its pipe buffer.
        process.OutputDataReceived += (_, e) => Forward(e.Data, onLine, stdoutEnded);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, onLine, stderrEnded);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        Wait(process, cancellationToken);
        Drain(stdoutEnded.Task, stderrEnded.Task);

        return process.ExitCode;
    }

    /// <summary>
    /// How long to keep waiting for the command's output after the command itself has exited.
    /// </summary>
    /// <remarks>
    /// Bounded, and the bound is the whole point. A redirected stream ends when the last handle to
    /// its write end closes — not when the process that was handed it exits — so a script that
    /// starts a helper without redirecting the helper's output leaves this pipe held open by
    /// something that may outlive the AppHost. Waiting for that unconditionally, which
    /// <see cref="Process.WaitForExit()"/> does, hangs the caller, and on the eager path the caller
    /// is composition: no timeout, nothing to cancel it. Measured rather than reasoned about — a
    /// script whose only sin was <c>sleep 20 &amp;</c> held the runner for the whole twenty seconds.
    /// <para>
    /// Long enough to be irrelevant in the ordinary case, where the pipes close with the process and
    /// this returns at once. What it costs in the pathological one is the tail of a command that has
    /// already exited, which is the right way round.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Waits for what the command wrote to have been forwarded, for as long as that is worth
    /// waiting for.
    /// </summary>
    private static void Drain(params Task[] streamsEnded) =>
        // The result is deliberately ignored: a timeout means a stream is still held open, which
        // nothing reports, because nothing is wrong with the step — it ran and it exited. Disposing
        // the process on the way out detaches the readers.
        Task.WhenAll(streamsEnded).Wait(StreamDrainTimeout);

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
    /// Waits for the process and for nothing else, which is the whole reason this polls rather than
    /// awaiting. <b>Both</b> of the obvious waits also wait for the redirected streams to end — the
    /// parameterless <see cref="Process.WaitForExit()"/> by documentation, and
    /// <see cref="Process.WaitForExitAsync"/> because it is that method's async equivalent down to
    /// the drain. Stream end is not process exit (see <see cref="StreamDrainTimeout"/>), so neither
    /// is bounded by anything the command controls: measured, a script whose only sin was
    /// <c>sleep 20 &amp;</c> held both. The integer overload is the one that waits for the process
    /// alone, so it is the one used, and cancellation is checked between polls.
    /// </para>
    /// <para>
    /// Draining is a separate, bounded step, and it is skipped entirely on the cancelled path:
    /// nothing reads the output of a step the developer interrupted, and waiting for it is what
    /// there was to avoid.
    /// </para>
    /// </remarks>
    private static void Wait(Process process, CancellationToken cancellationToken)
    {
        while (!process.WaitForExit(ExitPollMilliseconds))
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                continue;
            }

            Kill(process);

            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    /// <summary>
    /// How often the wait above looks up to see whether it has been cancelled.
    /// </summary>
    /// <remarks>
    /// The cost of not being able to await the one thing worth awaiting. Short enough that Ctrl-C
    /// during a four-minute import feels immediate, and long enough that a step running for an hour
    /// costs a few thousand handle waits and nothing else.
    /// </remarks>
    private const int ExitPollMilliseconds = 100;

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

    private static void Forward(string? line, Action<string> onLine, TaskCompletionSource ended)
    {
        // The stream's end arrives as a null line.
        if (line is null)
        {
            ended.TrySetResult();
            return;
        }

        onLine(line);
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
