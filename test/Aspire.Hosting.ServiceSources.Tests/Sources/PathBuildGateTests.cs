using System.ComponentModel;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

[Trait("Concurrency", "true")]
[Trait("Timing", "true")]
public class PathBuildGateTests
{
    private static readonly TimeSpan Rendezvous = TimeSpan.FromSeconds(10);

    private const string Key = "p:/repo";

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private sealed class FakeRunner(Func<string, CancellationToken, Task<int>>? run = null) : IBuildRunner
    {
        private int _active;
        private int _max;
        private int _calls;

        public int MaxConcurrency => Volatile.Read(ref _max);

        public int Calls => Volatile.Read(ref _calls);

        public List<(string Project, string? Configuration)> Requests { get; } = [];

        public async Task<int> RunAsync(
            string projectFile, string? configuration, Action<string> onLine, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);

            lock (Requests)
            {
                Requests.Add((projectFile, configuration));
            }

            var now = Interlocked.Increment(ref _active);
            int seen;

            while ((seen = Volatile.Read(ref _max)) < now && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
            }

            try
            {
                onLine($"building {projectFile}");
                return run is null ? await DelayAsync(cancellationToken) : await run(projectFile, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private static async Task<int> DelayAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(100, cancellationToken);
            return 0;
        }
    }

    private static PathBuildGate GateWith(params (string Resource, string Key)[] members)
    {
        var gate = new PathBuildGate();

        foreach (var (resource, key) in members)
        {
            gate.Register(resource, key);
        }

        return gate;
    }

    private static Task Build(
        PathBuildGate gate, IBuildRunner runner, string resource, string key,
        RecordingLogger? logger = null, CancellationToken ct = default) =>
        gate.RunGatedBuildAsync(runner, resource, key, $"{resource}.csproj", null, logger ?? new RecordingLogger(), ct);

    [Fact]
    public async Task SameKeyMembers_BuildsNeverOverlap()
    {
        var gate = GateWith(("a", Key), ("b", Key));
        var runner = new FakeRunner();

        await Task.WhenAll(Build(gate, runner, "a", Key), Build(gate, runner, "b", Key)).WaitAsync(Rendezvous);

        Assert.Equal(2, runner.Calls);
        Assert.Equal(1, runner.MaxConcurrency);
    }

    [Fact]
    public async Task DifferentKeys_BuildConcurrently()
    {
        var gate = GateWith(("a", "p:/one"), ("a2", "p:/one"), ("b", "p:/two"), ("b2", "p:/two"));
        var bothIn = new TaskCompletionSource();
        var inside = 0;
        var runner = new FakeRunner(async (_, ct) =>
        {
            if (Interlocked.Increment(ref inside) == 2)
            {
                bothIn.TrySetResult();
            }

            await bothIn.Task.WaitAsync(Rendezvous, ct);
            return 0;
        });

        await Task.WhenAll(Build(gate, runner, "a", "p:/one"), Build(gate, runner, "b", "p:/two")).WaitAsync(Rendezvous);

        Assert.Equal(2, runner.MaxConcurrency);
    }

    [Fact]
    public async Task LoneMember_DoesNotBuildOrLock()
    {
        var gate = GateWith(("a", Key));
        var runner = new FakeRunner();

        await Build(gate, runner, "a", Key);

        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task Membership_IsEvaluatedAtStartTime()
    {
        var gate = GateWith(("a", Key));
        var runner = new FakeRunner();

        gate.Register("b", Key);
        await Build(gate, runner, "a", Key);

        Assert.Equal(1, runner.Calls);
    }

    [Fact]
    public async Task Register_IsIdempotentPerResourceName()
    {
        var gate = GateWith(("a", Key), ("a", Key));
        var runner = new FakeRunner();

        await Build(gate, runner, "a", Key);

        Assert.Equal(0, runner.Calls);
        Assert.Equal(1, gate.MemberCount(Key));
    }

    [Fact]
    public async Task NonZeroExit_DoesNotThrow_ReleasesTheGate_AndLogsToTheResource()
    {
        var gate = GateWith(("a", Key), ("b", Key));
        var runner = new FakeRunner((project, _) => Task.FromResult(project.StartsWith("a", StringComparison.Ordinal) ? 1 : 0));
        var logger = new RecordingLogger();

        await Build(gate, runner, "a", Key, logger).WaitAsync(Rendezvous);
        await Build(gate, runner, "b", Key, logger).WaitAsync(Rendezvous);

        Assert.Equal(2, runner.Calls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunnerThrows_DoesNotThrow_AndReleasesTheGate(bool win32)
    {
        var gate = GateWith(("a", Key), ("b", Key));
        var throwing = true;
        var runner = new FakeRunner((_, _) =>
            throwing
                ? throw (win32 ? new Win32Exception("no dotnet") : new InvalidOperationException("no dotnet"))
                : Task.FromResult(0));
        var logger = new RecordingLogger();

        await Build(gate, runner, "a", Key, logger).WaitAsync(Rendezvous);
        throwing = false;
        await Build(gate, runner, "b", Key, logger).WaitAsync(Rendezvous);

        Assert.Equal(2, runner.Calls);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task CancelledWhileWaiting_Propagates_AndDoesNotReleaseWhatItDidNotAcquire()
    {
        var gate = GateWith(("a", Key), ("b", Key), ("c", Key));
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var runner = new FakeRunner(async (project, ct) =>
        {
            if (project.StartsWith("a", StringComparison.Ordinal))
            {
                entered.SetResult();
                await release.Task.WaitAsync(Rendezvous, ct);
            }

            return 0;
        });

        var holder = Build(gate, runner, "a", Key);
        await entered.Task.WaitAsync(Rendezvous);

        using var cts = new CancellationTokenSource();
        var waiting = Build(gate, runner, "b", Key, ct: cts.Token);
        await Task.Delay(100);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        var third = Build(gate, runner, "c", Key);
        await Task.Delay(200);
        Assert.False(third.IsCompleted, "the cancelled waiter released a hold it never had");

        release.SetResult();
        await Task.WhenAll(holder, third).WaitAsync(Rendezvous);
        Assert.Equal(1, runner.MaxConcurrency);
    }

    [Fact]
    public async Task CancelledDuringTheBuild_PassesTheCancelToTheRunner_ReleasesAndRethrows()
    {
        var gate = GateWith(("a", Key), ("b", Key));
        var entered = new TaskCompletionSource();
        var runner = new FakeRunner(async (project, ct) =>
        {
            if (project.StartsWith("a", StringComparison.Ordinal))
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return 0;
        });

        using var cts = new CancellationTokenSource();
        var first = Build(gate, runner, "a", Key, ct: cts.Token);
        await entered.Task.WaitAsync(Rendezvous);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        await Build(gate, runner, "b", Key).WaitAsync(Rendezvous);
        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task TheGateIsReleasedBeforeTheHandlerReturns()
    {
        var gate = GateWith(("a", Key), ("b", Key));
        var runner = new FakeRunner();

        await Build(gate, runner, "a", Key);

        // A held semaphore would make this second build block until the timeout.
        await Build(gate, runner, "b", Key).WaitAsync(Rendezvous);

        Assert.Equal(2, runner.Calls);
    }

    [Fact]
    public async Task ARegistrationArrivingAfterALoneMemberPassedTheCheck_DoesNotThrow()
    {
        var gate = GateWith(("a", Key));
        var runner = new FakeRunner();

        var lone = Build(gate, runner, "a", Key);
        gate.Register("b", Key);

        await lone;
        await Build(gate, runner, "b", Key);

        Assert.True(runner.Calls >= 1);
    }

    [Fact]
    public async Task BuildOutput_IsForwardedToTheLogger()
    {
        var gate = GateWith(("a", Key), ("b", Key));
        var logger = new RecordingLogger();

        await Build(gate, new FakeRunner(), "a", Key, logger);

        Assert.Contains(logger.Entries, e => e.Message.Contains("building a.csproj", StringComparison.Ordinal));
    }
}