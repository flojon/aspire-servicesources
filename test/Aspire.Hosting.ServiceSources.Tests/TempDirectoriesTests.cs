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
    public void SweepOrphanedProcessRoots_RemovesASubfolderWhoseOwningProcessIsNotRunning()
    {
        // Larger than any platform's real process id ceiling (Linux's own hard limit is
        // 2^22), so this is deterministically "not running" without guessing a small number
        // that could coincidentally collide with a live process. Positive, and not fewer than
        // two digits, so it can't be confused with the leading "-" of a negative id (which
        // collides with the "-" used to separate the id from the rest of the folder name) or
        // with a single-digit id short enough to matter.
        const int NoSuchProcessId = 999_999_999;
        var orphan = Directory.CreateDirectory(
            Path.Combine(TempDirectories.CommonRoot, $"{NoSuchProcessId}-orphan-marker"));
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
}
