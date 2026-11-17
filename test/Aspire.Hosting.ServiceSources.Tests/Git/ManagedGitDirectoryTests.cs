using System.Text.RegularExpressions;
using Aspire.Hosting.ServiceSources.Git;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

[Trait("IO", "true")]
public class ManagedGitDirectoryTests
{
    private const string Id = "abcd1234";

    private sealed record Managed(string RepoRoot, string GitRoot, string GitDirectory)
    {
        public string DotGit => Path.Combine(RepoRoot, ".git");

        public void WriteGitFile(string target) => File.WriteAllText(DotGit, $"gitdir: {target}\n");
    }

    private static Managed NewManagedUnder(string parent, string? recordedCheckout = null, bool writeRecord = true)
    {
        var repoRoot = Path.Combine(parent, "app", "checkouts", "orders");
        var gitRoot = Path.Combine(parent, "gitroot");
        Directory.CreateDirectory(repoRoot);

        var gitDirectory = Path.Combine(gitRoot, ManagedGitDirectory.NameFor(repoRoot, Id));
        Directory.CreateDirectory(gitDirectory);
        if (writeRecord)
        {
            ManagedGitDirectory.WriteOwner(
                gitDirectory, new OwnerRecord(recordedCheckout ?? repoRoot, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        var managed = new Managed(repoRoot, gitRoot, gitDirectory);
        managed.WriteGitFile(Forward(gitDirectory));
        return managed;
    }

    private static Managed NewManaged(string? recordedCheckout = null, bool writeRecord = true) =>
        NewManagedUnder(TempDirectories.CreateSubdirectory().FullName, recordedCheckout, writeRecord);

    private static string Forward(string path) => path.Replace('\\', '/');

    private static void AssertRefused(Managed managed, string fragment, string? gitRoot = null)
    {
        var result = ManagedGitDirectory.Resolve(managed.RepoRoot, gitRoot ?? managed.GitRoot);

        Assert.Null(result.GitDirectory);
        Assert.NotNull(result.Refusal);
        Assert.Contains(fragment, result.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    // Symlink creation needs a privilege on Windows; the test is vacuous where it is denied.
    private static bool TryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [Fact]
    public void NameFor_IsHashDashIdDotGit_AndMatchesPattern()
    {
        var name = ManagedGitDirectory.NameFor(Path.Combine(Path.GetTempPath(), "x"), Id);

        Assert.Matches(ManagedGitDirectory.NamePattern, name);
        Assert.EndsWith($"-{Id}.git", name, StringComparison.Ordinal);
        Assert.Equal(29, name.Length);
    }

    [Fact]
    public void HashOf_IgnoresCaseAndTrailingSeparator()
    {
        var path = Path.Combine(Path.GetTempPath(), "Some", "Checkout");

        var hash = ManagedGitDirectory.HashOf(path);

        Assert.Matches(new Regex("^[0-9a-f]{16}$"), hash);
        Assert.Equal(hash, ManagedGitDirectory.HashOf(path.ToUpperInvariant()));
        Assert.Equal(hash, ManagedGitDirectory.HashOf(path + Path.DirectorySeparatorChar));
        Assert.NotEqual(hash, ManagedGitDirectory.HashOf(path + "2"));
    }

    [Fact]
    public void Resolve_InPlaceGitDirectory_ReturnsIt()
    {
        var repoRoot = TempDirectories.CreateSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

        var result = ManagedGitDirectory.Resolve(repoRoot, gitRoot: null);

        Assert.Equal(Path.Combine(repoRoot, ".git"), result.GitDirectory);
        Assert.Null(result.Refusal);
    }

    [Fact]
    public void Resolve_NoGitEntry_ReturnsNeither()
    {
        var repoRoot = TempDirectories.CreateSubdirectory().FullName;

        var result = ManagedGitDirectory.Resolve(repoRoot, "gitroot");

        Assert.Null(result.GitDirectory);
        Assert.Null(result.Refusal);
    }

    [Fact]
    public void Resolve_ValidGitFile_ReturnsTheGitDirectory()
    {
        var managed = NewManaged();

        var result = ManagedGitDirectory.Resolve(managed.RepoRoot, managed.GitRoot);

        Assert.Null(result.Refusal);
        Assert.Equal(managed.GitDirectory, result.GitDirectory);
    }

    [Fact]
    public void Resolve_FileOver4KiB_IsRefused()
    {
        var managed = NewManaged();
        managed.WriteGitFile(Forward(managed.GitDirectory) + new string('/', 5000));

        AssertRefused(managed, "4 KiB");
    }

    [Fact]
    public void Resolve_TwoLines_IsRefused()
    {
        var managed = NewManaged();
        File.WriteAllText(managed.DotGit, $"gitdir: {Forward(managed.GitDirectory)}\nextra\n");

        AssertRefused(managed, "more than one line");
    }

    [Fact]
    public void Resolve_NotGitdirPrefix_IsRefused()
    {
        var managed = NewManaged();
        File.WriteAllText(managed.DotGit, managed.GitDirectory);

        AssertRefused(managed, "does not start with 'gitdir:'");
    }

    [Fact]
    public void Resolve_TargetWithDotDot_IsRefused()
    {
        var managed = NewManaged();
        managed.WriteGitFile(Forward(Path.Combine(managed.GitRoot, "..", "gitroot", Path.GetFileName(managed.GitDirectory))));

        AssertRefused(managed, "'..'");
    }

    [Fact]
    public void Resolve_TargetNotDirectChildOfRoot_IsRefused()
    {
        var managed = NewManaged();
        var nested = Path.Combine(managed.GitRoot, "sub", Path.GetFileName(managed.GitDirectory));
        Directory.CreateDirectory(nested);
        managed.WriteGitFile(Forward(nested));

        AssertRefused(managed, "git root");
    }

    [Fact]
    public void Resolve_NameNotMatchingPattern_IsRefused()
    {
        var managed = NewManaged();
        var other = Path.Combine(managed.GitRoot, "not-a-managed-name.git");
        Directory.CreateDirectory(other);
        managed.WriteGitFile(Forward(other));

        AssertRefused(managed, "name");
    }

    [Fact]
    public void Resolve_TargetMissing_IsRefused()
    {
        var managed = NewManaged();
        Directory.Delete(managed.GitDirectory, recursive: true);

        AssertRefused(managed, "no longer exists");
    }

    [Fact]
    public void Resolve_RecordMissing_IsRefused()
    {
        var managed = NewManaged(writeRecord: false);

        AssertRefused(managed, "no ownership record");
    }

    [Fact]
    public void Resolve_RecordMalformed_IsRefused()
    {
        var managed = NewManaged();
        File.WriteAllText(Path.Combine(managed.GitDirectory, ManagedGitDirectory.OwnerFileName), "{ not json");

        AssertRefused(managed, "cannot be read");
    }

    [Fact]
    public void Resolve_RecordNamesAnotherCheckout_SaysMovedOrRenamed()
    {
        var managed = NewManaged(recordedCheckout: Path.Combine(Path.GetTempPath(), "elsewhere", "orders"));

        var result = ManagedGitDirectory.Resolve(managed.RepoRoot, managed.GitRoot);

        Assert.Null(result.GitDirectory);
        Assert.Contains("moved or renamed", result.Refusal, StringComparison.Ordinal);
        Assert.Contains("delete", result.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-run", result.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_TargetOutsideThisMachinesRoot_NamesBothRoots()
    {
        var managed = NewManaged();
        var otherRoot = Path.Combine(TempDirectories.CreateSubdirectory().FullName, "theirroot");

        var result = ManagedGitDirectory.Resolve(managed.RepoRoot, otherRoot);

        Assert.Null(result.GitDirectory);
        Assert.Contains(otherRoot, result.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(managed.GitRoot, result.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_GitRootIsReparsePoint_IsRefused()
    {
        var managed = NewManaged();
        var real = managed.GitRoot + "-real";
        Directory.Move(managed.GitRoot, real);
        if (!TryLink(managed.GitRoot, real))
        {
            return;
        }

        AssertRefused(managed, "reparse point");
    }

    [Fact]
    public void Resolve_TargetIsReparsePoint_IsRefused()
    {
        var managed = NewManaged();
        var real = managed.GitDirectory + "-real";
        Directory.Move(managed.GitDirectory, real);
        if (!TryLink(managed.GitDirectory, real))
        {
            return;
        }

        AssertRefused(managed, "reparse point");
    }

    [Fact]
    public void Resolve_GitRootAncestorIsReparsePoint_IsRefused()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var realParent = Path.Combine(temp, "real");
        var managed = NewManagedUnder(realParent);
        var linkParent = Path.Combine(temp, "link");
        if (!TryLink(linkParent, realParent))
        {
            return;
        }

        var viaLink = Path.Combine(linkParent, "gitroot");
        managed.WriteGitFile(Forward(Path.Combine(viaLink, Path.GetFileName(managed.GitDirectory))));

        AssertRefused(managed, "reparse point", gitRoot: viaLink);
    }

    [Fact]
    public void Resolve_GitRootOver149_StillResolvesAnExistingCheckout()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var managed = NewManagedUnder(Path.Combine(temp, new string('d', GitPathLimit.MaxGitRootLength)));

        var result = ManagedGitDirectory.Resolve(managed.RepoRoot, managed.GitRoot);

        Assert.True(managed.GitRoot.Length > GitPathLimit.MaxGitRootLength);
        Assert.Equal(managed.GitDirectory, result.GitDirectory);
    }

    [Fact]
    public void Resolve_NullGitRoot_RejectsGitFileNamingTheCause_ButAcceptsGitDirectory()
    {
        var managed = NewManaged();

        foreach (var root in new string?[] { null, "" })
        {
            var result = ManagedGitDirectory.Resolve(managed.RepoRoot, root);

            Assert.Null(result.GitDirectory);
            Assert.Contains("no usable git directory root", result.Refusal, StringComparison.Ordinal);
        }

        var inPlace = TempDirectories.CreateSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(inPlace, ".git"));
        Assert.NotNull(ManagedGitDirectory.Resolve(inPlace, null).GitDirectory);
    }

    [Fact]
    public void Resolve_BackslashesAndOtherCaseAccepted_OnWindowsOnly()
    {
        var managed = NewManaged();
        var name = Path.GetFileName(managed.GitDirectory);
        var parent = managed.GitRoot.Replace('/', '\\').ToUpperInvariant();
        managed.WriteGitFile($"{parent}\\{name}");

        var result = ManagedGitDirectory.Resolve(managed.RepoRoot, managed.GitRoot);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(managed.GitDirectory, result.GitDirectory, ignoreCase: true);
        }
        else
        {
            Assert.Null(result.GitDirectory);
        }
    }

    [Fact]
    public void WriteOwner_ThenReadOwner_RoundTrips_AndLeavesNoTempFile()
    {
        var directory = TempDirectories.CreateSubdirectory().FullName;
        var record = new OwnerRecord("C:/x/y", DateTimeOffset.Parse("2026-01-02T03:04:05Z"), DateTimeOffset.Parse("2026-02-03T04:05:06Z"));

        ManagedGitDirectory.WriteOwner(directory, record);
        ManagedGitDirectory.WriteOwner(directory, record);

        Assert.Equal(record, ManagedGitDirectory.ReadOwner(directory));
        Assert.Equal([ManagedGitDirectory.OwnerFileName], Directory.GetFiles(directory).Select(Path.GetFileName));
    }

    [Fact]
    public void ReadOwner_MissingOrMalformed_IsNull()
    {
        var directory = TempDirectories.CreateSubdirectory().FullName;
        Assert.Null(ManagedGitDirectory.ReadOwner(directory));

        File.WriteAllText(Path.Combine(directory, ManagedGitDirectory.OwnerFileName), "{}");
        Assert.Null(ManagedGitDirectory.ReadOwner(directory));
    }
}
