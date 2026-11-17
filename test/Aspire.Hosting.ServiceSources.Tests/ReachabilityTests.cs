using Aspire.Hosting.ApplicationModel;
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

    private sealed class DerivedHealthCheck() : HealthCheckAnnotation("x");
    private sealed class TestAnnotation : IResourceAnnotation;

    public static TheoryData<string, Type, bool> Matrix()
    {
        var data = new TheoryData<string, Type, bool>();
        string[] sources = ["url", "kubernetes", "disabled", UnconfiguredSource.SourceName];
        Type[] gated =
        [
            typeof(WaitAnnotation), typeof(HealthCheckAnnotation), typeof(DerivedHealthCheck),
            typeof(EnvironmentCallbackAnnotation), typeof(ResourceCommandAnnotation), typeof(TestAnnotation),
        ];

        foreach (var source in sources)
        {
            foreach (var type in gated)
            {
                var reachable = source == "kubernetes"
                    && (type == typeof(WaitAnnotation) || type == typeof(HealthCheckAnnotation));
                data.Add(source, type, !reachable);
            }

            data.Add(source, typeof(Aspire.Hosting.ApplicationModel.ResourceRelationshipAnnotation), false);
            data.Add(source, typeof(Aspire.Hosting.ApplicationModel.EndpointReferenceAnnotation), false);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Matrix_OfSourceAndAnnotationType_IsUnreachableExactlyWhereExpected(string source, Type type, bool unreachable) =>
        Assert.Equal(unreachable, Reachability.IsUnreachable(type, source));

    [Fact]
    public void HealthCheckAnnotation_IsLabelledWithTheMethodTheAuthorCalled() =>
        Assert.Equal("WithHealthCheck/WithHttpHealthCheck", Reachability.CapabilityLabel(typeof(HealthCheckAnnotation)));

    [Theory]
    [InlineData("url")]
    [InlineData("kubernetes")]
    [InlineData("disabled")]
    [InlineData(UnconfiguredSource.SourceName)]
    [InlineData("repository")]
    [InlineData("container")]
    public void TheTwoPredicates_AgreeOnWaitFor_ForEverySource(string source) =>
        Assert.Equal(
            Reachability.IsUnreachable(typeof(WaitAnnotation), source),
            ServiceConfigurationExtensions.IsUnreachable<IResourceWithWaitSupport>(source));
}
