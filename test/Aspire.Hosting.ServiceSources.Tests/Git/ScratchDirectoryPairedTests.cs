using Aspire.Hosting.ServiceSources.Git;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

[Trait("IO", "true")]
public class ScratchDirectoryPairedTests
{
    private sealed record Setup(string CheckoutsRoot, string GitRoot, string FinalPath)
    {
        public SeparateGit Separate => new(GitRoot, FinalPath);
    }

    private static Setup NewSetup()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var checkoutsRoot = Path.Combine(temp, "checkouts");
        Directory.CreateDirectory(checkoutsRoot);
        return new Setup(checkoutsRoot, Path.Combine(temp, "gitroot"), Path.Combine(checkoutsRoot, "orders"));
    }

    private static string Id(ScratchDirectory scratch) => Path.GetFileName(scratch.Path)[".incoming-".Length..];

    private static void Populate(string gitDirectory)
    {
        var pack = Path.Combine(gitDirectory, "objects", "pack");
        Directory.CreateDirectory(pack);
        var file = Path.Combine(pack, "pack-1.idx");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
    }

    [Fact]
    public void Pair_NamesGitDirFromHashAndId()
    {
        var setup = NewSetup();
        using var scratch = ScratchDirectory.Create(setup.CheckoutsRoot);

        scratch.PairGitDirectory(setup.Separate);

        Assert.Equal(
            Path.Combine(setup.GitRoot, ManagedGitDirectory.NameFor(setup.FinalPath, Id(scratch))),
            scratch.GitDirectory);
    }

    [Fact]
    public void Pair_HoldsGitRootLockUntilDispose()
    {
        var setup = NewSetup();
        var scratch = ScratchDirectory.Create(setup.CheckoutsRoot);
        scratch.PairGitDirectory(setup.Separate);
        var lockPath = scratch.GitDirectory + ".lock";

        Assert.Throws<IOException>(
            () => new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1));

        scratch.Dispose();
        Assert.False(File.Exists(lockPath));
    }

    [Fact]
    public void Dispose_RemovesGitDirectoryAndBothLocks()
    {
        var setup = NewSetup();
        var scratch = ScratchDirectory.Create(setup.CheckoutsRoot);
        scratch.PairGitDirectory(setup.Separate);
        Populate(scratch.GitDirectory!);

        scratch.Dispose();

        Assert.Empty(Directory.GetFileSystemEntries(setup.GitRoot));
        Assert.Empty(Directory.GetFileSystemEntries(setup.CheckoutsRoot));
    }

    [Fact]
    public void Dispose_AfterAdopt_KeepsGitDirectoryRemovesItsLock()
    {
        var setup = NewSetup();
        var scratch = ScratchDirectory.Create(setup.CheckoutsRoot);
        scratch.PairGitDirectory(setup.Separate);
        Populate(scratch.GitDirectory!);
        var gitDirectory = scratch.GitDirectory!;

        scratch.AdoptGitDirectory();
        scratch.Dispose();

        Assert.True(Directory.Exists(gitDirectory));
        Assert.Equal([gitDirectory], Directory.GetFileSystemEntries(setup.GitRoot));
    }

    [Fact]
    public void SweepAbandoned_RemovesPairedGitDirectoryAndLock_WhenStagingLockIsFree()
    {
        var setup = NewSetup();
        var staging = Path.Combine(setup.CheckoutsRoot, ".incoming-aaaaaaaa");
        Directory.CreateDirectory(staging);
        File.WriteAllText(staging + ".lock", "");
        var gitDirectory = Path.Combine(setup.GitRoot, ManagedGitDirectory.NameFor(setup.FinalPath, "aaaaaaaa"));
        Populate(gitDirectory);
        File.WriteAllText(gitDirectory + ".lock", "");

        ScratchDirectory.SweepAbandoned(setup.CheckoutsRoot, gitRoot: setup.GitRoot);

        Assert.Empty(Directory.GetFileSystemEntries(setup.CheckoutsRoot));
        Assert.Empty(Directory.GetFileSystemEntries(setup.GitRoot));
    }

    [Fact]
    public void SweepAbandoned_KeepsGitDirectory_WhenOnlyOrphanStagingLockExists()
    {
        var setup = NewSetup();
        File.WriteAllText(Path.Combine(setup.CheckoutsRoot, ".incoming-aaaaaaaa.lock"), "");
        var gitDirectory = Path.Combine(setup.GitRoot, ManagedGitDirectory.NameFor(setup.FinalPath, "aaaaaaaa"));
        Populate(gitDirectory);

        ScratchDirectory.SweepAbandoned(setup.CheckoutsRoot, gitRoot: setup.GitRoot);

        Assert.True(Directory.Exists(gitDirectory));
    }

    [Fact]
    public void SweepAbandoned_LeavesPairedGitDirectory_WhenStagingLockIsHeld()
    {
        var setup = NewSetup();
        using var live = ScratchDirectory.Create(setup.CheckoutsRoot);
        live.PairGitDirectory(setup.Separate);
        Directory.CreateDirectory(live.Path);
        Populate(live.GitDirectory!);
        Directory.SetLastWriteTimeUtc(live.Path, DateTime.UtcNow.AddDays(-30));

        ScratchDirectory.SweepAbandoned(setup.CheckoutsRoot, gitRoot: setup.GitRoot);

        Assert.True(Directory.Exists(live.Path));
        Assert.True(Directory.Exists(live.GitDirectory));
    }

    [Fact]
    public void Create_NewScratchAfterDispose_HasNewIdAndGitDirectoryName()
    {
        var setup = NewSetup();
        var first = ScratchDirectory.Create(setup.CheckoutsRoot);
        first.PairGitDirectory(setup.Separate);
        var firstGit = first.GitDirectory;
        first.Dispose();

        using var second = ScratchDirectory.Create(setup.CheckoutsRoot);
        second.PairGitDirectory(setup.Separate);

        Assert.NotEqual(firstGit, second.GitDirectory);
    }

    [Fact]
    public void Pair_GitLockCollision_ReleasesStagingLockAndRetriesWithNewId()
    {
        var setup = NewSetup();
        Directory.CreateDirectory(setup.GitRoot);
        File.WriteAllText(
            Path.Combine(setup.GitRoot, ManagedGitDirectory.NameFor(setup.FinalPath, "aaaaaaaa")) + ".lock", "");
        var ids = new Queue<string>(["aaaaaaaa", "bbbbbbbb"]);

        using var scratch = ScratchDirectory.Create(setup.CheckoutsRoot, ids.Dequeue);
        scratch.PairGitDirectory(setup.Separate, ids.Dequeue);

        Assert.Equal(Path.Combine(setup.CheckoutsRoot, ".incoming-bbbbbbbb"), scratch.Path);
        Assert.EndsWith("-bbbbbbbb.git", scratch.GitDirectory, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(setup.CheckoutsRoot, ".incoming-aaaaaaaa.lock")));
    }
}

[Trait("IO", "true")]
[Trait("Concurrency", "true")]
public class ScratchDirectoryPairedConcurrencyTests
{
    [Fact]
    public async Task TwoCreates_SameFinalPath_GetDistinctGitDirectories()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var checkoutsRoot = Path.Combine(temp, "checkouts");
        Directory.CreateDirectory(checkoutsRoot);
        var separate = new SeparateGit(Path.Combine(temp, "gitroot"), Path.Combine(checkoutsRoot, "orders"));
        using var barrier = new Barrier(2);

        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            var scratch = ScratchDirectory.Create(checkoutsRoot);
            scratch.PairGitDirectory(separate);
            return scratch;
        })).ToArray();
        var scratches = await Task.WhenAll(tasks);

        try
        {
            Assert.NotEqual(scratches[0].GitDirectory, scratches[1].GitDirectory);
        }
        finally
        {
            foreach (var scratch in scratches)
            {
                scratch.Dispose();
            }
        }
    }
}
