using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Prepare;

[Trait("IO", "true")]
public class PrepareMarkerSeparateTests
{
    private static (string RepoRoot, string GitRoot, string GitDirectory) SeparateCheckout()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var repoRoot = Path.Combine(temp, "checkouts", "orders");
        var gitRoot = Path.Combine(temp, "gitroot");
        Directory.CreateDirectory(repoRoot);
        var gitDirectory = Path.Combine(gitRoot, ManagedGitDirectory.NameFor(repoRoot, "abcd1234"));
        Directory.CreateDirectory(gitDirectory);
        ManagedGitDirectory.WriteOwner(gitDirectory, new OwnerRecord(repoRoot, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        File.WriteAllText(Path.Combine(repoRoot, ".git"), $"gitdir: {gitDirectory.Replace('\\', '/')}\n");
        return (repoRoot, gitRoot, gitDirectory);
    }

    [Fact]
    public void LocationFor_Managed_InPlace_IsUnderDotGit()
    {
        var repoRoot = TempDirectories.CreateSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

        Assert.Equal(
            Path.Combine(repoRoot, ".git", "servicesources-prepare.json"),
            PrepareMarker.LocationFor("orders", repoRoot, "/unused", managedCheckout: true, gitRoot: "/no-git-root"));
    }

    [Fact]
    public void LocationFor_Managed_Separate_IsInsideResolvedGitDirectory()
    {
        var (repoRoot, gitRoot, gitDirectory) = SeparateCheckout();

        Assert.Equal(
            Path.Combine(gitDirectory, "servicesources-prepare.json"),
            PrepareMarker.LocationFor("orders", repoRoot, "/unused", managedCheckout: true, gitRoot));
    }

    [Fact]
    public void LocationFor_Managed_RejectedGitFile_IsNull()
    {
        var repoRoot = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(repoRoot, ".git"), "gitdir: elsewhere");

        Assert.Null(PrepareMarker.LocationFor("orders", repoRoot, "/unused", managedCheckout: true, gitRoot: Path.GetTempPath()));
    }
}
