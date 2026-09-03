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
    private static async Task<IReadOnlyList<string>> ReportAsync(
        IResource resource, params CustomResourceSnapshot[] snapshots)
    {
        var logger = new RecordingLogger();

        await new ServiceStartupFailureNotices()
            .ReportAsync(Publish(resource, snapshots), logger, CancellationToken.None);

        return logger.Written;
    }

    private static async IAsyncEnumerable<ResourceEvent> Publish(
        IResource resource, IEnumerable<CustomResourceSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            yield return new ResourceEvent(resource, resource.Name, snapshot);
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
    public async Task RuntimeUnhealthy_IsReported()
    {
        var written = await ReportAsync(ServiceResource(), Snapshot(KnownResourceStates.RuntimeUnhealthy));

        Assert.Contains(KnownResourceStates.RuntimeUnhealthy, Assert.Single(written));
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
