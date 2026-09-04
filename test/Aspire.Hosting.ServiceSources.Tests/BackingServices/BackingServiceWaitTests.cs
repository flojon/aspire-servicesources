using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// What a consumer's <c>WaitFor</c> on a backing service actually does, measured rather than
/// reasoned about: it hangs, and these pin that until it is fixed.
/// </summary>
/// <remarks>
/// Driven through <see cref="ResourceNotificationService.WaitForDependenciesAsync"/>, the method
/// Aspire's own orchestrator calls to honour a <see cref="WaitAnnotation"/> — the same route
/// <c>UrlConsumerWaitTests</c> takes, and the reason neither needs DCP to launch anything.
/// <para>
/// Worth measuring because the obvious reasoning was wrong. <c>DirectBackingServiceSource</c>'s
/// remarks claimed Aspire's <c>ConnectionStringResource</c> is an <see cref="IResourceWithoutLifetime"/>,
/// which is the marker that makes a wait resolve by being dropped (#170). It is not: on Aspire 13.5.2
/// the type declares <see cref="IResourceWithConnectionString"/> and
/// <see cref="IResourceWithWaitSupport"/> and no lifetime marker at all. With wait support present
/// and the drop-me marker absent, the wait is accepted rather than short-circuited, and the
/// plausible failure was a hang — the same one #170 was filed for.
/// </para>
/// </remarks>
public class BackingServiceWaitTests
{
    /// <summary>
    /// How long a wait is given before it counts as hanging.
    /// </summary>
    /// <remarks>
    /// Short, because these tests pin hangs and every second of it is paid on every CI run. A wait
    /// that resolves returns without waiting at all — the control below does — so this is orders of
    /// magnitude above the passing case rather than a fine margin. It was measured at 30s first, and
    /// all three hangs used the whole budget, which is what a hang looks like against a wait that
    /// resolves immediately.
    /// </remarks>
    private static readonly TimeSpan ResolvesWithin = TimeSpan.FromSeconds(5);

    private static IDistributedApplicationBuilder CreateBuilder(string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), json);

        return TestHelpers.CreateBuilderThatCanStart(dir);
    }

    private static IResourceBuilder<ExecutableResource> Consumer(IDistributedApplicationBuilder builder) =>
        builder.AddExecutable("worker", "dotnet", Directory.CreateTempSubdirectory().FullName);

    /// <remarks>
    /// <c>async</c> and awaited here rather than returning the task: a non-async version disposes
    /// the <see cref="CancellationTokenSource"/> — and with it the timer that would fire the
    /// cancellation — the moment it returns the task, so a hang runs forever instead of failing at
    /// the timeout. Which is how it was written first, and is why the first run of this file had to
    /// be killed rather than reporting anything.
    /// </remarks>
    private static async Task WaitAsync(IDistributedApplicationBuilder builder, IResource consumer)
    {
        var notifications = builder.Services.BuildServiceProvider()
            .GetRequiredService<ResourceNotificationService>();

        using var cts = new CancellationTokenSource(ResolvesWithin);

        await notifications.WaitForDependenciesAsync(consumer, cts.Token);
    }

    /// <summary>
    /// The consumer never leaves <c>Waiting</c>, and the message names the backing service whose
    /// state cannot be retrieved.
    /// </summary>
    /// <remarks>
    /// Asserted rather than merely observed, so the bug cannot be fixed or worsened unnoticed while
    /// it is open. Nothing ever publishes a state for the resource: it is Aspire's
    /// <c>ConnectionStringResource</c>, which has no process behind it, and the developer is pointing
    /// at something they already run.
    /// </remarks>
    [Fact]
    public async Task DirectSourcedBackingService_WaitedOnByAConsumer_HangsToday()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Host=shared-dev;Database=orders" } } } }
            """);

        var db = builder.AddBackingService("orders-db", () => builder.AddConnectionString("orders-db"));
        var worker = Consumer(builder).WaitFor(db);

        var ex = await Assert.ThrowsAsync<OperationCanceledException>(() => WaitAsync(builder, worker.Resource));

        Assert.Contains("failed to wait for dependencies", ex.Message);
        Assert.Contains("orders-db", ex.Message);
    }

    /// <summary>
    /// The same, for <c>WaitForCompletion</c> — the wait a resource with nothing to run can least
    /// honour.
    /// </summary>
    [Fact]
    public async Task DirectSourcedBackingService_WaitedOnForCompletion_HangsToday()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Host=shared-dev;Database=orders" } } } }
            """);

        var db = builder.AddBackingService("orders-db", () => builder.AddConnectionString("orders-db"));
        var worker = Consumer(builder).WaitForCompletion(db);

        await Assert.ThrowsAsync<OperationCanceledException>(() => WaitAsync(builder, worker.Resource));
    }

    /// <summary>
    /// The forwarding wrapper the README recommends when the factory's resource is not the caller's
    /// to rename hangs too, which is the worst of the three.
    /// </summary>
    /// <remarks>
    /// The shape that matters most, because this package tells people to write it — and unlike the
    /// two above there <i>is</i> a real resource behind the wrapper under <c>"local"</c>, so a
    /// developer has every reason to expect the wait to reach through to it. It does not: the wait
    /// lands on the wrapper, which never publishes a state.
    /// </remarks>
    [Fact]
    public async Task ForwardingWrapper_WaitedOnByAConsumer_HangsToday()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": { "source": "local" } } }
            """);

        var db = builder.AddBackingService("orders-db", () =>
        {
            var shared = builder.AddConnectionString(
                "some-helpers-own-name", ReferenceExpression.Create($"Host=localhost;Database=orders"));

            return builder.AddConnectionString("orders-db", ReferenceExpression.Create($"{shared}"));
        });

        var worker = Consumer(builder).WaitFor(db);

        await Assert.ThrowsAsync<OperationCanceledException>(() => WaitAsync(builder, worker.Resource));
    }

    /// <summary>
    /// The control: <c>ServiceUrlResource</c> declares the lifetime marker, so its wait is the shape
    /// that is supposed to resolve. If this hangs, the harness is wrong rather than the subject.
    /// </summary>
    [Fact]
    public async Task Control_UrlSourcedService_ResolvesRatherThanHanging()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              inventory:
                url:
                  url: https://orders.example.com
            """);
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" } } }""");

        var builder = TestHelpers.CreateBuilderThatCanStart(dir);

        var inventory = builder.AddService("inventory");
        var worker = Consumer(builder).WaitFor(inventory);

        await WaitAsync(builder, worker.Resource);
    }
}
