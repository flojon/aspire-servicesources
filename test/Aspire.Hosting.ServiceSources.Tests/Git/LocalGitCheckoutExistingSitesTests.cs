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
        var root = LocalGitCheckout.ManagedRepoRoot(CheckoutsLocation.Default(home), "orders");
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

        Assert.Null(LocalGitCheckout.ReferenceCheckout(home, CheckoutsLocation.Default(Path.GetTempPath()), Definition(Url), git, gitRoot: Path.GetTempPath()));
    }

    [Fact]
    public void ReferenceCheckout_LinkedWorktreeGitFile_StillNull()
    {
        var worktreeGitDirectory = Path.Combine(Path.GetTempPath(), "main", ".git", "worktrees", "x").Replace('\\', '/');
        var home = HomeWith(root => File.WriteAllText(Path.Combine(root, ".git"), $"gitdir: {worktreeGitDirectory}\n"));
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Assert.Null(LocalGitCheckout.ReferenceCheckout(home, CheckoutsLocation.Default(Path.GetTempPath()), Definition(Url), git, gitRoot: Path.GetTempPath()));
    }

    [Fact]
    public void ReferenceCheckout_InPlaceGitDirectory_IsStillBorrowedFrom()
    {
        var home = HomeWith(root => Directory.CreateDirectory(Path.Combine(root, ".git")));
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Assert.Equal(
            LocalGitCheckout.ManagedRepoRoot(CheckoutsLocation.Default(home), "orders"),
            LocalGitCheckout.ReferenceCheckout(home, CheckoutsLocation.Default(Path.GetTempPath()), Definition(Url), git));
    }

    private static string Reconcile(
        LocalGitCheckoutSeparateTests.Environment environment, IGitClient git, List<string>? notices = null, string url = Url) =>
        LocalGitCheckout.ReconcileRepoRoot(
            "orders", Definition(url), new() { Source = "repository" }, repositoryConfig: null,
            new LocalGitCheckout.PreparedCheckout(environment.Checkout, NeedsReconciliation: true, CheckoutsLocation.Default(Path.GetTempPath())),
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
    public void Reconcile_InPlaceCheckoutWhoseGitDirWouldBeTooBig_LogsNoticeNamingDeleteAndReclone()
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
    public void NoteExistingCheckout_names_the_actual_checkouts_directory_and_the_setting()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var layout = new CloneLayoutOptions(Path.Combine(temp, "gitroot"), IsWindows: true);
        var location = CheckoutsLocation.Resolve(Path.Combine(temp, "apphost"), Path.Combine(temp, new string('a', 200)));
        var repoRoot = LocalGitCheckout.ManagedRepoRoot(location, "orders");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        var notices = new List<string>();

        LocalGitCheckout.ReconcileRepoRoot(
            "orders", Definition(Url), new() { Source = "repository" }, repositoryConfig: null,
            new LocalGitCheckout.PreparedCheckout(repoRoot, NeedsReconciliation: true, location),
            new ScriptedCloneGitClient(), layout, notice: notice => notices.Add(notice.ToString()));

        var notice = Assert.Single(notices);
        Assert.Contains($"out of '{location.CheckoutsRoot}'", notice, StringComparison.Ordinal);
        Assert.Contains("ServiceSources:CheckoutsDirectory", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("move the AppHost", notice, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(215, 0)]
    [InlineData(216, 1)]
    public void Reconcile_InPlaceCheckout_NoticeFollowsTheGitDirBoundary(int checkoutLength, int expectedNotices)
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var layout = new CloneLayoutOptions(Path.Combine(temp, "gitroot"), IsWindows: true);
        var probe = new LocalGitCheckoutSeparateTests.Environment(temp, layout.GitRoot!, layout);
        var padding = checkoutLength - probe.Checkout.Length;
        var environment = probe with { AppHost = temp + new string('a', padding) };
        Assert.Equal(checkoutLength, environment.Checkout.Length);
        Directory.CreateDirectory(Path.Combine(environment.Checkout, ".git"));
        var notices = new List<string>();

        Reconcile(environment, new ScriptedCloneGitClient(), notices);

        Assert.Equal(expectedNotices, notices.Count);
        if (expectedNotices > 0)
        {
            Assert.Contains("'$GIT_DIR' too big", notices[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Reconcile_DeepCheckoutWithGitFile_LogsNoNotice()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var layout = new CloneLayoutOptions(Path.Combine(temp, "gitroot"), IsWindows: true);
        var environment = new LocalGitCheckoutSeparateTests.Environment(Path.Combine(temp, new string('a', 200)), layout.GitRoot!, layout);
        Directory.CreateDirectory(environment.Checkout);
        File.WriteAllText(Path.Combine(environment.Checkout, ".git"), "gitdir: elsewhere");
        var notices = new List<string>();

        Reconcile(environment, new ScriptedCloneGitClient(), notices);

        Assert.Empty(notices);
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
