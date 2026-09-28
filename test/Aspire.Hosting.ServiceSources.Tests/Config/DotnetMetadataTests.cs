using Aspire.Hosting.ServiceSources.Config;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

public class DotnetMetadataTests
{
    [Fact]
    public void Resolve_Null_IsNoNameAndNoExclude() =>
        Assert.Equal((null, false), DotnetMetadata.Resolve(null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankName_IsAbsent(string? name) =>
        Assert.Equal((null, false), DotnetMetadata.Resolve(new() { LaunchProfileName = name }));

    [Fact]
    public void Resolve_PaddedName_IsUsedVerbatim() =>
        Assert.Equal((" http ", false), DotnetMetadata.Resolve(new() { LaunchProfileName = " http " }));

    [Fact]
    public void Resolve_ExcludeTrue_IsExclude() =>
        Assert.Equal((null, true), DotnetMetadata.Resolve(new() { ExcludeLaunchProfile = true }));

    [Fact]
    public void Resolve_ExcludeFalseWithName_SelectsTheName() =>
        Assert.Equal(("http", false), DotnetMetadata.Resolve(new() { LaunchProfileName = "http", ExcludeLaunchProfile = false }));

    [Fact]
    public void Validate_ExcludeTrueWithName_ThrowsNamingServiceAndBothFields()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => DotnetMetadata.Validate(
            "orders", LocalKinds.Dotnet, new() { LaunchProfileName = "http", ExcludeLaunchProfile = true }));
        Assert.Contains("orders", ex.Message);
        Assert.Contains("launchProfileName", ex.Message);
        Assert.Contains("excludeLaunchProfile", ex.Message);
    }

    [Fact]
    public void Validate_ExcludeTrueWithBlankName_IsFine() =>
        DotnetMetadata.Validate("orders", LocalKinds.Dotnet, new() { LaunchProfileName = " ", ExcludeLaunchProfile = true });

    [Fact]
    public void Validate_NonDotnetKind_ThrowsNamingServiceAndKind()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => DotnetMetadata.Validate(
            "orders", "java", new() { LaunchProfileName = "http" }));
        Assert.Contains("orders", ex.Message);
        Assert.Contains("java", ex.Message);
    }

    [Fact]
    public void Validate_NullMetadata_NeverThrows() => DotnetMetadata.Validate("orders", "java", null);
}
