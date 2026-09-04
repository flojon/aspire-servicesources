using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// Whether Aspire drops or honours a consumer's <see cref="WaitAnnotation"/> on a backing service.
/// </summary>
/// <remarks>
/// Aspire drops a wait whose target is an <see cref="IResourceWithoutLifetime"/> and honours every
/// other one, leaving it to resolve when the target publishes a state. A <c>"direct"</c>-sourced
/// backing service is Aspire's <c>ConnectionStringResource</c>, which does not carry that marker, so
/// its wait is honoured — unlike <see cref="Sources.ServiceUrlResource"/>, which declares the marker
/// deliberately (#170) and is the contrast that gives these their meaning.
/// <para>
/// <b>These assert the marker and never a timing.</b> They run against a provider built from the
/// builder's services, with no orchestrator publishing states for anything, so an honoured wait
/// blocks here whatever its target is — an ordinary executable included. Blocking in this harness
/// therefore means the wait was not dropped, and nothing more; it is not evidence that anything
/// hangs.
/// </para>
/// <para>
/// What an honoured wait then waits for is the orchestrator's business and needs a live host to
/// answer. Measured there (#220): a <c>"direct"</c> backing service is satisfied at once, because
/// its connection string references nothing, while a wrapper forwarding another resource waits for
/// that resource to be running. Neither follows from the interfaces asserted below.
/// </para>
/// </remarks>
public class BackingServiceWaitTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), json);

        return TestHelpers.CreateBuilderThatCanStart(dir);
    }

    /// <summary>
    /// A <c>"direct"</c>-sourced backing service carries no lifetime marker, so its wait is honoured
    /// rather than dropped.
    /// </summary>
    /// <remarks>
    /// Pinned because the type's interfaces are the whole mechanism, and because
    /// <c>DirectBackingServiceSource</c> asserted the opposite in prose for a long time without
    /// anything checking.
    /// </remarks>
    [Fact]
    public void DirectSourcedBackingService_CarriesNoLifetimeMarker()
    {
        var builder = CreateBuilder("""
            { "backingServices": { "orders-db": {
                "source": "direct",
                "direct": { "connectionString": "Host=shared-dev;Database=orders" } } } }
            """);

        var db = builder.AddBackingService("orders-db", () => builder.AddConnectionString("orders-db"));

        Assert.IsNotAssignableFrom<IResourceWithoutLifetime>(db.Resource);
    }

    /// <summary>
    /// Nor does the forwarding wrapper the README offers when a factory's resource is not the
    /// caller's to rename, so it is the same shape as the one above.
    /// </summary>
    /// <remarks>
    /// The marker is all this pins: the wait is honoured rather than dropped, and nothing about what
    /// it then waits <i>for</i>. <c>ConnectionStringResource</c> also implements
    /// <see cref="IValueWithReferences"/>, and measured on a live host Aspire follows that reference
    /// — the wrapper stays in <c>Waiting</c> until the resource it forwards is running, so a
    /// consumer does hold back for the database. What it loses is the database's health check, since
    /// the wrapper is satisfied by running rather than healthy. Neither half is derivable from the
    /// interfaces below, which is why they are measured rather than reasoned about.
    /// </remarks>
    [Fact]
    public void ForwardingWrapper_CarriesNoLifetimeMarker()
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

        Assert.IsNotAssignableFrom<IResourceWithoutLifetime>(db.Resource);
    }

    /// <summary>
    /// A <c>"url"</c>-sourced service does carry the marker, and its wait resolves with nothing
    /// running at all.
    /// </summary>
    /// <remarks>
    /// The contrast, and the only wait here that demonstrates resolving rather than merely being
    /// honoured: Aspire drops the annotation outright, so this returns in a harness where an
    /// honoured wait blocks.
    /// </remarks>
    [Fact]
    public async Task UrlSourcedService_CarriesTheMarkerAndItsWaitResolves()
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
        var worker = builder
            .AddExecutable("worker", "dotnet", Directory.CreateTempSubdirectory().FullName)
            .WaitFor(inventory);

        Assert.IsAssignableFrom<IResourceWithoutLifetime>(inventory.Resource);

        var notifications = builder.Services.BuildServiceProvider()
            .GetRequiredService<ResourceNotificationService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await notifications.WaitForDependenciesAsync(worker.Resource, cts.Token);
    }
}
