using ServiceSourcesOverloadProbe;

namespace Aspire.Hosting.ServiceSources.Tests;

public class OverloadResolutionProbeGuardTests
{
    [Fact]
    public void OverloadProbe_IsNotNestedUnderAnyAspireNamespace() =>
        Assert.False(
            typeof(OverloadProbe).Namespace!.StartsWith("Aspire.Hosting", StringComparison.Ordinal),
            "OverloadProbe must stay outside the Aspire.Hosting/Aspire.Hosting.ServiceSources namespace tree — " +
            "moving it inside would silently make every test routed through it vacuous again (see OverloadResolutionProbe.cs).");
}
