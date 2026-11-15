using Aspire.Hosting.ServiceSources.Git;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

[Trait("IO", "true")]
public class SeparateGitDirectoriesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Old = Now - SeparateGitDirectories.Retention - TimeSpan.FromDays(1);
    private static readonly DateTimeOffset Recent = Now - TimeSpan.FromDays(1);

    private sealed record World(string Temp, string GitRoot)
    {
        public string NewCheckout(string name = "orders")
        {
            var checkout = Path.Combine(Temp, "app", "checkouts", name);
            Directory.CreateDirectory(checkout);
            return checkout;
        }

        // Id is varied so several git directories for one checkout can coexist.
        public string NewGitDirectory(string checkout, string id, DateTimeOffset? lastUsed, string? recordedCheckout = null)
        {
            var gitDirectory = Path.Combine(GitRoot, ManagedGitDirectory.NameFor(checkout, id));
            var pack = Path.Combine(gitDirectory, "objects", "pack");
            Directory.CreateDirectory(pack);
            var file = Path.Combine(pack, "pack-1.idx");
            File.WriteAllText(file, "x");
            File.SetAttributes(file, FileAttributes.ReadOnly);
            if (lastUsed is { } used)
            {
                ManagedGitDirectory.WriteOwner(gitDirectory, new OwnerRecord(recordedCheckout ?? checkout, used, used));
            }

            return gitDirectory;
        }

        public static void Link(string checkout, string gitDirectory) =>
            File.WriteAllText(Path.Combine(checkout, ".git"), $"gitdir: {gitDirectory.Replace('\\', '/')}\n");
    }

    private static World NewWorld()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var gitRoot = Path.Combine(temp, "gitroot");
        Directory.CreateDirectory(gitRoot);
        return new World(temp, gitRoot);
    }

    private static void SetWriteTime(string directory, DateTimeOffset when) =>
        Directory.SetLastWriteTimeUtc(directory, when.UtcDateTime);

    [Fact]
    public void Sweep_LiveGitDirectory_IsKept()
    {
        var world = NewWorld();
        var checkout = world.NewCheckout();
        var git = world.NewGitDirectory(checkout, "aaaaaaaa", Old);
        World.Link(checkout, git);

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.True(Directory.Exists(git));
    }

    [Fact]
    public void Sweep_RecordedCheckoutNowHasAnInPlaceGitDir_IsNotLive()
    {
        var world = NewWorld();
        var checkout = world.NewCheckout();
        var git = world.NewGitDirectory(checkout, "aaaaaaaa", Old);
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.False(Directory.Exists(git));
    }

    [Fact]
    public void Sweep_RecordNamesMovedAwayCheckout_AgesOutAfterRetention()
    {
        var world = NewWorld();
        var movedTo = world.NewCheckout("renamed");
        var oldPlace = Path.Combine(world.Temp, "app", "checkouts", "orders");
        var git = world.NewGitDirectory(oldPlace, "aaaaaaaa", Recent);
        World.Link(movedTo, git);

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);
        Assert.True(Directory.Exists(git));

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now + TimeSpan.FromDays(30));
        Assert.False(Directory.Exists(git));
    }

    [Fact]
    public void Sweep_RecordWrittenButNeverMoved_IsKeptUntilRetention_ThenCollected()
    {
        var world = NewWorld();
        var checkout = world.NewCheckout();
        var git = world.NewGitDirectory(checkout, "aaaaaaaa", Now);

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now + TimeSpan.FromDays(1));
        Assert.True(Directory.Exists(git));

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now + SeparateGitDirectories.Retention + TimeSpan.FromHours(1));
        Assert.False(Directory.Exists(git));
    }

    [Fact]
    public void Sweep_StaleLock_RemovedWithItsDirectory()
    {
        var world = NewWorld();
        var checkout = world.NewCheckout();
        var git = world.NewGitDirectory(checkout, "aaaaaaaa", Old);
        File.WriteAllText(git + ".lock", "");

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.Empty(Directory.GetFileSystemEntries(world.GitRoot));
    }

    [Fact]
    public void Sweep_RecordedDriveRootMissing_IsKept()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var missingDrive = Enumerable.Range('Z' - 5, 6).Select(c => (char)c)
            .Select(c => $"{c}:\\")
            .FirstOrDefault(root => !Directory.Exists(root));
        if (missingDrive is null)
        {
            return;
        }

        var world = NewWorld();
        var git = world.NewGitDirectory(Path.Combine(missingDrive, "app", "checkouts", "orders"), "aaaaaaaa", Old);

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.True(Directory.Exists(git));
    }

    [Theory]
    [InlineData(@"\\no-such-host-for-servicesources\share\orders")]
    [InlineData(@"relative\orders")]
    public void Sweep_RecordedPathUncOrRelative_IsKept_AndNeverProbed(string recorded)
    {
        var world = NewWorld();
        var checkout = world.NewCheckout();
        var git = world.NewGitDirectory(checkout, "aaaaaaaa", Old, recordedCheckout: recorded);

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.True(Directory.Exists(git));
    }

    [Fact]
    public void Sweep_ParsableRecordWithinRetention_IsKept()
    {
        var world = NewWorld();
        var git = world.NewGitDirectory(world.NewCheckout(), "aaaaaaaa", Recent);

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.True(Directory.Exists(git));
    }

    [Fact]
    public void Sweep_ParsableRecordOlderThanRetention_IsDeleted_AndLogged()
    {
        var world = NewWorld();
        var git = world.NewGitDirectory(world.NewCheckout(), "aaaaaaaa", Old);
        var log = new RecordingProgressSink();

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now, log);

        Assert.False(Directory.Exists(git));
        var line = Assert.Single(log.Lines);
        Assert.Contains(git, line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unreferenced for more than 14 days", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Sweep_NoRecord_UsesDirectoryWriteTime()
    {
        var world = NewWorld();
        var checkout = world.NewCheckout();
        var recent = world.NewGitDirectory(checkout, "aaaaaaaa", lastUsed: null);
        var old = world.NewGitDirectory(checkout, "bbbbbbbb", lastUsed: null);
        SetWriteTime(recent, Recent);
        SetWriteTime(old, Old);

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.True(Directory.Exists(recent));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void Sweep_MalformedRecord_UsesDirectoryWriteTime()
    {
        var world = NewWorld();
        var checkout = world.NewCheckout();
        var recent = world.NewGitDirectory(checkout, "aaaaaaaa", Old);
        var old = world.NewGitDirectory(checkout, "bbbbbbbb", Recent);
        File.WriteAllText(Path.Combine(recent, ManagedGitDirectory.OwnerFileName), "{ nope");
        File.WriteAllText(Path.Combine(old, ManagedGitDirectory.OwnerFileName), "{ nope");
        SetWriteTime(recent, Recent);
        SetWriteTime(old, Old);

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.True(Directory.Exists(recent));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void Sweep_NamedNotMatchingPattern_IsKept()
    {
        var world = NewWorld();
        var stranger = Path.Combine(world.GitRoot, "somebody-elses-directory");
        Directory.CreateDirectory(stranger);
        SetWriteTime(stranger, Old);

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.True(Directory.Exists(stranger));
    }

    [Fact]
    public void Sweep_ReparsePointChild_IsKept_TargetUntouched()
    {
        var world = NewWorld();
        var checkout = world.NewCheckout();
        var target = world.NewGitDirectory(checkout, "aaaaaaaa", Old);
        var linkName = Path.Combine(world.GitRoot, ManagedGitDirectory.NameFor(checkout, "bbbbbbbb"));
        var realElsewhere = Path.Combine(world.Temp, "elsewhere");
        Directory.Move(target, realElsewhere);
        try
        {
            Directory.CreateSymbolicLink(linkName, realElsewhere);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;
        }

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.True(Directory.Exists(realElsewhere));
        Assert.True(File.Exists(Path.Combine(realElsewhere, ManagedGitDirectory.OwnerFileName)));
    }

    [Fact]
    public void Sweep_ReadOnlyPackFiles_StillDeleted()
    {
        var world = NewWorld();
        var git = world.NewGitDirectory(world.NewCheckout(), "aaaaaaaa", Old);

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.False(Directory.Exists(git));
    }

    [Fact]
    public void Sweep_NeverDeletesAPathNamedInTheRecord()
    {
        var world = NewWorld();
        var bystander = Path.Combine(world.Temp, "bystander");
        Directory.CreateDirectory(bystander);
        File.WriteAllText(Path.Combine(bystander, "precious.txt"), "x");
        var git = world.NewGitDirectory(world.NewCheckout(), "aaaaaaaa", Old, recordedCheckout: bystander);

        SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

        Assert.False(Directory.Exists(git));
        Assert.True(File.Exists(Path.Combine(bystander, "precious.txt")));
    }

    [Fact]
    public void Sweep_IOExceptionOnOneChild_ContinuesWithTheRest()
    {
        var world = NewWorld();
        var checkout = world.NewCheckout();
        var held = world.NewGitDirectory(checkout, "aaaaaaaa", Old);
        var other = world.NewGitDirectory(checkout, "bbbbbbbb", Old);

        if (OperatingSystem.IsWindows())
        {
            using var handle = new FileStream(
                Path.Combine(held, "held.txt"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

            SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);

            Assert.True(Directory.Exists(held));
        }
        else
        {
            SeparateGitDirectories.SweepOrphans(Path.Combine(world.Temp, "no-such-root"), Now);
            SeparateGitDirectories.SweepOrphans(world.GitRoot, Now);
        }

        Assert.False(Directory.Exists(other));
    }

    [Fact]
    public void RefreshLastUsed_RewritesOnlyWhenOverADayOld()
    {
        var world = NewWorld();
        var checkout = world.NewCheckout();
        var fresh = world.NewGitDirectory(checkout, "aaaaaaaa", Now - TimeSpan.FromHours(23));
        var stale = world.NewGitDirectory(checkout, "bbbbbbbb", Now - TimeSpan.FromHours(25));

        SeparateGitDirectories.RefreshLastUsed(fresh, Now);
        SeparateGitDirectories.RefreshLastUsed(stale, Now);

        Assert.Equal(Now - TimeSpan.FromHours(23), ManagedGitDirectory.ReadOwner(fresh)!.LastUsedUtc);
        Assert.Equal(Now, ManagedGitDirectory.ReadOwner(stale)!.LastUsedUtc);
    }

    [Fact]
    public void RefreshLastUsed_LeavesNoTempFileBehind()
    {
        var world = NewWorld();
        var git = world.NewGitDirectory(world.NewCheckout(), "aaaaaaaa", Old);

        SeparateGitDirectories.RefreshLastUsed(git, Now);

        Assert.Equal([ManagedGitDirectory.OwnerFileName], Directory.GetFiles(git).Select(Path.GetFileName));
    }

    [Fact]
    public void RefreshLastUsed_NoRecord_DoesNothing()
    {
        var world = NewWorld();
        var git = world.NewGitDirectory(world.NewCheckout(), "aaaaaaaa", lastUsed: null);

        SeparateGitDirectories.RefreshLastUsed(git, Now);

        Assert.Empty(Directory.GetFiles(git));
    }
}

[Trait("IO", "true")]
[Trait("Concurrency", "true")]
public class SeparateGitDirectoriesLockTests
{
    [Fact]
    public async Task Sweep_HeldLock_IsKept()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var gitRoot = Path.Combine(temp, "gitroot");
        var checkout = Path.Combine(temp, "checkouts", "orders");
        Directory.CreateDirectory(checkout);
        var git = Path.Combine(gitRoot, ManagedGitDirectory.NameFor(checkout, "aaaaaaaa"));
        Directory.CreateDirectory(git);
        var old = DateTimeOffset.UtcNow - TimeSpan.FromDays(60);
        ManagedGitDirectory.WriteOwner(git, new OwnerRecord(checkout, old, old));

        var acquired = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holder = Task.Run(async () =>
        {
            using var stream = new FileStream(git + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1);
            acquired.SetResult();
            await release.Task;
        });
        await acquired.Task;

        try
        {
            SeparateGitDirectories.SweepOrphans(gitRoot, DateTimeOffset.UtcNow);

            Assert.True(Directory.Exists(git));
        }
        finally
        {
            release.SetResult();
            await holder;
        }
    }
}
