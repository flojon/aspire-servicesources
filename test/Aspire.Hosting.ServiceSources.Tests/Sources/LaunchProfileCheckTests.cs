using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class LaunchProfileCheckTests
{
    private const string TwoProfiles = """
        { "profiles": { "http": { "commandName": "Project" }, "https": { "commandName": "Project" } } }
        """;

    private static string ProjectWith(string? launchSettings)
    {
        var directory = TempDirectories.CreateSubdirectory("launch-profile-");
        var projectFile = Path.Combine(directory.FullName, "Orders.csproj");
        File.WriteAllText(projectFile, "<Project />");

        if (launchSettings is not null)
        {
            Directory.CreateDirectory(Path.Combine(directory.FullName, "Properties"));
            File.WriteAllText(Path.Combine(directory.FullName, "Properties", "launchSettings.json"), launchSettings);
        }

        return projectFile;
    }

    [Fact]
    public void Verify_NameNull_NeverReadsTheFile() =>
        LaunchProfileCheck.Verify("orders", ProjectWith(null), null);

    [Fact]
    public void Verify_NameInFile_Passes() =>
        LaunchProfileCheck.Verify("orders", ProjectWith(TwoProfiles), "https");

    [Fact]
    public void Verify_NameMissing_ThrowsListingServiceNameFileAndAvailableProfiles()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LaunchProfileCheck.Verify("orders", ProjectWith(TwoProfiles), "staging"));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("staging", ex.Message, StringComparison.Ordinal);
        Assert.Contains("launchSettings.json", ex.Message, StringComparison.Ordinal);
        Assert.Contains("http", ex.Message, StringComparison.Ordinal);
        Assert.Contains("https", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_NoLaunchSettingsAtAll_ThrowsSayingTheFileIsAbsent()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LaunchProfileCheck.Verify("orders", ProjectWith(null), "http"));

        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Add that file", ex.Message, StringComparison.Ordinal);
        Assert.Contains("remove 'launchProfileName'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("launchSettings.json", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_PaddedName_Fails() =>
        Assert.Throws<ServiceSourcesConfigurationException>(
            () => LaunchProfileCheck.Verify("orders", ProjectWith(TwoProfiles), " http "));

    [Fact]
    public void Verify_MatchIsCaseSensitive() =>
        Assert.Throws<ServiceSourcesConfigurationException>(
            () => LaunchProfileCheck.Verify("orders", ProjectWith(TwoProfiles), "HTTP"));

    [Fact]
    public void Verify_FileWithCommentsAndTrailingCommas_Passes() =>
        LaunchProfileCheck.Verify("orders", ProjectWith("""
            {
              // a comment
              "profiles": {
                "http": { "commandName": "Project", },
              },
            }
            """), "http");

    [Fact]
    public void Verify_UnparseableFile_SkipsTheCheck() =>
        LaunchProfileCheck.Verify("orders", ProjectWith("{ not json"), "http");

    [Fact]
    public void Verify_HostileProfileKey_IsNotReproducedRaw()
    {
        var projectFile = ProjectWith("""{ "profiles": { "evil\u001b[31mred": { "commandName": "Project" } } }""");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LaunchProfileCheck.Verify("orders", projectFile, "x"));

        Assert.DoesNotContain('\u001b', ex.Message);
    }

    [Fact]
    public void ProfileNames_NoFile_IsAbsent()
    {
        var names = LandedLaunchProfile.ProfileNames(ProjectWith(null));

        Assert.Equal(LaunchSettingsState.Absent, names.State);
        Assert.Empty(names.Names);
    }

    [Fact]
    public void ProfileNames_BadJson_IsUnreadable() =>
        Assert.Equal(LaunchSettingsState.Unreadable, LandedLaunchProfile.ProfileNames(ProjectWith("{ not json")).State);

    [Fact]
    public void ProfileNames_GoodFile_ListsNamesInFileOrder()
    {
        var names = LandedLaunchProfile.ProfileNames(ProjectWith(
            """{ "profiles": { "zeta": {}, "alpha": {} } }"""));

        Assert.Equal(LaunchSettingsState.Read, names.State);
        Assert.Equal(["zeta", "alpha"], names.Names);
    }

    [Fact]
    public void ProfileNames_NonObjectProfileValue_IsExcluded()
    {
        var names = LandedLaunchProfile.ProfileNames(ProjectWith(
            """{ "profiles": { "http": {}, "bogus": 3 } }"""));

        Assert.Equal(["http"], names.Names);
    }

    [Fact]
    public void ProfileNames_NoProfilesObject_IsReadWithNoNames()
    {
        var names = LandedLaunchProfile.ProfileNames(ProjectWith("{}"));

        Assert.Equal(LaunchSettingsState.Read, names.State);
        Assert.Empty(names.Names);
    }
}
