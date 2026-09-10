using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Round-2 review fix (#313): <see cref="Reachability"/>'s decoration table gates by <see
/// cref="Type"/> identity, not <c>Type.Name</c> — guards against an unrelated type that merely
/// shares a decoration type's simple name silently bypassing the fail-closed gate the round-1 fix
/// exists to enforce.
/// </summary>
public class ReachabilityTests
{
    // Same simple Type.Name as Aspire's own decoration annotations but otherwise unrelated types —
    // Type.Name alone can't tell these apart from the real thing; only Type identity can.
    private sealed class ResourceRelationshipAnnotation;
    private sealed class EndpointReferenceAnnotation;

    [Fact]
    public void ATypeSharingADecorationTypesSimpleName_DoesNotImpersonateIt()
    {
        Assert.True(Reachability.IsUnreachable(typeof(ResourceRelationshipAnnotation), "url"));
        Assert.True(Reachability.IsUnreachable(typeof(EndpointReferenceAnnotation), "kubernetes"));
    }

    [Fact]
    public void TheRealDecorationTypes_StillDualWriteSilently()
    {
        Assert.False(Reachability.IsUnreachable(
            typeof(Aspire.Hosting.ApplicationModel.ResourceRelationshipAnnotation), "url"));
        Assert.False(Reachability.IsUnreachable(
            typeof(Aspire.Hosting.ApplicationModel.EndpointReferenceAnnotation), "kubernetes"));
    }
}
