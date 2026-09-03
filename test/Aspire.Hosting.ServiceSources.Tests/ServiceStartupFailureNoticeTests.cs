using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// A resource this package added that never starts (#150). The compiler's account of a checkout
/// that will not build goes to that resource's console in the dashboard and nowhere else, so
/// without a line of our own the AppHost's own console has nothing at all to say about a service
/// that silently failed to appear.
/// </summary>
public class ServiceStartupFailureNoticeTests
{
    private sealed class FakeServiceResource(string name) : Resource(name), IResourceWithServiceDiscovery;

    /// <summary>Collects what the report writes, without a host to write it through.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Written { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Written.Add(formatter(state, exception));
            }
        }
    }

    private static CustomResourceSnapshot Snapshot(string? state, int? exitCode = null) =>
        new()
        {
            ResourceType = "Project",
            Properties = [],
            State = state is null ? null : new ResourceStateSnapshot(state, null),
            ExitCode = exitCode,
        };

    /// <summary>
    /// Feeds the report a finite run of snapshots and hands back what it wrote — the states in the
    /// order DCP would have published them, with nothing left running afterwards, so the assertions
    /// are on a completed report rather than on a race with one.
    /// </summary>
    private static Task<IReadOnlyList<string>> ReportAsync(
        IResource resource, params CustomResourceSnapshot[] snapshots) =>
        ReportInstancesAsync(resource, [.. snapshots.Select(snapshot => (resource.Name, snapshot))]);

    /// <summary>
    /// The same, for snapshots that belong to named instances of one resource — the shape replicas
    /// arrive in, where the events interleave in a single stream and only the id tells them apart.
    /// </summary>
    private static Task<IReadOnlyList<string>> ReportInstancesAsync(
        IResource resource, params (string ResourceId, CustomResourceSnapshot Snapshot)[] events) =>
        ReportEventsAsync(
            dashboard: true,
            [.. events.Select(e => new ResourceEvent(resource, e.ResourceId, e.Snapshot))]);

    /// <summary>
    /// Feeds the report a finite run of ready-made events and hands back what it wrote, with
    /// nothing left running afterwards — so the assertions are on a completed report rather than on
    /// a race with one.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReportEventsAsync(
        bool dashboard, params ResourceEvent[] events)
    {
        var logger = new RecordingLogger();

        await new ServiceStartupFailureNotices()
            .ReportAsync(Publish(events), logger, CancellationToken.None, dashboard);

        return logger.Written;
    }

    private static async IAsyncEnumerable<ResourceEvent> Publish(IEnumerable<ResourceEvent> events)
    {
        foreach (var published in events)
        {
            yield return published;
            await Task.Yield();
        }
    }

    private static IResource ServiceResource(string name = "orders", string source = "local")
    {
        var resource = new FakeServiceResource(name);
        resource.Annotations.Add(new ServiceSourceAnnotation(name, source));
        return resource;
    }

    [Fact]
    public async Task FailedToStart_IsReported_NamingTheServiceAndWhereTheReasonIs()
    {
        var written = await ReportAsync(
            ServiceResource(),
            Snapshot(KnownResourceStates.Starting),
            Snapshot(KnownResourceStates.FailedToStart));

        var notice = Assert.Single(written);

        // The three things the notice is for: which service, that it is not running, and that this
        // console is not where the reason is.
        Assert.Contains("'orders'", notice);
        Assert.Contains("is not running", notice);
        Assert.Contains("dashboard", notice);

        // Named without claiming to know why — the state is what was reported, not a diagnosis.
        Assert.Contains(KnownResourceStates.FailedToStart, notice);
    }

    [Fact]
    public async Task NonZeroExit_IsReported_WithTheExitCodeItReported()
    {
        // What a checkout that will not compile looks like from outside: `dotnet run` starts, the
        // build fails, the process exits, and the resource never comes up.
        var written = await ReportAsync(
            ServiceResource(),
            Snapshot(KnownResourceStates.Running),
            Snapshot(KnownResourceStates.Exited, exitCode: 1));

        var notice = Assert.Single(written);

        Assert.Contains("'orders'", notice);
        Assert.Contains("exit code 1", notice);
    }

    [Fact]
    public async Task CleanExit_IsNotReported()
    {
        var written = await ReportAsync(
            ServiceResource(),
            Snapshot(KnownResourceStates.Running),
            Snapshot(KnownResourceStates.Finished, exitCode: 0));

        Assert.Empty(written);
    }

    [Fact]
    public async Task TerminalStateWithNoExitCode_IsNotInventedIntoAFailure()
    {
        // A terminal state whose exit code was never reported says nothing about whether it went
        // well. The prefetch's speculative reporting takes the same line: never invent a failure.
        var written = await ReportAsync(
            ServiceResource(),
            Snapshot(KnownResourceStates.Exited));

        Assert.Empty(written);
    }

    [Fact]
    public async Task RuntimeUnhealthy_IsNotReported_BecauseAspireRetriesOutOfIt()
    {
        // Measured against Aspire 13.5.2: an AppHost started before the container runtime is
        // reachable reports RuntimeUnhealthy for every container-backed service, and then goes on
        // to Starting and Running once the runtime answers. The state names the runtime, not this
        // service — and a line already written cannot be withdrawn once the service turns out fine.
        var written = await ReportAsync(
            ServiceResource(source: "container"),
            Snapshot(KnownResourceStates.Starting),
            Snapshot(KnownResourceStates.RuntimeUnhealthy),
            Snapshot(KnownResourceStates.Starting),
            Snapshot(KnownResourceStates.Running));

        Assert.Empty(written);
    }

    [Fact]
    public async Task AStopTheDeveloperAskedFor_IsNotReportedAsAFailureToStart()
    {
        // Stopping is what Aspire publishes before it takes a resource down on request — measured
        // for both a project and a container, each of which then reported exit code 0, so this
        // guards a case that does not misfire today rather than one that does. It is here because
        // the exit code of a stop is the runtime's to choose and a signal exit would read exactly
        // like a crash; the state that says "this was asked for" does not.
        var written = await ReportAsync(
            ServiceResource(),
            Snapshot(KnownResourceStates.Running),
            Snapshot(KnownResourceStates.Stopping),
            Snapshot(KnownResourceStates.Exited, exitCode: 137));

        Assert.Empty(written);
    }

    [Fact]
    public async Task AServiceStartedAgainAfterAStop_IsStillReportedWhenItThenFails()
    {
        // The stop is remembered only until the instance begins again, so it cannot swallow the
        // next failure.
        var written = await ReportAsync(
            ServiceResource(),
            Snapshot(KnownResourceStates.Running),
            Snapshot(KnownResourceStates.Stopping),
            Snapshot(KnownResourceStates.Exited, exitCode: 0),
            Snapshot(KnownResourceStates.Starting),
            Snapshot(KnownResourceStates.Exited, exitCode: 1));

        Assert.Single(written);
    }

    [Fact]
    public async Task AResourceThisPackageDidNotAdd_IsNotReported()
    {
        // A project the developer added themselves. They know the project and where its logs are,
        // and Aspire is not this package's to narrate.
        var written = await ReportAsync(
            new FakeServiceResource("frontend"),
            Snapshot(KnownResourceStates.FailedToStart));

        Assert.Empty(written);
    }

    [Fact]
    public async Task RepeatedFailureSnapshots_AreReportedOnce()
    {
        // Every later snapshot of a failed resource carries the state it failed in, and health and
        // URL updates keep arriving after it. One failure is one line.
        var written = await ReportAsync(
            ServiceResource(),
            Snapshot(KnownResourceStates.FailedToStart),
            Snapshot(KnownResourceStates.FailedToStart),
            Snapshot(KnownResourceStates.Exited, exitCode: 1));

        Assert.Single(written);
    }

    [Fact]
    public async Task AServiceThatIsRestartedAndFailsAgain_IsReportedAgain()
    {
        var written = await ReportAsync(
            ServiceResource(),
            Snapshot(KnownResourceStates.FailedToStart),
            Snapshot(KnownResourceStates.Starting),
            Snapshot(KnownResourceStates.FailedToStart));

        Assert.Equal(2, written.Count);
    }

    [Fact]
    public async Task ALocalService_IsToldItsBuildOutputIsNotHere()
    {
        var local = Assert.Single(await ReportAsync(
            ServiceResource(source: "local"), Snapshot(KnownResourceStates.FailedToStart)));

        var container = Assert.Single(await ReportAsync(
            ServiceResource(source: "container"), Snapshot(KnownResourceStates.FailedToStart)));

        // The reason issue #150 is filed against 'local' rather than against every source: the
        // developer never added this project and did not choose where its code lives, so the
        // notice says which console the build wrote to.
        Assert.Contains("checkout", local);
        Assert.DoesNotContain("checkout", container);

        Assert.Contains("'local'", local);
        Assert.Contains("'container'", container);
    }

    [Fact]
    public async Task OneFailingReplicaAmongHealthyOnes_IsReportedOnceAndDoesNotRepeat()
    {
        // Replicas share one IResource and are told apart only by their id, and their snapshots
        // interleave in the one stream this reads. Keyed by resource name, the healthy replica's
        // Running would clear the failure the other had just reported and the next snapshot from
        // the dead one would report it again — for as long as the host ran.
        var orders = ServiceResource();
        orders.Annotations.Add(new ReplicaAnnotation(2));

        var written = await ReportInstancesAsync(
            orders,
            ("orders-aaaa", Snapshot(KnownResourceStates.Running)),
            ("orders-bbbb", Snapshot(KnownResourceStates.Running)),
            ("orders-bbbb", Snapshot(KnownResourceStates.Finished, exitCode: 1)),
            ("orders-aaaa", Snapshot(KnownResourceStates.Running)),
            ("orders-bbbb", Snapshot(KnownResourceStates.Finished, exitCode: 1)),
            ("orders-aaaa", Snapshot(KnownResourceStates.Running)),
            ("orders-bbbb", Snapshot(KnownResourceStates.Finished, exitCode: 1)),
            ("orders-aaaa", Snapshot(KnownResourceStates.Running)));

        // Stable rather than growing with the interleaving, and it names which replica died.
        var notice = Assert.Single(written);
        Assert.Contains("orders-bbbb", notice);
        Assert.DoesNotContain("orders-aaaa", notice);
    }

    [Fact]
    public async Task EachFailingReplica_IsReportedWithItsOwnExitCode()
    {
        var orders = ServiceResource();
        orders.Annotations.Add(new ReplicaAnnotation(2));

        var written = await ReportInstancesAsync(
            orders,
            ("orders-aaaa", Snapshot(KnownResourceStates.Finished, exitCode: 1)),
            ("orders-bbbb", Snapshot(KnownResourceStates.Finished, exitCode: 3)));

        // One line per failing replica rather than one per service: two replicas can die of
        // different things, and a single line would have to pick one of them to be about.
        Assert.Equal(2, written.Count);
        Assert.Contains(written, line => line.Contains("orders-aaaa", StringComparison.Ordinal)
            && line.Contains("exit code 1", StringComparison.Ordinal));
        Assert.Contains(written, line => line.Contains("orders-bbbb", StringComparison.Ordinal)
            && line.Contains("exit code 3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreplicatedService_DoesNotNameItsInstanceId()
    {
        // DCP suffixes every instance id whether the service is replicated or not, so naming it
        // unconditionally would put "orders-yjebxsrp" into the ordinary one-replica line.
        var written = await ReportInstancesAsync(
            ServiceResource(),
            ("orders-yjebxsrp", Snapshot(KnownResourceStates.FailedToStart)));

        var notice = Assert.Single(written);

        Assert.Contains("'orders'", notice);
        Assert.DoesNotContain("orders-yjebxsrp", notice);
    }

    /// <summary>
    /// A resource whose annotations cannot be read at all. Stands in for anything that can throw
    /// while one event is being considered, which must cost that event and nothing else.
    /// </summary>
    private sealed class ExplodingResource : IResource
    {
        public string Name => "exploding";

        public ResourceAnnotationCollection Annotations =>
            throw new InvalidOperationException("Collection was modified.");
    }

    [Fact]
    public async Task AnEventThatThrows_CostsThatEventAndNoLaterOne()
    {
        // The loop is the only thing watching, so ending it on one bad event would put every later
        // service back into the silence this class exists to break.
        var orders = ServiceResource();

        var written = await ReportEventsAsync(
            dashboard: true,
            new ResourceEvent(new ExplodingResource(), "exploding-aaaa", Snapshot(KnownResourceStates.Running)),
            new ResourceEvent(orders, "orders-aaaa", Snapshot(KnownResourceStates.FailedToStart)),
            new ResourceEvent(new ExplodingResource(), "exploding-aaaa", Snapshot(KnownResourceStates.Running)));

        Assert.Contains("'orders'", Assert.Single(written));
    }

    [Fact]
    public async Task WithoutADashboard_TheNoticePointsAtTheResourcesOwnLogsInstead()
    {
        // A DistributedApplicationTestingBuilder host has no dashboard by default, and neither does
        // an AppHost that turned it off. Naming one would send the reader to the only place that
        // isn't there.
        var written = await ReportEventsAsync(
            dashboard: false,
            new ResourceEvent(ServiceResource(), "orders-aaaa", Snapshot(KnownResourceStates.FailedToStart)));

        var notice = Assert.Single(written);

        Assert.DoesNotContain("dashboard URL", notice);
        Assert.Contains("no dashboard", notice);
        Assert.Contains("own logs", notice);
    }

    [Fact]
    public async Task ARestartThatFailsImmediatelyAfterAStop_IsStillReported()
    {
        // The failing half of the stop guard: an instance that reports FailedToStart with no
        // Starting snapshot in between must not inherit the suppression from the stop before it.
        // FailedToStart cannot be the echo of a stop — a stop ends in Exited or Finished — so it
        // clears the guard rather than being swallowed by it.
        var written = await ReportAsync(
            ServiceResource(),
            Snapshot(KnownResourceStates.Running),
            Snapshot(KnownResourceStates.Stopping),
            Snapshot(KnownResourceStates.Finished, exitCode: 0),
            Snapshot(KnownResourceStates.FailedToStart));

        Assert.Single(written);
    }

    [Fact]
    public async Task AStoppedResourceRepublishingHowItEnded_IsStillNotReported()
    {
        // The guard has to survive repeats of the state the stop produced — a stopped resource goes
        // on republishing it — while not surviving into anything else.
        var written = await ReportAsync(
            ServiceResource(),
            Snapshot(KnownResourceStates.Running),
            Snapshot(KnownResourceStates.Stopping),
            Snapshot(KnownResourceStates.Exited, exitCode: 137),
            Snapshot(KnownResourceStates.Exited, exitCode: 137),
            Snapshot(KnownResourceStates.Exited, exitCode: 137));

        Assert.Empty(written);
    }

    [Fact]
    public async Task ARestartThatDiesWithADifferentExitCodeThanTheStop_IsReported()
    {
        var written = await ReportAsync(
            ServiceResource(),
            Snapshot(KnownResourceStates.Stopping),
            Snapshot(KnownResourceStates.Exited, exitCode: 137),
            Snapshot(KnownResourceStates.Exited, exitCode: 1));

        Assert.Contains("exit code 1", Assert.Single(written));
    }

    [Fact]
    public async Task AResourceNamedDifferentlyFromItsService_NamesBoth()
    {
        var resource = new FakeServiceResource("orders-app");
        resource.Annotations.Add(new ServiceSourceAnnotation("orders", "local"));

        var notice = Assert.Single(await ReportAsync(resource, Snapshot(KnownResourceStates.FailedToStart)));

        Assert.Contains("'orders'", notice);
        Assert.Contains("'orders-app'", notice);
    }

    [Fact]
    public async Task AFailureThatArrivesWhileTheReportIsRunning_ReachesTheAppHostsOwnConsole()
    {
        // End to end through the same seam a real run uses: the report is subscribed by the source
        // that tagged the resource, started from BeforeStartEvent, and reads the notification
        // service DCP publishes into.
        var builder = TestHelpers.CreateBuilderThatCanStart(Directory.CreateTempSubdirectory().FullName);

        var notices = TestHelpers.StreamServiceSourcesWarnings(builder);

        var orders = builder.AddResource(new FakeServiceResource("orders"));
        ResolvedService.Tag(orders, "orders", "local");

        var services = builder.Services.BuildServiceProvider();
        await builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));

        // Stands in for DCP, which is what publishes this for a project whose process died before
        // the service came up.
        await services.GetRequiredService<ResourceNotificationService>()
            .PublishUpdateAsync(orders.Resource, snapshot => snapshot with
            {
                State = new ResourceStateSnapshot(
                    KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error),
            });

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var notice = await notices.ReadAsync(timeout.Token);

        Assert.Contains("'orders'", notice);
        Assert.Contains("dashboard", notice);
    }

    [Fact]
    public async Task PublishMode_ReportsNothing()
    {
        // Publish mode composes the model, writes the manifest and exits without starting a
        // resource, so there is no start to fail and no console watching for one.
        var builder = TestHelpers.CreatePublishingBuilder(Directory.CreateTempSubdirectory().FullName);

        var orders = builder.AddResource(new FakeServiceResource("orders"));
        ResolvedService.Tag(orders, "orders", "local");

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Empty(warnings);
        Assert.Null(ServiceStartupFailureNotices.For(builder).ReportTask);
    }
}
