using System.Diagnostics;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Runs <c>dotnet build</c> on a project as a child process: an argument list with no shell, its
/// output capped so a noisy build cannot flood a resource log.
/// </summary>
internal sealed class ProcessBuildRunner(
    Func<string, string?, ProcessStartInfo>? startInfoFactory = null, int headLines = 200, int tailLines = 50)
    : IBuildRunner
{
    private static readonly TimeSpan StreamDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<string, string?, ProcessStartInfo> _startInfo = startInfoFactory ?? CreateStartInfo;

    public static ProcessStartInfo CreateStartInfo(string projectFile, string? configuration)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(projectFile)) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        startInfo.ArgumentList.Add("build");
        startInfo.ArgumentList.Add(projectFile);

        if (!string.IsNullOrEmpty(configuration))
        {
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add(configuration);
        }

        return startInfo;
    }

    public async Task<int> RunAsync(
        string projectFile, string? configuration, Action<string> onLine, CancellationToken cancellationToken)
    {
        var startInfo = _startInfo(projectFile, configuration);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrEnded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new CappedOutput(onLine, headLines, tailLines);

        process.Exited += (_, _) => exited.TrySetResult();
        process.OutputDataReceived += (_, e) => Forward(e.Data, output, stdoutEnded);
        process.ErrorDataReceived += (_, e) => Forward(e.Data, output, stderrEnded);

        process.Start();

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // A build that asks a question must see the end of input, not wait for a human.
            process.StandardInput.Close();

            await exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Kill(process);

            // The gate frees the next build on return; the killed tree must be done with obj/ first.
            await WaitBrieflyForExitAsync(process).ConfigureAwait(false);
            throw;
        }

        // Bounded: a helper the build left running can hold the pipes open long after the build ended.
        await Task.WhenAny(Task.WhenAll(stdoutEnded.Task, stderrEnded.Task), Task.Delay(StreamDrainTimeout, CancellationToken.None))
            .ConfigureAwait(false);

        output.Complete();

        return process.ExitCode;
    }

    private static async Task WaitBrieflyForExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(StreamDrainTimeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            // Never started or still dying; nothing more to wait for.
        }
    }

    private static void Forward(string? data, CappedOutput output, TaskCompletionSource ended)
    {
        if (data is null)
        {
            ended.TrySetResult();
            return;
        }

        try
        {
            output.Add(data);
        }
        catch (Exception)
        {
            // A throwing log sink on a pipe callback would take the AppHost down.
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already exited.
        }
    }

    /// <summary>Streams the first lines live and keeps only the last few of the rest, like the prepare output cap.</summary>
    private sealed class CappedOutput(Action<string> onLine, int head, int tail)
    {
        private readonly object _gate = new();
        private readonly Queue<string> _tail = new();
        private int _total;

        public void Add(string line)
        {
            lock (_gate)
            {
                if (_total++ < head)
                {
                    onLine(line);
                    return;
                }

                _tail.Enqueue(line);

                if (_tail.Count > tail)
                {
                    _tail.Dequeue();
                }
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                var elided = _total - head - _tail.Count;

                if (elided > 0)
                {
                    onLine(elided == 1 ? "... (1 line elided) ..." : $"... ({elided} lines elided) ...");
                }

                while (_tail.Count > 0)
                {
                    onLine(_tail.Dequeue());
                }
            }
        }
    }
}