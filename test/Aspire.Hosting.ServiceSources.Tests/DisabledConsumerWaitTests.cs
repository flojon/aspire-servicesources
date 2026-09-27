using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The "disabled" counterpart of <see cref="UrlConsumerWaitTests"/>: a consumer's <c>WaitFor</c>
/// against a service another developer has switched to <c>"disabled"</c> shares "url"'s hazard
/// (#170) — nothing ever publishes a state for either facade, since neither registers a real
/// resource — so <see cref="Sources.UnregisteredServiceStartupGuard"/> drops the wait for both
/// rather than letting it hang. These tests exercise that generalization through the real
/// <c>BeforeStartEvent</c> pipeline rather than through <see cref="ServiceConfigurationExtensionsTests"/>'s
/// unit-level checks.
/// </summary>
[Trait("IO", "true")]
[Trait("Timing", "true")]
public class DisabledConsumerWaitTests
{
    private static string AppHostDirectory()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              inventory:
                url:
                  url: https://orders.example.com
            """);
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "disabled" } } }""");
        return dir;
    }

    private static IResourceBuilder<ExecutableResource> Consumer(
        IDistributedApplicationBuilder builder, string name) =>
        builder.AddExecutable(name, "dotnet", TempDirectories.CreateSubdirectory().FullName);

    private static readonly TimeSpan ResolvesWithin = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task DisabledSourcedService_WaitedOnByAConsumer_ResolvesRatherThanHanging()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory());

        var inventory = builder.AddService("inventory");
        var worker = Consumer(builder, "worker").WaitFor(inventory);

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        var notifications = builder.Services.BuildServiceProvider()
            .GetRequiredService<ResourceNotificationService>();

        using var cts = new CancellationTokenSource(ResolvesWithin);

        // Nothing ever publishes a state for a disabled-sourced service — its resource is
        // deliberately not registered — so before this generalization this waited until killed.
        await notifications.WaitForDependenciesAsync(worker.Resource, cts.Token);
    }

    [Fact]
    public async Task DisabledSourcedService_WaitedOnByAContainer_LosesTheWaitAnnotationBeforeStart()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory());

        var inventory = builder.AddService("inventory");
        var storefront = builder.AddContainer("storefront", "nginx:alpine").WaitFor(inventory);

        Assert.Single(storefront.Resource.Annotations.OfType<WaitAnnotation>());

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        Assert.Empty(storefront.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public async Task DroppedWait_IsReportedNamingTheDisabledSource()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory());

        var inventory = builder.AddService("inventory");
        Consumer(builder, "worker").WaitFor(inventory);

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var warning = Assert.Single(warnings);
        Assert.Contains("inventory", warning);
        Assert.Contains("WaitFor from 'worker'", warning);
        Assert.Contains("'disabled'", warning);
    }

    /// <summary>
    /// A container that <c>WithReference</c>s a disabled service is not refused the way one
    /// referencing a "url" service is (#58/#72): a disabled service registers no endpoint at all, so
    /// there is nothing for DCP's container-to-host wiring to trip over — see
    /// <see cref="Sources.DisabledSource"/>'s remarks.
    /// </summary>
    [Fact]
    public async Task DisabledSourcedService_ReferencedByAContainer_IsNotRefused()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory());

        var inventory = builder.AddService("inventory");
        var storefront = builder.AddContainer("storefront", "nginx:alpine").WithReference(inventory);

        var ex = await Record.ExceptionAsync(() => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Null(ex);
    }

    /// <summary>
    /// The removal is keyed on the waited-on service, not on the consumer, so a wait on anything
    /// Aspire actually runs has to survive it — same guard as <c>WaitOnANonUrlResource_SurvivesTheUrlPreflight</c>.
    /// </summary>
    [Fact]
    public async Task WaitOnANonDisabledResource_SurvivesThePreflight()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory());

        // The disabled service is what puts the pre-flight in the graph at all.
        builder.AddService("inventory");

        var migrations = builder.AddContainer("migrations", "nginx:alpine");
        var worker = Consumer(builder, "worker").WaitFor(migrations);

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        var wait = Assert.Single(worker.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Same(migrations.Resource, wait.Resource);
    }
}
