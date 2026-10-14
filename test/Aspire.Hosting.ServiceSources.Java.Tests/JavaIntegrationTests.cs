using Aspire.Hosting.ApplicationModel;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

/// <summary>
/// This project references only the Community Toolkit's integration, so it is the AppHost the
/// official package's absence has to leave exactly as it was.
/// </summary>
public class JavaIntegrationTests
{
    [Fact]
    public void Installed_WithoutTheOfficialPackage_IsTheCommunityToolkits()
    {
        Assert.IsType<CommunityToolkitJavaIntegration>(JavaIntegration.Installed);
        Assert.Equal(typeof(JavaAppExecutableResource), new JavaLocalResourceKind().ResourceType);
    }

    [Fact]
    public void OfficialIntegration_NotInstalled_IsNotLoaded() =>
        Assert.Null(OfficialJavaIntegration.TryLoad());
}
