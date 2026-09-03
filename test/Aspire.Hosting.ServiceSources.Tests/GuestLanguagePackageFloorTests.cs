namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The minimum version of each guest-language hosting package is stated twice, because two
/// different mechanisms enforce it: <c>build/KoalaSoft.Aspire.Hosting.ServiceSources.targets</c>
/// fails a consumer's build, and <see cref="GuestLanguagePackages"/> explains the runtime load
/// failure for the consumers that file cannot reach. Two numbers that must agree and are edited in
/// different files is exactly the shape that rots quietly, so it is checked.
/// </summary>
public class GuestLanguagePackageFloorTests
{
    [Fact]
    public void EveryFloorInCode_MatchesTheFloorTheTargetsFileEnforces()
    {
        var targets = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "Aspire.Hosting.ServiceSources", "build",
            "KoalaSoft.Aspire.Hosting.ServiceSources.targets"));

        foreach (var (packageId, minimumVersion) in GuestLanguagePackages.Floors)
        {
            // The property is named for the language rather than the package, so derive the
            // property name the same way the targets file spells it.
            var language = packageId.Replace("CommunityToolkit.Aspire.Hosting.", "", StringComparison.Ordinal)
                .Replace("Aspire.Hosting.", "", StringComparison.Ordinal);
            var property = $"<ServiceSources{language}MinVersion>{minimumVersion}</ServiceSources{language}MinVersion>";

            Assert.Contains(property, targets, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Walks up to the directory holding the solution, so the test does not care how deep the test
    /// assembly's output directory happens to be.
    /// </summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ServiceSources.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
