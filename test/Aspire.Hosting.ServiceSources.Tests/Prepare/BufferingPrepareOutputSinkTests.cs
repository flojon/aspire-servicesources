using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Prepare;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests.Prepare;

/// <summary>
/// The eager path's buffering sink: forwards to the sink it wraps immediately, and separately caps
/// and replays the same lines into the matching service's own resource log at BeforeStartEvent.
/// </summary>
public class BufferingPrepareOutputSinkTests
{
    private sealed class RecordingSink : IPrepareOutputSink
    {
        public List<string> Lines { get; } = [];

        public void Report(string line) => Lines.Add(line);
    }

    /// <summary>
    /// A resource carrying the same <see cref="ServiceSourceAnnotation"/> every source-resolved
    /// resource does, without needing a real checkout or kind behind it.
    /// </summary>
    private static IResourceBuilder<ContainerResource> AddTaggedResource(
        IDistributedApplicationBuilder builder, string serviceName) =>
        builder.AddContainer(serviceName, "test-image")
            .WithAnnotation(new ServiceSourceAnnotation(serviceName, "local"));

    private static Task PublishBeforeStartEventAsync(
        IDistributedApplicationBuilder builder, IServiceProvider services) =>
        builder.Eventing.PublishAsync(
            new BeforeStartEvent(services, new DistributedApplicationModel(builder.Resources)));

    /// <summary>
    /// The <paramref name="expectedCount"/> lines <paramref name="resource"/>'s log ends up
    /// carrying, read off the live stream rather than <c>GetAllAsync</c> — which requires the
    /// console-logs backend a real DCP host provides, and throws without one.
    /// </summary>
    private static async Task<List<string>> ReadLogsAsync(
        IServiceProvider services, IResource resource, int expectedCount)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lines = new List<string>();

        await foreach (var batch in services.GetRequiredService<ResourceLoggerService>()
            .WatchAsync(resource).WithCancellation(cts.Token))
        {
            lines.AddRange(batch.Select(log => StripTimestamp(log.Content)));

            if (lines.Count >= expectedCount)
            {
                break;
            }
        }

