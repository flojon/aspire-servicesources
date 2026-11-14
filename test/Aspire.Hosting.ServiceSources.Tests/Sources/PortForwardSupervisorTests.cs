using System.Threading.Channels;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// What happens to a <c>"kubernetes"</c> service's <c>kubectl port-forward</c> once it has started
/// (#490). Driven through <see cref="IPortForwardControl"/> and hand-fed snapshots: no kubectl, no
/// cluster and no DCP are involved, so these pin the decisions and not the Aspire wiring.
/// </summary>
/// <remarks>
/// The supervisor runs on its own task while the test feeds it snapshots and advances a fake clock
/// (<c>Concurrency</c>), and waits for what it has done with a bounded <c>WaitAsync</c>
/// (<c>Timing</c>). The clock itself is fake and would not count on its own.
/// </remarks>
[Trait("Concurrency", "true")]
[Trait("Timing", "true")]
public class PortForwardSupervisorTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private sealed class FakeResource() : Resource("orders"), IResourceWithServiceDiscovery;

    private sealed class FakeControl : IPortForwardControl
    {
        private readonly Channel<int> _starts = Channel.CreateUnbounded<int>();

        public List<string?> Failed { get; } = [];

        public int Starts { get; private set; }

        /// <summary>What a start request answers; <see langword="null"/> accepts it.</summary>
        public string? StartAnswer { get; set; }

        public string? LastOutput { get; set; }

        public Task<string?> StartAsync(CancellationToken cancellationToken)
        {
            Starts++;
            _starts.Writer.TryWrite(Starts);
            return Task.FromResult(StartAnswer);
        }

        public Task FailToStartAsync(string? kubectlError, CancellationToken cancellationToken)
        {
            Failed.Add(kubectlError);
            return Task.CompletedTask;
        }

        public async Task NextStartAsync() => await _starts.Reader.ReadAsync().AsTask().WaitAsync(Patience);
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly Channel<(LogLevel Level, string Text)> _lines = Channel.CreateUnbounded<(LogLevel, string)>();

        public List<(LogLevel Level, string Text)> Written { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = (logLevel, formatter(state, exception));
            lock (Written)
            {
                Written.Add(line);
            }

            _lines.Writer.TryWrite(line);
        }

        public async Task<(LogLevel Level, string Text)> NextAsync() =>
            await _lines.Reader.ReadAsync().AsTask().WaitAsync(Patience);
    }

    private sealed class Harness
    {
        private readonly Channel<ResourceEvent> _events = Channel.CreateUnbounded<ResourceEvent>();
        private readonly FakeResource _resource = new();
        private readonly Task _running;

        public Harness(TimeSpan? settle = null)
        {
            _running = new PortForwardSupervisor("orders", Logger, Control, Time, settle ?? TimeSpan.Zero)
                .SuperviseAsync(_events.Reader.ReadAllAsync(), CancellationToken.None);
        }

        public FakeControl Control { get; } = new();

        public RecordingLogger Logger { get; } = new();

        public ManualTimeProvider Time { get; } = new();

        public void Publish(string? state, int? exitCode = null, HealthStatus? health = null)
        {
            var snapshot = new CustomResourceSnapshot
            {
                ResourceType = "Executable",
                Properties = [],
                State = state is null ? null : new ResourceStateSnapshot(state, null),
                ExitCode = exitCode,
            };

            if (health is { } status)
            {
                snapshot = snapshot.WithHealthReports([new HealthReportSnapshot("tcp", status, null, null)]);
            }

            _events.Writer.TryWrite(new ResourceEvent(_resource, "orders-abc", snapshot));
        }

        /// <summary>Starting, Running, healthy: the tunnel came up.</summary>
        public void BringUp()
        {
            Publish(KnownResourceStates.Starting);
            Publish(KnownResourceStates.Running);
            Publish(KnownResourceStates.Running, health: HealthStatus.Healthy);
        }

        /// <summary>Ends the stream and waits for the supervisor to finish what it was doing.</summary>
        public async Task FinishAsync()
        {
            _events.Writer.Complete();
            await _running.WaitAsync(Patience);
            await _running;
        }
    }

    [Fact]
    public async Task Backoff_DoublesFromOneSecondAndStopsAtThirty()
    {
        Assert.Equal(
            [1, 2, 4, 8, 16, 30, 30],
            Enumerable.Range(1, 7).Select(attempt => (int)PortForwardSupervisor.Backoff(attempt).TotalSeconds));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ExitBeforeTheTunnelWasUp_IsAFailedStart_CarryingKubectlsLastLine_AndIsNotRetried()
    {
        var harness = new Harness();
        harness.Control.LastOutput = "Error from server (NotFound): services \"no-such-service-dev\" not found";

        harness.Publish(KnownResourceStates.Starting);
        harness.Publish(KnownResourceStates.Running);
        harness.Publish(KnownResourceStates.Finished, exitCode: 1);

        var (level, text) = await harness.Logger.NextAsync();
        await harness.FinishAsync();

        Assert.Equal(LogLevel.Error, level);
        Assert.Contains("'orders'", text);
        Assert.Contains("exit code 1", text);
        Assert.Contains("FailedToStart", text);
        Assert.Contains("no-such-service-dev", text);
        Assert.Equal(["Error from server (NotFound): services \"no-such-service-dev\" not found"], harness.Control.Failed);
        Assert.Equal(0, harness.Control.Starts);
    }

    [Fact]
    public async Task ExitAfterTheTunnelWasUp_IsRestarted_OnlyOnceTheBackoffHasPassed()
    {
        var harness = new Harness();
        harness.BringUp();
        harness.Publish(KnownResourceStates.Finished, exitCode: 1);

        var (level, text) = await harness.Logger.NextAsync();

        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("after it had been up", text);
        Assert.Contains("in 1s (attempt 1 of 8)", text);

        harness.Time.Advance(TimeSpan.FromMilliseconds(900));
        Assert.Equal(0, harness.Control.Starts);

        harness.Time.Advance(TimeSpan.FromMilliseconds(200));
        await harness.Control.NextStartAsync();
        await harness.FinishAsync();

        Assert.Equal(1, harness.Control.Starts);
    }

    [Fact]
    public async Task ARestartThatDiesBeforeComingUp_WaitsLonger_AndCountsAgainstTheSameLimit()
    {
        var harness = new Harness();
        harness.BringUp();
        harness.Publish(KnownResourceStates.Finished, exitCode: 1);
        await harness.Logger.NextAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Control.NextStartAsync();

        // The new process starts and dies without ever being healthy.
        harness.Publish(KnownResourceStates.Starting);
        harness.Publish(KnownResourceStates.Running);
        harness.Publish(KnownResourceStates.Finished, exitCode: 1);

        var (_, text) = await harness.Logger.NextAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(2));
        await harness.Control.NextStartAsync();
        await harness.FinishAsync();

        Assert.Contains("again before it was up", text);
        Assert.Contains("in 2s (attempt 2 of 8)", text);
        Assert.Empty(harness.Control.Failed);
    }

    [Fact]
    public async Task ATunnelThatCameBackUp_GetsTheFullAllowanceAgainForTheNextOutage()
    {
        var harness = new Harness();
        harness.BringUp();
        harness.Publish(KnownResourceStates.Finished, exitCode: 1);
        await harness.Logger.NextAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Control.NextStartAsync();

        harness.BringUp();
        harness.Publish(KnownResourceStates.Finished, exitCode: 1);

        var (_, text) = await harness.Logger.NextAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Control.NextStartAsync();
        await harness.FinishAsync();

        Assert.Contains("in 1s (attempt 1 of 8)", text);
    }

    [Fact]
    public async Task AnEndedTunnel_RepublishedWhileEnded_IsRestartedOnce()
    {
        var harness = new Harness();
        harness.BringUp();
        harness.Publish(KnownResourceStates.Finished, exitCode: 1);
        harness.Publish(KnownResourceStates.Finished, exitCode: 1);
        harness.Publish(KnownResourceStates.Finished, exitCode: 1);

        await harness.Logger.NextAsync();
        harness.Time.Advance(TimeSpan.FromSeconds(1));
        await harness.Control.NextStartAsync();
        await harness.FinishAsync();

        Assert.Equal(1, harness.Control.Starts);
        Assert.Single(harness.Logger.Written);
    }

    [Fact]
    public async Task AStopTheDeveloperAskedFor_IsNeitherRestartedNorReported()
    {
        var harness = new Harness();
        harness.BringUp();
        harness.Publish(KnownResourceStates.Stopping);
        harness.Publish(KnownResourceStates.Exited, exitCode: 143);
        await harness.FinishAsync();

        Assert.Empty(harness.Logger.Written);
        Assert.Equal(0, harness.Control.Starts);
        Assert.Empty(harness.Control.Failed);
    }

    [Fact]
    public async Task AFailedStartAspireReportedItself_IsNotRestarted()
    {
        // kubectl not on PATH: the process never launched, and no restart would change that.
        var harness = new Harness();
        harness.Publish(KnownResourceStates.Starting);
        harness.Publish(KnownResourceStates.FailedToStart);
        harness.Publish(KnownResourceStates.Exited, exitCode: 1);
        await harness.FinishAsync();

        Assert.Empty(harness.Logger.Written);
        Assert.Equal(0, harness.Control.Starts);
        Assert.Empty(harness.Control.Failed);
    }

    [Fact]
    public async Task RestartsThatAreRefused_StopAtTheLimit_AndSayHowToGetTheTunnelBack()
    {
        var harness = new Harness();
        harness.Control.StartAnswer = "the resource is not in a state that can be started";
        harness.BringUp();
        harness.Publish(KnownResourceStates.Finished, exitCode: 1);

        // Each attempt logs its warning, then the refusal; the last one is the give-up.
        (LogLevel Level, string Text) last = default;
        for (var attempt = 1; attempt <= PortForwardSupervisor.MaxRestarts; attempt++)
        {
            await harness.Logger.NextAsync();
            harness.Time.Advance(PortForwardSupervisor.MaxBackoff);
            await harness.Control.NextStartAsync();
            last = await harness.Logger.NextAsync();
            Assert.Contains("refused", last.Text);
        }

        last = await harness.Logger.NextAsync();
        await harness.FinishAsync();

        Assert.Equal(LogLevel.Error, last.Level);
        Assert.Contains($"{PortForwardSupervisor.MaxRestarts} restarts in a row", last.Text);
        Assert.Contains("dashboard", last.Text);
        Assert.Equal(PortForwardSupervisor.MaxRestarts, harness.Control.Starts);
    }

    [Fact]
    public async Task TunnelOutput_PrefersTheErrorStream_AndIgnoresBlankLines()
    {
        var output = new PortForwardSupervisor.TunnelOutput();

        await output.ConsumeAsync(
            Batches(
                [new LogLine(1, "Forwarding from 127.0.0.1:5000 -> 80", false), new LogLine(2, "  ", true)],
                [new LogLine(3, "error: lost connection to pod", true), new LogLine(4, "Handling connection for 5000", false)]),
            CancellationToken.None);

        Assert.Equal("error: lost connection to pod", output.Last);
    }

    [Fact]
    public async Task TunnelOutput_WithNoErrorStream_FallsBackToTheLastLine()
    {
        var output = new PortForwardSupervisor.TunnelOutput();

        await output.ConsumeAsync(
            Batches([new LogLine(1, "Forwarding from 127.0.0.1:5000 -> 80", false)]), CancellationToken.None);

        Assert.Equal("Forwarding from 127.0.0.1:5000 -> 80", output.Last);
    }

    private static async IAsyncEnumerable<IReadOnlyList<LogLine>> Batches(params LogLine[][] batches)
    {
        foreach (var batch in batches)
        {
            yield return batch;
            await Task.Yield();
        }
    }
}
