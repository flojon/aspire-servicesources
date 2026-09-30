using System.ComponentModel;
using System.Diagnostics;
using Aspire.Hosting.ServiceSources.Sources;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

[Trait("IO", "true")]
public class ProcessBuildRunnerTests
{
    private static ProcessStartInfo Sleeper() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("ping") { ArgumentList = { "-n", "60", "127.0.0.1" } }
        : new ProcessStartInfo("sleep") { ArgumentList = { "60" } };

    [Fact]
    public void CreateStartInfo_IsDotnetBuildWithAnArgumentListAndNoShell()
    {
        var project = Path.Combine(TempDirectories.CreateSubdirectory().FullName, "Api", "Api.csproj");

        var info = ProcessBuildRunner.CreateStartInfo(project, null);

        Assert.Equal("dotnet", info.FileName);
        Assert.Equal(["build", project], info.ArgumentList);
        Assert.False(info.UseShellExecute);
        Assert.Equal(Path.GetDirectoryName(project), info.WorkingDirectory);
    }

    [Fact]
    public void CreateStartInfo_PassesTheConfigurationOnlyWhenSet()
    {
        var info = ProcessBuildRunner.CreateStartInfo("/x/Api.csproj", "Release");

        Assert.Equal(["build", "/x/Api.csproj", "--configuration", "Release"], info.ArgumentList);
        Assert.DoesNotContain("--configuration", ProcessBuildRunner.CreateStartInfo("/x/Api.csproj", "").ArgumentList);
    }

    [Fact]
    public async Task RunAsync_OutputAboveTheCap_IsElidedWithAMarker_AndTheExitCodeReturned()
    {
        var runner = new ProcessBuildRunner((_, _) => new ProcessStartInfo("dotnet") { ArgumentList = { "--info" } }, headLines: 2, tailLines: 2);
        var lines = new List<string>();

        var exit = await runner.RunAsync("ignored", null, lines.Add, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Equal(5, lines.Count);
        Assert.Contains("lines elided", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_Cancelled_KillsTheChildAndThrows()
    {
        var runner = new ProcessBuildRunner((_, _) => Sleeper());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync("ignored", null, _ => { }, cts.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), "cancellation did not stop the child promptly");
    }

    [Fact]
    public async Task RunAsync_MissingExecutable_SurfacesAsAnException()
    {
        var runner = new ProcessBuildRunner((_, _) => new ProcessStartInfo("definitely-not-a-real-program-397"));

        await Assert.ThrowsAsync<Win32Exception>(() => runner.RunAsync("ignored", null, _ => { }, CancellationToken.None));
    }
}