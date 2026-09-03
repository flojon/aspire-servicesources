using System.Text.RegularExpressions;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The minimum version of each guest-language hosting package is stated in several places, because
/// several different mechanisms need it: <c>build/KoalaSoft.Aspire.Hosting.ServiceSources.targets</c>
/// fails a consumer's build, <see cref="GuestLanguagePackages"/> explains the runtime load failure
/// for the consumers that file cannot reach, and the version core actually restores against lives in
/// the repository's props files. Numbers that must agree and are edited in different files are
/// exactly the shape that rots quietly, so they are checked against each other.
/// </summary>
public class GuestLanguagePackageFloorTests
{
    [Fact]
    public void EveryFloorInCode_MatchesTheFloorTheTargetsFileEnforces()
    {
        var targets = File.ReadAllText(TargetsPath());

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
    /// A floor has to be the version core actually restores against, and nothing structural makes it
    /// so: those versions live in <c>Directory.Build.props</c> and <c>Directory.Packages.props</c>
    /// while the floors are literals in two other files. Drift is silent and one-directional in the
    /// worst way — the runtime rolls an assembly reference forward and refuses to roll it back, so a
    /// floor left behind a version bump promises a version that no longer works, and reports that
    /// wrong number when it fails.
    /// </summary>
    /// <remarks>
    /// Compared against the <em>default</em> versions the repository states, not against whatever
    /// this particular build compiled against: <c>aspire-matrix.yml</c> rebuilds the whole repository
    /// under <c>-p:AspireVersion=</c> to exercise a version other than the floor, and those builds
    /// ship nothing, so a floor that does not match one of them is not a defect. What a consumer who
    /// pins nothing gets is the default, and the default is what a floor has to describe.
    /// </remarks>
    [Fact]
    public void EveryFloor_IsTheVersionTheRepositoryRestoresByDefault()
    {
        var root = RepositoryRoot();

        // Read the same way aspire-matrix.yml's `floor` leg reads it, and for the same reason: the
        // default sits in an attribute-conditioned element, so there is exactly one to find.
        var aspireVersion = SingleCapture(
            File.ReadAllText(Path.Combine(root, "Directory.Build.props")),
            @"<AspireVersion[^>]*>([^<]+)</AspireVersion>",
            "the <AspireVersion> default in Directory.Build.props");

        var packages = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));

        var restoredVersions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Aspire.Hosting.JavaScript moves with Aspire.Hosting as one matched set, so its floor
            // is the Aspire floor rather than a version of its own.
            ["Aspire.Hosting.JavaScript"] = aspireVersion,
            ["CommunityToolkit.Aspire.Hosting.Java"] = SingleCapture(
                packages,
                @"<PackageVersion\s+Include=""CommunityToolkit\.Aspire\.Hosting\.Java""\s+Version=""([^""]+)""",
                "the CommunityToolkit.Aspire.Hosting.Java PackageVersion in Directory.Packages.props"),
        };

        foreach (var (packageId, minimumVersion) in GuestLanguagePackages.Floors)
        {
            Assert.True(
                restoredVersions.TryGetValue(packageId, out var restored),
                $"{packageId} has a floor but this test does not know where its restored version is "
                + "stated, so that floor is unguarded. Add it here.");

            Assert.Equal(restored, minimumVersion);
        }
    }

    /// <summary>
    /// The complement, and the one direction that stays true under an <c>-p:AspireVersion=</c>
    /// override: core must never compile against something older than the floor it advertises, or the
    /// build-time gate would accept a version core itself could not be satisfied by.
    /// </summary>
    /// <remarks>
    /// <c>GetReferencedAssemblies</c> reads metadata and needs neither assembly on disk, which is
    /// what lets this run in a test project that has neither. Compared on major.minor.patch: both
    /// packages currently give their assembly the same version as the package, and one that stopped
    /// doing that would need this revisited.
    /// </remarks>
    [Fact]
    public void CoreNeverCompilesAgainstSomethingOlderThanTheFloorItAdvertises()
    {
        var references = typeof(GuestLanguagePackages).Assembly.GetReferencedAssemblies()
            .ToDictionary(reference => reference.Name!, reference => reference.Version!);

        foreach (var (packageId, minimumVersion) in GuestLanguagePackages.Floors)
        {
            Assert.True(
                references.TryGetValue(packageId, out var compiledAgainst),
                $"core declares a floor for {packageId} but does not reference it at all, so the "
                + "floor guards nothing.");

            var floor = Version.Parse(minimumVersion);
            var compiled = new Version(
                compiledAgainst!.Major, compiledAgainst.Minor, compiledAgainst.Build);

            Assert.True(
                compiled >= floor,
                $"core compiles against {packageId} {compiled} but advertises a floor of {floor}, so "
                + "the build-time gate would accept a version core cannot be satisfied by.");
        }
    }

    /// <summary>
    /// Two tables have to be edited together for a kind: the floors, keyed by assembly, and the
    /// kind-to-assembly map that decides which floor a failing service can be told about. Wire only
    /// the first and that kind's too-old report silently degrades to the generic "the handler
    /// failed" message — nothing errors, so nothing says so. #46-#50 each add a kind, so this is
    /// checked rather than remembered.
    /// </summary>
    [Fact]
    public void EveryKindAndEveryFloor_AreWiredToEachOther()
    {
        var floors = GuestLanguagePackages.Floors.Select(floor => floor.PackageId).ToHashSet(StringComparer.Ordinal);
        var mapped = GuestLanguagePackages.KindAssemblies.Select(entry => entry.AssemblyName).ToHashSet(StringComparer.Ordinal);

        // Assembly simple name and package id are the same string for both of these today; if a
        // package ever ships an assembly named differently this needs a second lookup, not a
        // relaxed assertion.
        Assert.Equal(floors.OrderBy(id => id, StringComparer.Ordinal), mapped.OrderBy(id => id, StringComparer.Ordinal));

        foreach (var (kind, _) in GuestLanguagePackages.KindAssemblies)
        {
            Assert.False(string.IsNullOrWhiteSpace(kind));
        }
    }

    private static string TargetsPath() => Path.Combine(
        RepositoryRoot(), "src", "Aspire.Hosting.ServiceSources", "build",
        "KoalaSoft.Aspire.Hosting.ServiceSources.targets");

    /// <summary>
    /// Exactly one match, not the first: a second <c>&lt;AspireVersion&gt;</c> or
    /// <c>PackageVersion</c> for the same id would mean the file no longer says one thing, and
    /// silently reading the first is how this check would start passing against the wrong number.
    /// </summary>
    private static string SingleCapture(string content, string pattern, string what)
    {
        var matches = Regex.Matches(content, pattern);

        Assert.True(
            matches.Count == 1,
            $"expected exactly one occurrence of {what}, found {matches.Count} — this check is not "
            + "reading what it thinks it is.");

        return matches[0].Groups[1].Value.Trim();
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
