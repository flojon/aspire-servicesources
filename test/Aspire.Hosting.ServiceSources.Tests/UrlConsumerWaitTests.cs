using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Covers issue #170: an AppHost writing <c>WaitFor(service)</c> against a service another
/// developer has switched to <c>"url"</c>. The wait used to never resolve and the consumer sat in
/// <c>Waiting</c> for the life of the run, with no error naming the cause.
/// </summary>
/// <remarks>
/// Driven through <see cref="ResourceNotificationService.WaitForDependenciesAsync"/>, which is the
/// method Aspire's own orchestrator calls to honour a <see cref="WaitAnnotation"/>. That keeps the
/// tests on the real code path without needing DCP to launch anything.
/// </remarks>
public class UrlConsumerWaitTests
{
    private static string AppHostDirectory(string source)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              inventory:
                url:
                  url: https://orders.example.com
                container:
                  image: nginxdemos/hello
                  port: 8080
            """);
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            $$"""{ "services": { "inventory": { "source": "{{source}}" } } }""");
        return dir;
    }

    private static IResourceBuilder<ExecutableResource> Consumer(
        IDistributedApplicationBuilder builder, string name) =>
        builder.AddExecutable(name, "dotnet", Directory.CreateTempSubdirectory().FullName);

    /// <summary>
    /// Generous enough that a real hang is what fails the test rather than a slow machine — the
    /// satisfied case returns without waiting at all.
    /// </summary>
    private static readonly TimeSpan ResolvesWithin = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task UrlSourcedService_WaitedOnByAConsumer_ResolvesRatherThanHanging()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        var inventory = builder.AddService("inventory");
        var worker = Consumer(builder, "worker").WaitFor(inventory);

        var notifications = builder.Services.BuildServiceProvider()
            .GetRequiredService<ResourceNotificationService>();

        using var cts = new CancellationTokenSource(ResolvesWithin);

        // Nothing ever publishes a state for a url-sourced service — its resource is deliberately
        // not registered — so before #170 this waited until the AppHost was killed.
        await notifications.WaitForDependenciesAsync(worker.Resource, cts.Token);
    }

    /// <summary>
    /// <c>WaitForCompletion</c> is the wait an out-of-band service can least honour — nothing is
    /// ever going to exit — and it took the same never-resolving path.
    /// </summary>
    [Fact]
    public async Task UrlSourcedService_WaitedOnForCompletion_ResolvesRatherThanHanging()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        var inventory = builder.AddService("inventory");
        var worker = Consumer(builder, "worker").WaitForCompletion(inventory);

        var notifications = builder.Services.BuildServiceProvider()
            .GetRequiredService<ResourceNotificationService>();

        using var cts = new CancellationTokenSource(ResolvesWithin);

        await notifications.WaitForDependenciesAsync(worker.Resource, cts.Token);
    }

    /// <summary>
    /// The mechanism, pinned separately from the behaviour it produces. Aspire's wait machinery
    /// drops a <see cref="WaitAnnotation"/> whose target is an
    /// <see cref="IResourceWithoutLifetime"/>, so declaring the interface is the whole fix — no
    /// state is published and no annotation is rewritten.
    /// </summary>
    [Fact]
    public void UrlSourcedService_HasNoLifetime()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        var inventory = builder.AddService("inventory");

        Assert.IsAssignableFrom<IResourceWithoutLifetime>(inventory.Resource);
    }

    /// <summary>
    /// The contrast that keeps the fix narrow. Every other source resolves to a resource Aspire
    /// actually runs, so a wait on one has to keep meaning what it says.
    /// </summary>
    [Fact]
    public void ContainerSourcedService_StillHasALifetime()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("container"));

        var inventory = builder.AddService("inventory");

        Assert.IsNotAssignableFrom<IResourceWithoutLifetime>(inventory.Resource);
    }

    /// <summary>
    /// Proves the two tests above are sensitive: the same call against a source that <i>does</i>
    /// run blocks until its resource reports a state, so their passing is Aspire honouring the
    /// marker rather than <c>WaitForDependenciesAsync</c> returning for everything.
    /// </summary>
    /// <remarks>
    /// The only test here that spends its budget rather than returning immediately, so the budget
    /// is small — it is establishing that a wait blocks, which needs nothing longer.
    /// </remarks>
    [Fact]
    public async Task ContainerSourcedService_WaitedOnByAConsumer_StillBlocks()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("container"));

        var inventory = builder.AddService("inventory");
        var worker = Consumer(builder, "worker").WaitFor(inventory);

        var notifications = builder.Services.BuildServiceProvider()
            .GetRequiredService<ResourceNotificationService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => notifications.WaitForDependenciesAsync(worker.Resource, cts.Token));
    }

    /// <summary>
    /// The marker makes the wait resolve, but Aspire reads a <see cref="WaitAnnotation"/> in a
    /// second place too — <c>GetResourceDependenciesAsync</c> counts a wait target as a dependency —
    /// so a container consumer still had the url service plumbed into it and reached
    /// <c>FailedToStart</c>, silently. The pre-flight removes the annotation to close that.
    /// </summary>
    [Fact]
    public async Task UrlSourcedService_WaitedOnByAContainer_LosesTheWaitAnnotationBeforeStart()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        var inventory = builder.AddService("inventory");
        var storefront = builder.AddContainer("storefront", "nginx:alpine").WaitFor(inventory);

        Assert.Single(storefront.Resource.Annotations.OfType<WaitAnnotation>());

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        Assert.Empty(storefront.Resource.Annotations.OfType<WaitAnnotation>());

        // A bare WaitFor is not a reference, so the container check has nothing to say about it —
        // the consumer is meant to start, not to be refused.
        Assert.Contains(
            storefront.Resource.Annotations.OfType<ResourceRelationshipAnnotation>(),
            relationship => relationship.Type == "WaitFor");
    }

    /// <summary>
    /// The same removal for a host-process consumer, where the wait already resolved without it.
    /// One rule everywhere beats a container special case nobody would think to look for.
    /// </summary>
    [Fact]
    public async Task UrlSourcedService_WaitedOnByAnExecutable_AlsoLosesTheWaitAnnotation()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        var inventory = builder.AddService("inventory");
        var worker = Consumer(builder, "worker").WaitFor(inventory);

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        Assert.Empty(worker.Resource.Annotations.OfType<WaitAnnotation>());
    }

    /// <summary>
    /// The removal is keyed on the waited-on service, not on the consumer, so a wait on anything
    /// Aspire actually runs has to survive it.
    /// </summary>
    [Fact]
    public async Task WaitOnANonUrlResource_SurvivesTheUrlPreflight()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        // The url service is what puts the pre-flight in the graph at all.
        builder.AddService("inventory");

        var migrations = builder.AddContainer("migrations", "nginx:alpine");
        var worker = Consumer(builder, "worker").WaitFor(migrations);

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        var wait = Assert.Single(worker.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Same(migrations.Resource, wait.Resource);
    }

    /// <summary>
    /// A url-sourced service reached through <c>AddConnectionString</c> rather than a
    /// <c>WaitFor</c> the AppHost wrote. Aspire adds a <c>WaitForStart</c> of its own for each
    /// resource a connection-string expression references, and it took the same never-resolving
    /// path — with nothing in the AppHost to point at, since nobody wrote the wait.
    /// </summary>
    [Fact]
    public async Task UrlSourcedService_ReferencedByAConnectionString_ResolvesRatherThanHanging()
    {
        var builder = TestHelpers.CreateBuilderThatCanStart(AppHostDirectory("url"));

        var inventory = builder.AddService("inventory");
        var connectionString = builder.AddConnectionString(
            "inventory-cs", ReferenceExpression.Create($"{inventory.GetEndpoint("https")}"));

        var notifications = builder.Services.BuildServiceProvider()
            .GetRequiredService<ResourceNotificationService>();

        using var cts = new CancellationTokenSource(ResolvesWithin);

        await notifications.WaitForDependenciesAsync(connectionString.Resource, cts.Token);
    }
}
