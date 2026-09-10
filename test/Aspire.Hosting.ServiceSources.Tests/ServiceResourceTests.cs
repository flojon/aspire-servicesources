using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Pins the exact shape PR #319 measured as "shape D" — the one the TypeScript codegen wall does
/// not apply to, and the one the ticket's acceptance item 3 names. A regression here (an interface
/// added or dropped) changes generated TypeScript without any test elsewhere catching it, since
/// nothing else asserts ServiceResource's own interface list.
/// </summary>
public class ServiceResourceTests
{
    [Fact]
    public void ServiceResource_DeclaresExactlyTheFiveCapabilityInterfaces()
    {
        var type = typeof(ServiceResource);

        Assert.True(type.IsSealed);
        Assert.True(typeof(Resource).IsAssignableFrom(type));

        var interfaces = type.GetInterfaces();
        Assert.Contains(typeof(IResourceWithServiceDiscovery), interfaces);
        Assert.Contains(typeof(IResourceWithEnvironment), interfaces);
        Assert.Contains(typeof(IResourceWithArgs), interfaces);
        Assert.Contains(typeof(IResourceWithEndpoints), interfaces);
        Assert.Contains(typeof(IResourceWithWaitSupport), interfaces);

        // Deliberately excluded (spec §1): declaring these unconditionally on a type every source
        // shares would suppress WaitFor/DCP-execution-model behaviour for sources that need it.
        Assert.DoesNotContain(typeof(IResourceWithoutLifetime), interfaces);
        Assert.DoesNotContain(typeof(IComputeResource), interfaces);
#pragma warning disable ASPIREPROBES001 // IResourceWithProbes is [Experimental]; only referenced here to assert ServiceResource doesn't implement it.
        Assert.DoesNotContain(typeof(IResourceWithProbes), interfaces);
#pragma warning restore ASPIREPROBES001
    }

    [Fact]
    public void ServiceResource_ConstructsWithTheGivenName()
    {
        var resource = new ServiceResource("orders");

        Assert.Equal("orders", resource.Name);
    }
}
