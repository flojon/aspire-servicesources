using System.Text.Json;
using System.Xml.Linq;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The barrier files stop MSBuild's and NuGet's upward directory walk at the tool-owned
/// <c>.servicesources</c> directory, so a checkout underneath it builds under its own repository's
/// settings instead of the AppHost repository's.
/// </summary>
public class CheckoutBuildBarrierTests
{
    private static string NewToolDirectory()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory().FullName, ".servicesources");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Ensure_WritesAnEmptyDirectoryBuildPropsAndTargets()
    {
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir);

        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
        {
            var document = XDocument.Load(Path.Combine(dir, name));
            Assert.Equal("Project", document.Root!.Name.LocalName);
            Assert.Empty(document.Root.Elements());
        }
    }

    [Fact]
    public void Ensure_WritesADirectoryPackagesPropsThatTurnsOffCentralPackageManagement()
    {
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir);

        var document = XDocument.Load(Path.Combine(dir, "Directory.Packages.props"));
        var value = document.Root!
            .Elements("PropertyGroup")
            .Elements("ManagePackageVersionsCentrally")
            .Single()
            .Value;
        Assert.Equal("false", value);
    }

    [Fact]
    public void Ensure_WritesANuGetConfigThatClearsTheInheritedPackageSourceMapping()
    {
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir);

        var document = XDocument.Load(Path.Combine(dir, "nuget.config"));
        Assert.Equal("configuration", document.Root!.Name.LocalName);
        var mapping = document.Root.Elements("packageSourceMapping").Single();
        Assert.Single(mapping.Elements("clear"));
    }

    [Fact]
    public void Ensure_WritesANuGetConfigThatLeavesInheritedPackageSourcesAlone()
    {
        // Clearing the mapping already costs a supply-chain control (see the barrier's own remarks).
        // Clearing the *sources* on top of that would remove feeds the checkout may need and buy
        // nothing, so the barrier must not do it.
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir);

        var document = XDocument.Load(Path.Combine(dir, "nuget.config"));
        Assert.Empty(document.Root!.Elements("packageSources"));
    }

    [Fact]
    public void Ensure_WritesAnEditorConfigThatEndsTheUpwardWalkAndCarriesNoStyleOfItsOwn()
    {
        // .editorconfig cascades up too, and stops only at a file setting root = true. Severity
        // comes from the .editorconfig itself, so the Directory.Build.props barrier does not cover
        // it: a host repo spelling its code style as dotnet_diagnostic.<id>.severity = error would
        // otherwise raise the checkout's own analyzers to errors.
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir);

        var directives = File.ReadAllLines(Path.Combine(dir, ".editorconfig"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
        Assert.Equal(["root = true"], directives);
    }

    [Fact]
    public void Ensure_WritesAGlobalJsonThatRequestsNoSdkVersionAndPinsNoMSBuildSdk()
    {
        // hostfxr stops at the first global.json *file* it finds — it does not keep walking in
        // search of one carrying an sdk section — so an empty object requests no version at all,
        // which is exactly what no global.json existing anywhere does. That is the neutral value
        // the issue assumed did not exist.
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir);

        // Parsed with comments skipped because the file carries a banner. That both real readers of
        // this file tolerate one — hostfxr, and MSBuild's own msbuild-sdks reader — is what makes
        // the banner safe, and is checked against the installed SDK by the manual procedure in
        // docs/superpowers/, not here: this assertion only pins the shape of what is written.
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir, "global.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.False(document.RootElement.TryGetProperty("sdk", out _));
        Assert.False(document.RootElement.TryGetProperty("msbuild-sdks", out _));
    }

    [Fact]
    public void Ensure_RunAgainOverItsOwnOutput_DoesNotRewriteTheFiles()
    {
        var dir = NewToolDirectory();
        CheckoutBuildBarrier.Ensure(dir);
        var stamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var files = Directory.GetFiles(dir);
        foreach (var file in files)
        {
            File.SetLastWriteTimeUtc(file, stamp);
        }

        CheckoutBuildBarrier.Ensure(dir);

        foreach (var file in files)
        {
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
        }
    }

    [Fact]
    public void Ensure_StaleContentFromAnEarlierVersion_IsReplaced()
    {
        // Unlike .gitignore, whose content is fixed forever, barrier content can change between
        // versions — "already there, leave it" would strand a checkout on a stale copy.
        var dir = NewToolDirectory();
        var propsPath = Path.Combine(dir, "Directory.Packages.props");
        File.WriteAllText(propsPath, "<Project />");

        CheckoutBuildBarrier.Ensure(dir);

        var document = XDocument.Load(propsPath);
        Assert.Equal(
            "false",
            document.Root!.Elements("PropertyGroup").Elements("ManagePackageVersionsCentrally").Single().Value);
    }

    [Theory]
    [InlineData("Directory.Build.props")]
    [InlineData("Directory.Build.targets")]
    [InlineData("Directory.Packages.props")]
    [InlineData("nuget.config")]
    [InlineData(".editorconfig")]
    [InlineData("global.json")]
    public void Ensure_StaleContentInAnyOfTheSix_IsReplaced(string name)
    {
        // Every file, not just one: the two that build their banner through a comment marker
        // (.editorconfig and global.json) are where a content bug is likeliest, and were the two
        // the single-file test above did not reach.
        var dir = NewToolDirectory();
        CheckoutBuildBarrier.Ensure(dir);
        var path = Path.Combine(dir, name);
        var current = File.ReadAllText(path);
        File.WriteAllText(path, "stale");

        CheckoutBuildBarrier.Ensure(dir);

        Assert.Equal(current, File.ReadAllText(path));
    }

    [Fact]
    public void Ensure_LeavesTheSixBarriersAndNothingElse()
    {
        // The scratch file each write renames into place is deleted in a finally, so a leftover
        // here means a write path that loses one. Asserted by exact set rather than by presence,
        // because that is the only way a leak shows up.
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir);

        Assert.Equal(
            [
                ".editorconfig",
                "Directory.Build.props",
                "Directory.Build.targets",
                "Directory.Packages.props",
                "global.json",
                "nuget.config",
            ],
            Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Ensure_AbandonedScratchFileFromAKilledRun_IsSweptOnceItIsOldEnough()
    {
        // WriteIfDifferent removes its own scratch in a finally, which a killed process never runs.
        // Nothing else in the tree would ever remove one.
        var dir = NewToolDirectory();
        var abandoned = Path.Combine(dir, ".incoming-global.json-deadbeef");
        File.WriteAllText(abandoned, "half a file");
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - TimeSpan.FromDays(2));

        CheckoutBuildBarrier.Ensure(dir);

        Assert.False(File.Exists(abandoned));
    }

    [Fact]
    public void Ensure_ScratchFileFromAConcurrentRun_IsLeftAlone()
    {
        // A scratch file being written right now belongs to another AppHost mid-rename; sweeping it
        // would delete the content it is about to move into place.
        var dir = NewToolDirectory();
        var inFlight = Path.Combine(dir, ".incoming-global.json-deadbeef");
        File.WriteAllText(inFlight, "half a file");

        CheckoutBuildBarrier.Ensure(dir);

        Assert.True(File.Exists(inFlight));
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Enabled_ReadsTheEnvironmentVariable(string? value, bool expected) =>
        Assert.Equal(expected, CheckoutBuildBarrier.Enabled(value));

    [Fact]
    public void Ensure_KeepPackageSourceMappingRequested_WritesTheOtherFiveButNotTheNuGetConfig()
    {
        // The mapping barrier is the one that costs a supply-chain control, so a host that would
        // rather keep the control can turn off that file alone.
        var dir = NewToolDirectory();

        CheckoutBuildBarrier.Ensure(dir, KeepPackageSourceMapping);

        Assert.False(File.Exists(Path.Combine(dir, "nuget.config")));
        Assert.True(File.Exists(Path.Combine(dir, "Directory.Build.props")));
        Assert.True(File.Exists(Path.Combine(dir, "Directory.Build.targets")));
        Assert.True(File.Exists(Path.Combine(dir, "Directory.Packages.props")));
        Assert.True(File.Exists(Path.Combine(dir, ".editorconfig")));
        Assert.True(File.Exists(Path.Combine(dir, "global.json")));
    }

    [Fact]
    public void Ensure_KeepPackageSourceMappingRequestedAfterAnEarlierRunWroteIt_RemovesTheBarrier()
    {
        // Set after the AppHost has already run once, the switch has to undo what that run wrote:
        // a file left on disk goes on clearing the mapping regardless of the switch.
        var dir = NewToolDirectory();
        CheckoutBuildBarrier.Ensure(dir);

        CheckoutBuildBarrier.Ensure(dir, KeepPackageSourceMapping);

        Assert.False(File.Exists(Path.Combine(dir, "nuget.config")));
    }

    [Fact]
    public void Ensure_KeepPackageSourceMappingRequested_LeavesAHandWrittenNuGetConfigAlone()
    {
        // The directory is tool-owned, but content this tool did not write was put there by someone
        // who meant it. Removing the barrier must not become a way to delete that.
        var dir = NewToolDirectory();
        var path = Path.Combine(dir, "nuget.config");
        File.WriteAllText(path, "<configuration />");

        CheckoutBuildBarrier.Ensure(dir, KeepPackageSourceMapping);

        Assert.Equal("<configuration />", File.ReadAllText(path));
    }

    private static string? KeepPackageSourceMapping(string variable) =>
        variable == CheckoutBuildBarrier.KeepPackageSourceMappingEnvironmentVariable ? "1" : null;

    [Fact]
    public void Ensure_ConcurrentCalls_AllLeaveCompleteAndReadableFiles()
    {
        var dir = NewToolDirectory();

        Parallel.For(0, 16, _ => CheckoutBuildBarrier.Ensure(dir));

        foreach (var name in new[]
                 {
                     "Directory.Build.props",
                     "Directory.Build.targets",
                     "Directory.Packages.props",
                     "nuget.config",
                 })
        {
            var document = XDocument.Load(Path.Combine(dir, name));
            Assert.NotNull(document.Root);
        }

        using var globalJson = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(dir, "global.json")),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        Assert.Equal(JsonValueKind.Object, globalJson.RootElement.ValueKind);
        Assert.Contains("root = true", File.ReadAllText(Path.Combine(dir, ".editorconfig")));

        // Sixteen racing writers each create a uniquely named scratch file; every one of them has
        // to be gone, or the race leaks a file per losing write.
        Assert.Empty(Directory.GetFiles(dir, ".incoming-*"));
    }
}
