using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// Covers issue #328: a <c>WaitFor</c>/<c>WaitForCompletion</c> naming an <c>AddService()</c> result
/// records a <see cref="WaitAnnotation"/> pointing at the <see cref="ServiceResource"/> facade, which
/// <see cref="ResolvedService.Bridge"/> never adds to the app model — so nothing ever publishes a
/// state for it and the wait never resolves. <see cref="ServiceWaitRetargeting"/> rewrites that
/// annotation at <c>BeforeStartEvent</c> to point at the real, registered resource instead.
/// </summary>
public class ServiceWaitRetargetingTests
{
    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilderThatCanStart(TempDirectories.CreateSubdirectory().FullName);

    /// <summary>
    /// The dependency position: a plain, non-service consumer <c>WaitFor</c>s an
    /// <c>AddService()</c> result directly. Aspire's own <c>WaitFor</c> writes the annotation
    /// straight onto the consumer's real, registered resource — no dual-write involved — so this is
    /// the shape the issue's runtime repro hit whenever the AppHost waited on a service at all.
    /// </summary>
    [Fact]
    public async Task ContainerWaitingOnAServiceFacade_IsRetargetedToTheRealResource()
    {
        var builder = Builder();
        var real = builder.AddResource(new ServiceContainerResource("payments")).WithImage("nginx");
        var payments = ResolvedService.Bridge(real, "payments", "container");

        var consumer = builder.AddContainer("storefront", "nginx:alpine").WaitFor(payments);

        var before = Assert.Single(consumer.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Same(payments.Resource, before.Resource);
        Assert.DoesNotContain(builder.Resources, r => ReferenceEquals(r, before.Resource));

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        var after = Assert.Single(consumer.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Same(real.Resource, after.Resource);
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, after.Resource));
        Assert.Equal(before.WaitType, after.WaitType);
    }

    /// <summary>
    /// The same shape one level up: the waiter is itself a bridged <c>ServiceResource</c>, so
    /// <c>WaitForCompletion</c> goes through <see cref="ServiceResourceBuilder.WithAnnotation"/> and
    /// dual-writes the annotation onto both the waiter's facade and its real resource. What lands on
    /// the real, registered resource is what this fix has to retarget.
    /// </summary>
    [Fact]
    public async Task ServiceWaitingOnAnotherServiceFacade_IsRetargetedToTheRealResource()
    {
        var builder = Builder();

        var initReal = builder.AddResource(new ServiceContainerResource("common-auth-init")).WithImage("nginx");
        var init = ResolvedService.Bridge(initReal, "common-auth-init", "container");

        var authReal = builder.AddResource(new ServiceContainerResource("common-auth")).WithImage("nginx");
        ResolvedService.Bridge(authReal, "common-auth", "container").WaitForCompletion(init);

        var before = Assert.Single(authReal.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Same(init.Resource, before.Resource);

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        var after = Assert.Single(authReal.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Same(initReal.Resource, after.Resource);
        Assert.Equal(WaitType.WaitForCompletion, after.WaitType);
    }

    /// <summary>
    /// The rewrite is keyed on the wait's target being a <see cref="ServiceResource"/> facade, not on
    /// the waiter — a wait on an ordinary resource this package never touched has to survive
    /// untouched, same instance included.
    /// </summary>
    [Fact]
    public async Task WaitOnAPlainResource_IsUnaffected()
    {
        var builder = Builder();

        // Puts a bridged service in the model at all, so the rewrite has something to walk.
        var real = builder.AddResource(new ServiceContainerResource("payments")).WithImage("nginx");
        ResolvedService.Bridge(real, "payments", "container");

        var migrations = builder.AddContainer("migrations", "nginx:alpine");
        var worker = builder.AddContainer("worker", "nginx:alpine").WaitFor(migrations);

        var before = Assert.Single(worker.Resource.Annotations.OfType<WaitAnnotation>());

        await TestHelpers.PublishBeforeStartEventAsync(builder);

        var after = Assert.Single(worker.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Same(before, after);
        Assert.Same(migrations.Resource, after.Resource);
    }
}
