using System.Diagnostics;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// <c>build/KoalaSoft.Aspire.Hosting.ServiceSources.targets</c> is the only thing that reports a
/// too-old hosting package before an AppHost runs, and it is the one piece of this package that
/// executes in someone else's build rather than ours — so nothing else here covers it. It also has
/// the shape that hides mistakes: a condition that is wrong in the permissive direction produces no
/// error, which is indistinguishable from a version that was fine.
/// </summary>
/// <remarks>
/// Drives the real file with a synthesised <c>ResolvedCompileFileDefinitions</c>, which is how any
/// version can be tested without a package that has it. Needs no restore and no network: the probe
/// project imports the targets, declares the item, and calls the target.
/// </remarks>
public class GuestLanguageFloorTargetsTests
{
    // The floor is 13.5.2 for Aspire.Hosting.JavaScript. Each case is the resolved version an
    // AppHost would have, and whether the gate must reject it.
    [Theory]
    [InlineData("13.4.6", true)]                 // an older release
    [InlineData("13.5.1", true)]                 // the patch below the floor
    [InlineData("13.5.2-preview.1.25", true)]    // a prerelease OF the floor precedes it (SemVer)
    [InlineData("13.5.2", false)]                // exactly the floor
    [InlineData("13.5.3", false)]                // newer
    [InlineData("13.5.3-preview.1.25", false)]   // a prerelease above the floor is still above it
    [InlineData("14.0.0", false)]                // a new major is not this gate's business
    public void TheFloorGate_RejectsExactlyTheVersionsBelowTheFloor(string resolved, bool shouldReject)
    {
        var (exitCode, output) = RunProbe("Aspire.Hosting.JavaScript", resolved);

        if (shouldReject)
        {
            Assert.Contains("SERVICESOURCES001", output, StringComparison.Ordinal);
            Assert.Contains(resolved, output, StringComparison.Ordinal);
            Assert.NotEqual(0, exitCode);
        }
        else
        {
            Assert.DoesNotContain("SERVICESOURCES001", output, StringComparison.Ordinal);
            Assert.Equal(0, exitCode);
        }
    }

    /// <summary>
    /// The java floor is a different number and a different diagnostic code, so it is checked
    /// separately rather than assumed to follow from the javascript one.
    /// </summary>
    [Theory]
    [InlineData("13.2.0", true)]
    [InlineData("13.3.0-beta.1", true)]
    [InlineData("13.3.0", false)]
    [InlineData("13.4.0", false)]
    public void TheJavaFloorGate_UsesItsOwnFloorAndCode(string resolved, bool shouldReject)
    {
        var (exitCode, output) = RunProbe("CommunityToolkit.Aspire.Hosting.Java", resolved);

        if (shouldReject)
        {
            Assert.Contains("SERVICESOURCES002", output, StringComparison.Ordinal);
            Assert.NotEqual(0, exitCode);
        }
        else
        {
            Assert.DoesNotContain("SERVICESOURCES002", output, StringComparison.Ordinal);
            Assert.Equal(0, exitCode);
        }
    }

    /// <summary>
    /// The common case, and the one a check like this most easily gets wrong: an AppHost that
    /// references neither package must build, because it declares no service of either kind.
    /// </summary>
    [Fact]
    public void NoGuestLanguagePackageAtAll_IsAccepted()
    {
        var (exitCode, output) = RunProbe(packageId: null, resolved: null);

        Assert.DoesNotContain("SERVICESOURCES00", output, StringComparison.Ordinal);
        Assert.Equal(0, exitCode);
    }

    private static (int ExitCode, string Output) RunProbe(string? packageId, string? resolved)
    {
        var dir = Directory.CreateTempSubdirectory("floor-gate").FullName;

        try
        {
            var item = packageId is null
                ? ""
                : $"""
                     <ItemGroup>
                       <ResolvedCompileFileDefinitions Include="probe.dll">
                         <NuGetPackageId>{packageId}</NuGetPackageId>
                         <NuGetPackageVersion>{resolved}</NuGetPackageVersion>
                       </ResolvedCompileFileDefinitions>
                     </ItemGroup>
                   """;

            // ResolvePackageAssets is stubbed: the real one needs a restore, and all this file reads
            // is the item that target would have produced.
            var project = $"""
                <Project DefaultTargets="Probe">
                  <Import Project="{TargetsPath()}" />
                  <Target Name="ResolvePackageAssets" />
                  <Target Name="Probe" DependsOnTargets="ResolvePackageAssets;ServiceSourcesVerifyGuestLanguageFloors" />
                {item}
                </Project>
                """;

            var path = Path.Combine(dir, "probe.proj");
            File.WriteAllText(path, project);

            using var process = Process.Start(new ProcessStartInfo("dotnet")
            {
                // nodeReuse off so a persistent worker cannot carry state between cases.
                Arguments = $"msbuild \"{path}\" -nologo -nodeReuse:false -v:m",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();

            return (process.ExitCode, output);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string TargetsPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ServiceSources.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);

        return Path.Combine(
            dir.FullName, "src", "Aspire.Hosting.ServiceSources", "build",
            "KoalaSoft.Aspire.Hosting.ServiceSources.targets");
    }
}
