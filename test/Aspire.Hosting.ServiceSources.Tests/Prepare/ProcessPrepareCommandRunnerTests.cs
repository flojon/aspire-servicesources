using System.Diagnostics;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Tests.Prepare;

/// <summary>
/// The one place a test in this repository launches a real prepare command. Everything else
/// substitutes <see cref="IPrepareCommandRunner"/>, which is what keeps the suite free of processes
/// — but what this class asserts cannot be asserted against a substitute: that cancelling the step
/// ends the command, and its children, rather than merely ending the wait for it.
/// </summary>
public class ProcessPrepareCommandRunnerTests
{
    /// <summary>
    /// Writes an executable shell script. The mode call is guarded rather than left to the callers'
    /// own POSIX-only guards, because the platform analyzer reads this method on its own.
    /// </summary>
    private static void WriteScript(string path, string body)
    {
        File.WriteAllText(path, body);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    /// <summary>
    /// A script that backgrounds a long-lived child, records its pid, and waits — so the test can
    /// ask whether the whole tree died or only the shell at the top of it.
    /// </summary>
    private static void WriteSpawningScript(string directory) =>
        WriteScript(
            Path.Combine(directory, "spawn.sh"),
            """
            #!/bin/sh
            sleep 120 &
            echo $! > child.pid
            echo spawned
            wait
            """);

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // No process with that id, which is what a reaped child looks like.
            return false;
        }
    }

    private static async Task<int> WaitForChildPidAsync(string directory)
    {
        var pidFile = Path.Combine(directory, "child.pid");

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();

            if (File.Exists(pidFile)
                && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)
                && pid > 0)
            {
                return pid;
            }

            await Task.Delay(50, deadline.Token);
        }
    }

    /// <remarks>
    /// The case the design cares about is a committed script that runs <c>curl</c> and then a JVM:
    /// killing the shell alone would leave the download and the import running with no AppHost left
    /// to belong to. POSIX only — the script is a shell script, and what is under test is the kill
    /// rather than the platform.
    /// </remarks>
    [Fact]
    public async Task Cancellation_KillsTheCommandAndItsChildren()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory().FullName;
        WriteSpawningScript(directory);

        using var cancellation = new CancellationTokenSource();

        var started = Stopwatch.StartNew();
        var run = Task.Run(() => Assert.ThrowsAny<OperationCanceledException>(
            () => ProcessPrepareCommandRunner.Instance.Run(
                directory, ["./spawn.sh"], cancellation.Token, _ => { })));

        var child = await WaitForChildPidAsync(directory);
        Assert.True(IsAlive(child), "the backgrounded child should be running before cancellation");

        await cancellation.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(30));

        // Promptly, rather than after the 120 seconds the child was going to sleep for.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"took {started.Elapsed}");

        // And the grandchild is gone, which is the whole claim: the tree, not just the shell.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (IsAlive(child))
        {
            deadline.Token.ThrowIfCancellationRequested();
            await Task.Delay(50, deadline.Token);
        }
    }

    /// <summary>
    /// A command that leaves a helper holding the output pipe still returns.
    /// </summary>
    /// <remarks>
    /// A redirected stream ends when the last handle to its write end closes, not when the process
    /// that was handed it exits — so a script that starts a helper without redirecting the helper's
    /// output leaves this pipe open behind it. The parameterless <see cref="Process.WaitForExit()"/>
    /// waits for that, unconditionally: measured before the fix, the runner was still blocked eight
    /// seconds after the script had exited, and on the eager path that is composition hanging with
    /// no timeout and nothing to cancel it. The drain is bounded instead, so what a held pipe costs
    /// is the tail of a command that has already finished.
    /// </remarks>
    [Fact]
    public async Task ACommandThatLeavesAHelperHoldingThePipe_StillReturns()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory().FullName;
        WriteScript(
            Path.Combine(directory, "daemonize.sh"),
            """
            #!/bin/sh
            sleep 120 &
            echo parent-exiting
            exit 3
            """);

        var started = Stopwatch.StartNew();
        var exitCode = await Task.Run(() => ProcessPrepareCommandRunner.Instance.Run(
            directory, ["./daemonize.sh"], CancellationToken.None, _ => { }))
            .WaitAsync(TimeSpan.FromSeconds(60));

        // Bounded by the drain rather than by the helper, which is still sleeping.
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"took {started.Elapsed}");

        // And the command's own answer survives the bounded wait.
        Assert.Equal(3, exitCode);
    }

    /// <summary>
    /// A command that reads stdin sees the end of it rather than waiting for a human.
    /// </summary>
    /// <remarks>
    /// Left unredirected, the command inherits the AppHost's own stdin and a prompting bootstrap
    /// waits forever — with no timeout, and under <c>aspire run</c> no visible prompt either, since
    /// the CLI does not print the AppHost's output. <c>GitCommand</c> closes stdin for exactly this
    /// reason. The script here blocks on <c>read</c>, so without the close this test hangs rather
    /// than fails.
    /// </remarks>
    [Fact]
    public async Task ACommandThatReadsStdin_IsNotLeftWaitingForAHuman()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory().FullName;
        WriteScript(
            Path.Combine(directory, "prompt.sh"),
            """
            #!/bin/sh
            echo "continue? "
            read answer
            echo "got '$answer'"
            """);

        var lines = new List<string>();
        await Task.Run(() => ProcessPrepareCommandRunner.Instance.Run(
            directory,
            ["./prompt.sh"],
            CancellationToken.None,
            line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }
            }))
            .WaitAsync(TimeSpan.FromSeconds(30));

        // It ran past the `read` rather than sitting on it, and read nothing — which is what the
        // end of a closed stdin looks like to a shell. The exit code is the trailing echo's and says
        // nothing about any of this.
        Assert.Contains("got ''", lines);
    }

    [Fact]
    public async Task ACommandThatExits_ReportsItsOutputAndExitCode()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateTempSubdirectory().FullName;
        WriteScript(
            Path.Combine(directory, "both-streams.sh"),
            """
            #!/bin/sh
            echo on-stdout
            echo on-stderr >&2
            exit 7
            """);

        var lines = new List<string>();
        var exitCode = await Task.Run(() => ProcessPrepareCommandRunner.Instance.Run(
            directory,
            ["./both-streams.sh"],
            CancellationToken.None,
            line =>
            {
                lock (lines)
                {
                    lines.Add(line);
                }
            }));

        Assert.Equal(7, exitCode);

        // Both streams, and every line flushed before Run returned — which is what the second,
        // parameterless wait is for.
        Assert.Contains("on-stdout", lines);
        Assert.Contains("on-stderr", lines);
    }

    /// <remarks>
    /// A program that does not exist is a different thing to tell a developer than a command that
    /// ran and failed, so it arrives as its own exception type rather than as an exit code.
    /// </remarks>
    [Fact]
    public void AProgramThatDoesNotExist_IsALaunchFailure()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;

        var ex = Assert.Throws<PrepareLaunchException>(() => ProcessPrepareCommandRunner.Instance.Run(
            directory, ["./not-there.sh"], CancellationToken.None, _ => { }));

        Assert.Contains("not-there.sh", ex.Message);
    }
}