        return lines;
    }

    /// <summary>
    /// <c>ResourceLoggerService</c> prefixes every line it renders with its own UTC timestamp; the
    /// sink under test never sees or controls that, so the tests compare past it.
    /// </summary>
    private static string StripTimestamp(string content)
    {
        var spaceIndex = content.IndexOf(' ');
        return spaceIndex < 0 ? content : content[(spaceIndex + 1)..];
    }

    [Fact]
    public void Report_ForwardsEveryLineToTheInnerSinkImmediately()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);
        var inner = new RecordingSink();

        var sink = BufferingPrepareOutputSink.Wrap(builder, "orders", inner);

        sink.Report("first");
        sink.Report("second");

        // Immediate and unconditional: nothing here waits for BeforeStartEvent, which is the whole
        // point — dotnet run's live terminal output must be exactly what it was before this wrapped
        // it.
        Assert.Equal(["first", "second"], inner.Lines);
    }

    [Fact]
    public async Task Flush_WritesBufferedLinesToTheMatchingResourcesLog()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);
        var orders = AddTaggedResource(builder, "orders");

        var sink = BufferingPrepareOutputSink.Wrap(builder, "orders", new RecordingSink());
        sink.Report("[prepare orders] reason. Running: ./prepare.sh");
        sink.Report("[prepare orders] line 1");

        var services = builder.Services.BuildServiceProvider();
        await PublishBeforeStartEventAsync(builder, services);

        var logged = await ReadLogsAsync(services, orders.Resource, expectedCount: 2);

        Assert.Equal(
            ["[prepare orders] reason. Running: ./prepare.sh", "[prepare orders] line 1"], logged);
    }

    [Fact]
    public async Task Flush_OmitsTheElisionMarker_WhenNothingWasDropped()
    {
        // Exactly the head-plus-tail budget (40 lines): every line survives, so there is nothing to
        // mark as elided — a complete record must never read as a truncated one.
        var builder = TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);
        var orders = AddTaggedResource(builder, "orders");

        var sink = BufferingPrepareOutputSink.Wrap(builder, "orders", new RecordingSink());
        var written = Enumerable.Range(0, 40).Select(i => $"line {i}").ToArray();
        foreach (var line in written)
        {
            sink.Report(line);
        }

        var services = builder.Services.BuildServiceProvider();
        await PublishBeforeStartEventAsync(builder, services);

        var logged = await ReadLogsAsync(services, orders.Resource, expectedCount: 40);

        Assert.Equal(written, logged);
    }

    [Fact]
    public async Task Flush_InsertsAnUnambiguousElisionMarker_WhenLinesWereDropped()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);
        var orders = AddTaggedResource(builder, "orders");

        var sink = BufferingPrepareOutputSink.Wrap(builder, "orders", new RecordingSink());
        const int total = 61; // head(20) + 21 elided + tail(20)
        for (var i = 0; i < total; i++)
        {
            sink.Report($"line {i}");
        }

        var services = builder.Services.BuildServiceProvider();
        await PublishBeforeStartEventAsync(builder, services);

        var logged = await ReadLogsAsync(services, orders.Resource, expectedCount: 41);

        Assert.Equal(41, logged.Count); // 20 head + 1 marker + 20 tail
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"line {i}"), logged.Take(20));
        Assert.Equal(Enumerable.Range(total - 20, 20).Select(i => $"line {i}"), logged.Skip(21));

        var marker = logged[20];
        Assert.Contains("21", marker);
        Assert.Contains("elided", marker);
    }

    [Fact]
    public async Task Flush_UsesSingularWording_WhenExactlyOneLineWasElided()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);
        var orders = AddTaggedResource(builder, "orders");

        var sink = BufferingPrepareOutputSink.Wrap(builder, "orders", new RecordingSink());
        const int total = 41; // head(20) + 1 elided + tail(20)
        for (var i = 0; i < total; i++)
        {
            sink.Report($"line {i}");
        }

        var services = builder.Services.BuildServiceProvider();
        await PublishBeforeStartEventAsync(builder, services);

        var logged = await ReadLogsAsync(services, orders.Resource, expectedCount: 41);

        var marker = logged[20];
        Assert.Contains("1 line elided", marker);
        Assert.DoesNotContain("1 lines elided", marker);
    }

    [Fact]
    public async Task Flush_WritesNothing_ForAServiceWrappedButNeverReported()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);
        var orders = AddTaggedResource(builder, "orders");
        var routing = AddTaggedResource(builder, "routing");

        // "routing" is wrapped — e.g. CheckoutPreparation.Run decided the step should not run — but
        // Report is never called on it, so its buffer stays empty.
        BufferingPrepareOutputSink.Wrap(builder, "routing", new RecordingSink());
        BufferingPrepareOutputSink.Wrap(builder, "orders", new RecordingSink()).Report("orders line");

        var services = builder.Services.BuildServiceProvider();
        await PublishBeforeStartEventAsync(builder, services);

        Assert.Equal(["orders line"], await ReadLogsAsync(services, orders.Resource, expectedCount: 1));

        // Nothing was ever written for "routing". There is no line to wait for, so this bounds the
        // wait itself rather than asking ReadLogsAsync to wait for a count that would never arrive.
        var routingLines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await foreach (var batch in services.GetRequiredService<ResourceLoggerService>()
                .WatchAsync(routing.Resource).WithCancellation(cts.Token))
            {
                routingLines.AddRange(batch.Select(log => log.Content));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the watch never yields anything for "routing", so it can only end this way.
        }

        Assert.Empty(routingLines);
    }

    [Fact]
    public async Task Flush_KeepsEachServicesBufferSeparate()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);
        var orders = AddTaggedResource(builder, "orders");
        var routing = AddTaggedResource(builder, "routing");

        BufferingPrepareOutputSink.Wrap(builder, "orders", new RecordingSink()).Report("orders line");
        BufferingPrepareOutputSink.Wrap(builder, "routing", new RecordingSink()).Report("routing line");

        var services = builder.Services.BuildServiceProvider();
        await PublishBeforeStartEventAsync(builder, services);

        Assert.Equal(["orders line"], await ReadLogsAsync(services, orders.Resource, expectedCount: 1));
        Assert.Equal(["routing line"], await ReadLogsAsync(services, routing.Resource, expectedCount: 1));
    }

    [Fact]
    public async Task Flush_DoesNotThrow_WhenABufferedServiceHasNoMatchingResource()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);
        var orders = AddTaggedResource(builder, "orders");

        // "phantom" has output buffered but was never added as a resource — e.g. a step that ran
        // and reported before its kind handler got as far as creating one.
        BufferingPrepareOutputSink.Wrap(builder, "phantom", new RecordingSink()).Report("orphaned");
        BufferingPrepareOutputSink.Wrap(builder, "orders", new RecordingSink()).Report("orders line");

        var services = builder.Services.BuildServiceProvider();
        await PublishBeforeStartEventAsync(builder, services);

        Assert.Equal(["orders line"], await ReadLogsAsync(services, orders.Resource, expectedCount: 1));
    }
}
