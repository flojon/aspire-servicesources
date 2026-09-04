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

    public int Run(string workingDirectory, IReadOnlyList<string> command, Action<string> onLine)
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

        process.WaitForExit();

        return process.ExitCode;
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
