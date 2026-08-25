using System.Diagnostics;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

/// <summary>
/// Covers the MSBuild check the package ships in <c>buildTransitive/</c>, which fails a consumer's
/// build when Aspire.Hosting and Aspire.Hosting.JavaScript resolve to versions that could still
/// reproduce #89 - not any two different versions.
///
/// Nothing else in this repo exercises that file: it reaches a project only through the packed
/// package, and every project here references the satellite by ProjectReference instead. So these
/// tests drive it the way MSBuild does — a real <c>dotnet msbuild</c> invocation over a generated
/// project that imports the real file and declares the item the check reads.
///
/// The items are fabricated rather than restored. What the check needs is
/// ResolvedCompileFileDefinitions carrying NuGetPackageId/NuGetPackageVersion, and writing those
/// directly is what lets a test pin an Aspire pairing — including one nobody has published — without
/// a network restore.
/// </summary>
public sealed class AspireFamilyVersionCheckTests
{
    private const string ErrorCode = "KOALASS001";

    /// <summary>
    /// The file as it ships, copied next to the test assembly by the test project so this asserts
    /// against the real thing rather than a second copy that could drift.
    /// </summary>
    private static string TargetsPath =>
        Path.Combine(AppContext.BaseDirectory, "PackagedTargets", "KoalaSoft.Aspire.Hosting.ServiceSources.JavaScript.targets");

    [Fact]
    public void MatchedVersionsAboveBoundaryBuild()
    {
        var result = RunCheck(hosting: "13.5.2", javaScript: "13.5.2");

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public void MatchedVersionsBelowBoundaryBuild()
    {
        // The pairing this repo shipped before #89: identical versions, both older than the
        // fix. Matched versions of one release are inherently safe regardless of whether that
        // release predates the friend-assembly fix - IVT grants and needs it symmetrically for
        // a single version, so there is no mismatch for the check to react to.
        var result = RunCheck(hosting: "13.4.6", javaScript: "13.4.6");

        Assert.True(result.ExitCode == 0, result.Output);
    }

    /// <summary>
    /// The behavior that distinguishes this check from a strict equality check: two different
    /// versions, both at or above the boundary, do not reproduce #89 because neither package
    /// reaches into the other's internals any more. Forcing this to fail would block upgrades
    /// this repository has no evidence are unsafe.
    /// </summary>
    [Fact]
    public void MismatchedVersionsBothAboveBoundaryBuild()
    {
        var result = RunCheck(hosting: "13.6.0", javaScript: "13.5.2");

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public void MismatchAcrossTheBoundaryFailsNamingBoth()
    {
        var result = RunCheck(hosting: "13.5.2", javaScript: "13.4.6");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(ErrorCode, result.Output);
        Assert.Contains("13.5.2", result.Output);
        Assert.Contains("13.4.6", result.Output);
    }

    /// <summary>
    /// The failure the check names has to point at whichever package is actually below the
    /// boundary. A version-agnostic "reference Aspire.Hosting.JavaScript at $(Hosting)" message
    /// would tell a consumer to downgrade JavaScript to match a low Hosting here, which is wrong.
    /// </summary>
    [Fact]
    public void MismatchAcrossTheBoundaryNamesHostingWhenHostingIsTheLaggingSide()
    {
        var result = RunCheck(hosting: "13.4.6", javaScript: "13.5.2");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Aspire.Hosting 13.4.6 is the side below", result.Output);
    }

    [Fact]
    public void MismatchAcrossTheBoundaryNamesJavaScriptWhenJavaScriptIsTheLaggingSide()
    {
        var result = RunCheck(hosting: "13.5.2", javaScript: "13.4.6");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Aspire.Hosting.JavaScript 13.4.6 is the side below", result.Output);
    }

    [Fact]
    public void MismatchWithBothBelowBoundaryFails()
    {
        // Neither side has upstream's fix, so there is no evidence this pairing is safe - the
        // check has to treat it as risk even though it is not the exact pairing #89 measured.
        var result = RunCheck(hosting: "13.4.6", javaScript: "13.4.0");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(ErrorCode, result.Output);
    }

    [Fact]
    public void OptOutBuildsDespiteMismatch()
    {
        var result = RunCheck(hosting: "13.5.2", javaScript: "13.4.6", skipCheck: true);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    /// <summary>
    /// A project that resolves only one of the two has no pair to compare, and must not be failed
    /// on a version the check never saw.
    /// </summary>
    [Theory]
    [InlineData("13.6.0", null)]
    [InlineData(null, "13.5.2")]
    [InlineData(null, null)]
    public void IncompletePairIsLeftAlone(string? hosting, string? javaScript)
    {
        var result = RunCheck(hosting, javaScript);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    private static (int ExitCode, string Output) RunCheck(string? hosting, string? javaScript, bool skipCheck = false)
    {
        // Outside the repository on purpose: a project under it would import Directory.Build.props
        // and build something quite unlike the consumer project this stands in for.
        var directory = Directory.CreateTempSubdirectory("servicesources-aspire-family-check");

        try
        {
            var project = Path.Combine(directory.FullName, "probe.proj");
            File.WriteAllText(project, ProbeProject(hosting, javaScript));

            var startInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("msbuild");
            startInfo.ArgumentList.Add(project);
            startInfo.ArgumentList.Add("-t:VerifyAspireFamilyVersionsMatch");
            startInfo.ArgumentList.Add("-nologo");
            startInfo.ArgumentList.Add("-v:m");

            if (skipCheck)
            {
                startInfo.ArgumentList.Add("-p:SkipAspireFamilyVersionCheck=true");
            }

            using var process = Process.Start(startInfo)!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, output);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>
    /// A project carrying nothing but the import and the resolved-assembly items. Aspire.Hosting
    /// contributes two assemblies so the check's Distinct() is exercised: without it the same
    /// version arriving twice would read as a two-item list and never compare equal to the other
    /// package's single version, failing every matched pair.
    /// </summary>
    private static string ProbeProject(string? hosting, string? javaScript)
    {
        var items = new List<string>();

        if (hosting is not null)
        {
            items.Add(Item("Aspire.Hosting.dll", "Aspire.Hosting", hosting));
            items.Add(Item("Aspire.Hosting.Extra.dll", "Aspire.Hosting", hosting));
        }

        if (javaScript is not null)
        {
            items.Add(Item("Aspire.Hosting.JavaScript.dll", "Aspire.Hosting.JavaScript", javaScript));
        }

        // An unrelated package, so a check that ignored NuGetPackageId could not pass by accident.
        items.Add(Item("Unrelated.dll", "Some.Other.Package", "1.0.0"));

        return $"""
            <Project>
              <Import Project="{TargetsPath}" />
              <ItemGroup>
            {string.Join(Environment.NewLine, items)}
              </ItemGroup>
            </Project>
            """;

        static string Item(string file, string packageId, string version) =>
            $"""
                 <ResolvedCompileFileDefinitions Include="{file}">
                   <NuGetPackageId>{packageId}</NuGetPackageId>
                   <NuGetPackageVersion>{version}</NuGetPackageVersion>
                 </ResolvedCompileFileDefinitions>
             """;
    }
}
