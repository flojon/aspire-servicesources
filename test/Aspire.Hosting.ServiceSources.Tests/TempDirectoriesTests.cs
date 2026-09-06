using System.Diagnostics;

namespace Aspire.Hosting.ServiceSources.Tests;

public class TempDirectoriesTests
{
    [Fact]
    public void CreateSubdirectory_ReturnsARealDirectoryUnderTheCommonRoot()
    {
        var directory = TempDirectories.CreateSubdirectory();

        Assert.True(Directory.Exists(directory.FullName));
        Assert.StartsWith(TempDirectories.CommonRoot, directory.FullName, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSubdirectory_NestsUnderThisProcessOwnSubfolder()
    {
        var directory = TempDirectories.CreateSubdirectory();

        Assert.StartsWith(TempDirectories.ProcessRoot, directory.FullName, StringComparison.Ordinal);
        Assert.NotEqual(TempDirectories.ProcessRoot, directory.FullName);
    }

    [Fact]
    public void CreateSubdirectory_HonoursAPrefix()
    {
        var directory = TempDirectories.CreateSubdirectory("my-prefix-");

        Assert.StartsWith("my-prefix-", Path.GetFileName(directory.FullName), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateSubdirectory_TwoCallsNeverCollide()
    {
        var first = TempDirectories.CreateSubdirectory();
        var second = TempDirectories.CreateSubdirectory();

        Assert.NotEqual(first.FullName, second.FullName);
    }

    [Fact]
    public void SweepOrphanedProcessRoots_RemovesASubfolderWhoseOwningProcessHasExited()
    {
        var deadProcessId = FindAnExitedProcessId();
        var orphan = Directory.CreateDirectory(
            Path.Combine(TempDirectories.CommonRoot, $"{deadProcessId}-orphan-marker"));
        File.WriteAllText(Path.Combine(orphan.FullName, "leftover.txt"), "");

        TempDirectories.SweepOrphanedProcessRoots();

        Assert.False(Directory.Exists(orphan.FullName));
    }

    [Fact]
    public void SweepOrphanedProcessRoots_LeavesThisProcessOwnSubfolderAlone()
    {
        TempDirectories.CreateSubdirectory();

        TempDirectories.SweepOrphanedProcessRoots();

        Assert.True(Directory.Exists(TempDirectories.ProcessRoot));
    }

    /// <summary>
    /// A process id guaranteed not to belong to a running process: starts a real process and
    /// waits for it to exit, so the id is real but stale rather than merely a large guess.
    /// </summary>
    private static int FindAnExitedProcessId()
    {
        using var process = Process.Start(new ProcessStartInfo(
            OperatingSystem.IsWindows() ? "cmd.exe" : "true",
            OperatingSystem.IsWindows() ? "/c exit 0" : "")
        {
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        return process.Id;
    }
}
