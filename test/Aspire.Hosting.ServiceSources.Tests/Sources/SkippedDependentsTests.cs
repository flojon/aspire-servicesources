using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// What waits on a service the developer is about to leave unstarted, so the prompt can say so.
/// </summary>
public class SkippedDependentsTests
{
    private static DeferredCheckout.Deferred Service(string name, IResource resource, params IResource[] heldBack) =>
        new(
            name, resource, heldBack, RepoRoot: "", Definition: null!, Config: null!, RepositoryConfig: null,
            AppHostDirectory: "", Prefetch: null!, GitClient: null!, PrepareStep: null, PrepareRunner: null!,
            OnCheckoutLanded: static (_, _, _) => { });

    private static ContainerResource Container(string name, params IResource[] waitsOn)
    {
        var resource = new ContainerResource(name);
        foreach (var target in waitsOn)
        {
            resource.Annotations.Add(new WaitAnnotation(target, WaitType.WaitUntilHealthy));
        }

        return resource;
    }

    private static ExecutableResource Real(string name) =>
        Annotated(new ExecutableResource(name, "run", "."), name);

    private static T Annotated<T>(T resource, string serviceName) where T : IResource
    {
        resource.Annotations.Add(new ServiceSourceAnnotation(serviceName, "repository"));
        return resource;
    }

    private static string? DependentsOf(
        string service, IEnumerable<IResource> model, params DeferredCheckout.Deferred[] services) =>
        SkippedDependents.For(model, services).TryGetValue(service, out var text) ? text.ToString() : null;

    [Fact]
    public void WaitOnTheRealResource_IsListed()
    {
        var orders = Real("orders");
        var web = Container("web", orders);

        Assert.Equal(
            "Waited on by: web", DependentsOf("orders", [orders, web], Service("orders", orders)));
    }

    [Fact]
    public void NothingWaiting_HasNoEntry()
    {
        var orders = Real("orders");

        Assert.Null(DependentsOf("orders", [orders, Container("web")], Service("orders", orders)));
    }

    [Fact]
    public void WaitOnAHeldBackHelper_IsListed()
    {
        var orders = Real("orders");
        var installer = new ExecutableResource("orders-install", "install", ".");
        var web = Container("web", installer);

        Assert.Equal(
            "Waited on by: web",
            DependentsOf("orders", [orders, installer, web], Service("orders", orders, installer)));
    }

    [Fact]
    public void WaitOnAServiceResourceFacade_IsListed()
    {
        var orders = Real("orders");
        var facade = Annotated(new ServiceResource("orders"), "orders");
        var web = Container("web", facade);

        Assert.Equal(
            "Waited on by: web", DependentsOf("orders", [orders, web], Service("orders", orders)));
    }

    [Fact]
    public void FacadeAndRealBothWaitedOn_AreCountedOnce()
    {
        var orders = Real("orders");
        var facade = Annotated(new ServiceResource("orders"), "orders");
        var web = Container("web", facade, orders);

        Assert.Equal(
            "Waited on by: web", DependentsOf("orders", [orders, facade, web], Service("orders", orders)));
    }

    [Fact]
    public void ADependentThatIsAFacadeAndItsRealResource_IsCountedOnce()
    {
        var orders = Real("orders");
        var billing = Real("billing");
        billing.Annotations.Add(new WaitAnnotation(orders, WaitType.WaitUntilHealthy));
        var billingFacade = Annotated(new ServiceResource("billing"), "billing");
        billingFacade.Annotations.Add(new WaitAnnotation(orders, WaitType.WaitUntilHealthy));

        Assert.Equal(
            "Waited on by: billing",
            DependentsOf("orders", [orders, billing, billingFacade], Service("orders", orders)));
    }

    [Fact]
    public void ServiceItsOwnHelpersWaitingOnEachOther_AreNotDependents()
    {
        var installer = new ExecutableResource("orders-install", "install", ".");
        var orders = Real("orders");
        orders.Annotations.Add(new WaitAnnotation(installer, WaitType.WaitUntilHealthy));

        Assert.Null(DependentsOf("orders", [orders, installer], Service("orders", orders, installer)));
    }

    [Fact]
    public void Dependents_AreFollowedTransitively()
    {
        var orders = Real("orders");
        var web = Container("web", orders);
        var admin = Container("admin", web);

        Assert.Equal(
            "Waited on by: admin, web", DependentsOf("orders", [orders, web, admin], Service("orders", orders)));
    }

    [Fact]
    public void WaitCycle_Terminates()
    {
        var orders = Real("orders");
        var web = Container("web", orders);
        orders.Annotations.Add(new WaitAnnotation(web, WaitType.WaitUntilHealthy));

        Assert.Equal(
            "Waited on by: web", DependentsOf("orders", [orders, web], Service("orders", orders)));
    }

    [Fact]
    public void Dependents_AreSortedOrdinally()
    {
        var orders = Real("orders");
        var consumers = new[] { "b", "B", "a", "_" }.Select(name => Container(name, orders)).ToArray();

        Assert.Equal(
            "Waited on by: B, _, a, b",
            DependentsOf("orders", [orders, .. consumers], Service("orders", orders)));
    }

    [Fact]
    public void MoreThanFiveDependents_AreCappedWithACount()
    {
        var orders = Real("orders");
        var consumers = Enumerable.Range(1, 8).Select(n => Container($"c{n}", orders)).ToArray();

        Assert.Equal(
            "Waited on by: c1, c2, c3, c4, c5 and 3 more",
            DependentsOf("orders", [orders, .. consumers], Service("orders", orders)));
    }

    [Fact]
    public void ExactlyFiveDependents_AreAllNamed()
    {
        var orders = Real("orders");
        var consumers = Enumerable.Range(1, 5).Select(n => Container($"c{n}", orders)).ToArray();

        Assert.Equal(
            "Waited on by: c1, c2, c3, c4, c5",
            DependentsOf("orders", [orders, .. consumers], Service("orders", orders)));
    }

    [Fact]
    public void ADependentIsShownByItsServiceName_ElseItsResourceName()
    {
        var orders = Real("orders");
        var billing = Annotated(Container("billing-resource", orders), "billing");
        var web = Container("web", orders);

        Assert.Equal(
            "Waited on by: billing, web",
            DependentsOf("orders", [orders, billing, web], Service("orders", orders)));
    }

    [Fact]
    public void DependentsThatAreThemselvesUnpicked_AreStillListed()
    {
        var orders = Real("orders");
        var billing = Real("billing");
        billing.Annotations.Add(new WaitAnnotation(orders, WaitType.WaitUntilHealthy));
        var ordersService = Service("orders", orders);
        var billingService = Service("billing", billing);

        var result = SkippedDependents.For([orders, billing], [ordersService, billingService]);

        Assert.Equal("Waited on by: billing", result["orders"].ToString());
        Assert.False(result.ContainsKey("billing"));
    }

    [Fact]
    public void NamesAreEscapedAndNotTruncated()
    {
        var orders = Real("orders");
        var longName = new string('x', 100);
        var web = Container(longName + "\u001b[31m", orders);

        var text = DependentsOf("orders", [orders, web], Service("orders", orders));

        Assert.NotNull(text);
        Assert.Contains(longName, text);
        Assert.DoesNotContain('\u001b', text);
    }
}
