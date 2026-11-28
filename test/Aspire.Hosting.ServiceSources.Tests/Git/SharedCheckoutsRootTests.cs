using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Tests.Worktrees;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>Two AppHosts that name one <c>CheckoutsDirectory</c> must stay out of each other's way.</summary>
[Trait("IO", "true")]
[Trait("Concurrency", "true")]
public class SharedCheckoutsRootTests
{
    private const string Url = "https://example.com/orders.git";

    private static (CheckoutsLocation A, CheckoutsLocation B) TwoAppHosts()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var root = Path.Combine(temp, "ss");
        return (
            CheckoutsLocation.Resolve(Path.Combine(temp, "a"), root),
            CheckoutsLocation.Resolve(Path.Combine(temp, "b"), root));
    }

    private static string Stage(CheckoutsLocation location, string id)
    {
        Directory.CreateDirectory(location.CheckoutsRoot);
        var staging = Path.Combine(location.CheckoutsRoot, ".incoming-" + id);
        Directory.CreateDirectory(Path.Combine(staging, ".git"));
        File.WriteAllText(staging + ".lock", "");
        return staging;
    }

    private static LocalGitCheckout.PreparedCheckout Prepare(CheckoutsLocation location, IGitClient git) =>
        LocalGitCheckout.PrepareRepoRoot(
            "orders", LocalGitCheckoutSeparateTests.Definition(Url), new ServiceDeveloperConfig { Source = "repository" },
            repositoryConfig: null, location.AppHostDirectory, location, git,
            progress: null, homeAppHostDirectory: null, layout: new CloneLayoutOptions(null, IsWindows: false));

    [Fact]
    public void Two_apphosts_one_root_never_see_each_others_staging_or_locks()
    {
        var (a, b) = TwoAppHosts();
        a.EnsureOwned();
        b.EnsureOwned();
        var stagingA = Stage(a, "aaaaaaaa");
        var stagingB = Stage(b, "bbbbbbbb");
        File.WriteAllText(Path.Combine(a.CheckoutsRoot, ".incoming-cccccccc.lock"), "");

        ScratchDirectory.SweepAbandoned(b.CheckoutsRoot);

        Assert.True(Directory.Exists(stagingA));
        Assert.True(File.Exists(stagingA + ".lock"));
        Assert.True(File.Exists(Path.Combine(a.CheckoutsRoot, ".incoming-cccccccc.lock")));
        Assert.False(Directory.Exists(stagingB));

        stagingB = Stage(b, "bbbbbbbb");

        ScratchDirectory.SweepAbandoned(a.CheckoutsRoot);

        Assert.True(Directory.Exists(stagingB));
        Assert.True(File.Exists(stagingB + ".lock"));
        Assert.False(Directory.Exists(stagingA));
    }

    [Fact]
    public void Two_apphosts_clone_in_parallel_into_one_root()
    {
        var (a, b) = TwoAppHosts();
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        var results = new[] { a, b }.AsParallel().Select(location => Prepare(location, git).RepoRoot).ToArray();

        Assert.Equal(2, results.Distinct().Count());
        Assert.All(results, root => Assert.True(Directory.Exists(Path.Combine(root, ".git"))));
        Assert.Equal(2, git.PlainClones.Count);
    }

    [Fact]
    public void External_checkout_is_seen_as_live_by_SweepOrphans()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var location = CheckoutsLocation.Resolve(Path.Combine(temp, "apphost"), Path.Combine(temp, "ss"));
        var gitRoot = Path.Combine(temp, "gitroot");
        var layout = new CloneLayoutOptions(gitRoot, ForceSeparate: true);

        var prepared = LocalGitCheckout.PrepareRepoRoot(
            "orders", LocalGitCheckoutSeparateTests.Definition(LocalGitCheckoutSeparateTests.OriginUrl()),
            new ServiceDeveloperConfig { Source = "repository" }, repositoryConfig: null, location.AppHostDirectory, location,
            LocalGitCheckoutSeparateTests.RealGit(), progress: null, homeAppHostDirectory: null, layout);

        var gitDirectory = Assert.Single(Directory.GetDirectories(gitRoot));
        SeparateGitDirectories.SweepOrphans(gitRoot, DateTimeOffset.UtcNow.AddDays(365));

        Assert.True(Directory.Exists(gitDirectory));
        Assert.Equal(gitDirectory, ManagedGitDirectory.Resolve(prepared.RepoRoot, gitRoot).GitDirectory);
    }
}
