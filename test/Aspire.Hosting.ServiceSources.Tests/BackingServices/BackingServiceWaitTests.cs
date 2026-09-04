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
/// <b>Honoured is not the same as never-resolving, and the difference cost a wrong claim.</b> These
/// run against a provider built from the builder's services, with no orchestrator publishing states
/// for anything, so an honoured wait blocks here whatever its target is — an ordinary executable
/// included. That was briefly read as "a direct backing service hangs". Measured against a live host
/// it does not: the orchestrator publishes <c>Running</c> for the connection-string resource
/// immediately and the consumer leaves <c>Waiting</c> in about a second. So the wait is honoured and
/// then satisfied at once, which makes it a no-op rather than a hang — see
/// <c>DirectBackingServiceSource</c>'s remarks for what that costs.
/// </para>
/// <para>
/// Which is why these assert the marker rather than timing anything. The marker is what this
/// package controls and what the wait behaviour follows from; how long a honoured wait then takes is
/// the orchestrator's business and needs the orchestrator to answer.
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
