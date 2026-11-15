using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Tests.Worktrees;
using Xunit;
using static Aspire.Hosting.ServiceSources.Tests.Git.LocalGitCheckoutSeparateTests;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The sites that used to assume a checkout's <c>.git</c> is a directory: borrowing objects from a
/// home checkout, and reconciling an existing one.
/// </summary>
[Trait("IO", "true")]
public class LocalGitCheckoutExistingSitesTests
{
    private const string Url = "https://example.com/orders.git";

    private static string HomeWith(Action<string> shape)
    {
        var home = TempDirectories.CreateSubdirectory().FullName;
        var root = LocalGitCheckout.ManagedRepoRoot(home, "orders");
        Directory.CreateDirectory(root);
        shape(root);
        return home;
    }

    [Fact]
    public void ReferenceCheckout_SeparateManagedCheckout_IsBorrowedFrom()
    {
        var url = OriginUrl();
        var homeEnvironment = NewEnvironment();
        PrepareManaged(homeEnvironment, RealGit(), url);
        var resolvedHomeGitDirectory = ManagedGitDirectory.Resolve(homeEnvironment.Checkout, homeEnvironment.GitRoot).GitDirectory;
        var worktree = new LocalGitCheckoutSeparateTests.Environment(
            Path.Combine(TempDirectories.CreateSubdirectory().FullName, "apphost"), homeEnvironment.GitRoot, homeEnvironment.Layout);
        var git = new RecordingGitClient(RealGit());

        PrepareManaged(worktree, git, url, home: () => homeEnvironment.AppHost);

        Assert.Equal(resolvedHomeGitDirectory, Assert.Single(git.References));
    }

    [Fact]
    public void ReferenceCheckout_RejectedGitFile_ReturnsNull_NoError()
    {
        var home = HomeWith(root => File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere"));
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Assert.Null(LocalGitCheckout.ReferenceCheckout(home, Definition(Url), git, gitRoot: Path.GetTempPath()));
    }

    [Fact]
    public void ReferenceCheckout_LinkedWorktreeGitFile_StillNull()
    {
        var worktreeGitDirectory = Path.Combine(Path.GetTempPath(), "main", ".git", "worktrees", "x").Replace('\\', '/');
        var home = HomeWith(root => File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {worktreeGitDirectory}\n"));
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Assert.Null(LocalGitCheckout.ReferenceCheckout(home, Definition(Url), git, gitRoot: Path.GetTempPath()));
    }

    [Fact]
    public void ReferenceCheckout_InPlaceGitDirectory_IsStillBorrowedFrom()
    {
        var home = HomeWith(root => Directory.CreateDirectory(Path.Combine(root, ".git")));
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Assert.Equal(
            LocalGitCheckout.ManagedRepoRoot(home, "orders"),
            LocalGitCheckout.ReferenceCheckout(home, Definition(Url), git));
    }

    private static string Reconcile(
        LocalGitCheckoutSeparateTests.Environment environment, IGitClient git, List<string>? notices = null, string url = Url) =>
        LocalGitCheckout.ReconcileRepoRoot(
            "orders", Definition(url), new() { Source = "repository" }, repositoryConfig: null,
            new LocalGitCheckout.PreparedCheckout(environment.Checkout, NeedsReconciliation: true),
            git, environment.Layout, notice: notice => notices?.Add(notice.ToString()));

    [Fact]
    public void Reconcile_SeparateCheckout_RefreshesLastUsedWhenOverADayOld()
    {
        var environment = NewEnvironment();
        var url = OriginUrl();
        var git = RealGit();
        PrepareManaged(environment, git, url);
        var gitDirectory = Assert.Single(environment.GitDirectories);
        var stale = DateTimeOffset.UtcNow - TimeSpan.FromDays(3);
        ManagedGitDirectory.WriteOwner(gitDirectory, new OwnerRecord(environment.Checkout, stale, stale));

        Reconcile(environment, git, url: url);

        Assert.True(ManagedGitDirectory.ReadOwner(gitDirectory)!.LastUsedUtc > DateTimeOffset.UtcNow - TimeSpan.FromHours(1));
    }

    [Fact]
    public void Reconcile_InPlaceCheckoutWhosePackPathWouldNotFit_LogsNoticeNamingDeleteAndReclone()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var layout = new CloneLayoutOptions(Path.Combine(temp, "gitroot"), IsWindows: true);
        var environment = new LocalGitCheckoutSeparateTests.Environment(Path.Combine(temp, new string('a', 200)), layout.GitRoot!, layout);
        Directory.CreateDirectory(Path.Combine(environment.Checkout, ".git"));
        var notices = new List<string>();

        Reconcile(environment, new ScriptedCloneGitClient(), notices);

        var notice = Assert.Single(notices);
        Assert.Contains("delete", notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Path.Combine("checkouts", "orders"), notice, StringComparison.Ordinal);
        Assert.Contains("re-run", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reconcile_InPlaceCheckoutThatFits_LogsNothing()
    {
        var environment = NewEnvironment(separate: false);
        Directory.CreateDirectory(Path.Combine(environment.Checkout, ".git"));
        var notices = new List<string>();

        Reconcile(environment, new ScriptedCloneGitClient(), notices);

        Assert.Empty(notices);
    }

    [Fact]
    public void Reconcile_Never_MutatesAnInPlaceCheckout()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var layout = new CloneLayoutOptions(Path.Combine(temp, "gitroot"), IsWindows: true);
        var environment = new LocalGitCheckoutSeparateTests.Environment(Path.Combine(temp, new string('a', 200)), layout.GitRoot!, layout);
        var dotGit = Path.Combine(environment.Checkout, ".git");
        Directory.CreateDirectory(dotGit);
        File.WriteAllText(Path.Combine(dotGit, "HEAD"), "ref: refs/heads/main");
        var before = Directory.GetCreationTimeUtc(dotGit);

        Reconcile(environment, new ScriptedCloneGitClient(), []);

        Assert.True(Directory.Exists(dotGit));
        Assert.Equal(before, Directory.GetCreationTimeUtc(dotGit));
        Assert.Equal("ref: refs/heads/main", File.ReadAllText(Path.Combine(dotGit, "HEAD")));
        Assert.False(Directory.Exists(layout.GitRoot));
    }
}
