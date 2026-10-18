using System.Text.RegularExpressions;
using Aspire.Hosting.ServiceSources.Git;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

[Trait("IO", "true")]
public class ScratchDirectoryTests
{
    private static string NewRoot() => TempDirectories.CreateSubdirectory().FullName;

    private static string Populate(string directory)
    {
        var nested = Path.Combine(directory, ".git", "objects", "pack");
        Directory.CreateDirectory(nested);
        var file = Path.Combine(nested, "pack-1.idx");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        return file;
    }

    [Fact]
    public void Create_NamesTheDirectoryWithEightHexCharactersAndNothingElse()
    {
        var root = NewRoot();

        using var scratch = ScratchDirectory.Create(root);

        Assert.Equal(root, Path.GetDirectoryName(scratch.Path));
        Assert.Matches(new Regex(@"^\.incoming-[0-9a-f]{8}$"), Path.GetFileName(scratch.Path));
    }

    [Fact]
    public void Create_NameAlreadyTaken_RetriesWithAnotherName()
    {
        var root = NewRoot();
        var ids = new Queue<string>(["aaaaaaaa", "aaaaaaaa", "bbbbbbbb"]);

        using var first = ScratchDirectory.Create(root, ids.Dequeue);
        using var second = ScratchDirectory.Create(root, ids.Dequeue);

        Assert.Equal(Path.Combine(root, ".incoming-aaaaaaaa"), first.Path);
        Assert.Equal(Path.Combine(root, ".incoming-bbbbbbbb"), second.Path);
    }

    [Fact]
    public void Create_ExistingDirectoryWithThatName_RetriesWithAnotherName()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, ".incoming-aaaaaaaa"));
        var ids = new Queue<string>(["aaaaaaaa", "cccccccc"]);

        using var scratch = ScratchDirectory.Create(root, ids.Dequeue);

        Assert.Equal(Path.Combine(root, ".incoming-cccccccc"), scratch.Path);
    }

    [Fact]
    public void Dispose_RemovesTheDirectoryEvenWithReadOnlyFiles()
    {
        var root = NewRoot();
        var scratch = ScratchDirectory.Create(root);
        Populate(scratch.Path);

        scratch.Dispose();

        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void Sweep_DirectoryOfALiveClone_IsKeptRegardlessOfAge()
    {
        var root = NewRoot();
        using var live = ScratchDirectory.Create(root);
        Populate(live.Path);
        Directory.SetLastWriteTimeUtc(live.Path, DateTime.UtcNow.AddDays(-30));

        ScratchDirectory.SweepAbandoned(root);

        Assert.True(Directory.Exists(live.Path));
    }

    [Fact]
    public void Sweep_DirectoryWhoseOwnerDied_IsRemovedAtOnceIncludingReadOnlyFiles()
    {
        var root = NewRoot();
        var abandoned = Path.Combine(root, ".incoming-deadbeef");
        Populate(abandoned);
        File.WriteAllText(abandoned + ".lock", "");

        ScratchDirectory.SweepAbandoned(root);

        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void Sweep_UnmarkedDirectoryYoungerThanTheLegacyAge_IsKept()
    {
        var root = NewRoot();
        var legacy = Path.Combine(root, ".incoming-orders-0123456789abcdef0123456789abcdef");
        Populate(legacy);

        ScratchDirectory.SweepAbandoned(root);

        Assert.True(Directory.Exists(legacy));
    }

    [Fact]
    public void Sweep_UnmarkedDirectoryOlderThanTheLegacyAge_IsRemoved()
    {
        var root = NewRoot();
        var legacy = Path.Combine(root, ".incoming-orders-0123456789abcdef0123456789abcdef");
        Populate(legacy);
        Directory.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddHours(-25));

        ScratchDirectory.SweepAbandoned(root);

        Assert.False(Directory.Exists(legacy));
    }

    [Fact]
    public void Sweep_LockFileWithNoDirectory_IsRemoved()
    {
        var root = NewRoot();
        File.WriteAllText(Path.Combine(root, ".incoming-deadbeef.lock"), "");

        ScratchDirectory.SweepAbandoned(root);

        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public void Sweep_LockOfAClaimedNameBeforeItsDirectoryExists_IsKept()
    {
        var root = NewRoot();
        using var claimed = ScratchDirectory.Create(root);

        ScratchDirectory.SweepAbandoned(root);

        Assert.True(File.Exists(claimed.Path + ".lock"));
    }

    [Fact]
    public void Dispose_RemovesTheLockFileToo()
    {
        var root = NewRoot();
        var scratch = ScratchDirectory.Create(root);
        Directory.CreateDirectory(scratch.Path);

        scratch.Dispose();

        Assert.Empty(Directory.GetFileSystemEntries(root));
    }
}
