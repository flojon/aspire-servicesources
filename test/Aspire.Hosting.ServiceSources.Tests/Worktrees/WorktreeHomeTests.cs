using Aspire.Hosting.ServiceSources.Tests.Git;
using Aspire.Hosting.ServiceSources.Worktrees;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

[Trait("IO", "true")]
public class WorktreeHomeTests
{
    private static GitWorktreeProbe RealProbe() => new(TestRepository.IsolatedEnvironment());

    /// <summary>A repository with a committed AppHost directory at <c>app</c>.</summary>
    private static TestRepository CreateMain()
    {
        var main = TestRepository.CreateOrigin();
        Directory.CreateDirectory(Path.Combine(main.Path, "app"));
        main.Commit("app/AppHost.txt", "apphost", "add apphost");

        // Untracked, so only the main worktree has it: proves which directory resolved.
        main.Write("app/main-only.txt", "main");
        return main;
    }

    private static string AddWorktree(TestRepository from, string path, string branch)
    {
        from.Git("worktree", "add", "--quiet", "-b", branch, path);
        return path;
    }

    private static void AssertIsMainAppHost(HomeWorktree? home)
    {
        Assert.NotNull(home);
        Assert.Equal("main", File.ReadAllText(Path.Combine(home.AppHostDirectory, "main-only.txt")));
    }

    private sealed class RecordingProbe : IWorktreeProbe
    {
        public int Calls { get; private set; }

        public WorktreeLayout? Probe(string directory)
        {
            Calls++;
            return null;
        }
    }

    [Fact]
    public void LinkedWorktree_ResolvesToTheMainWorktreesAppHost()
    {
        var main = CreateMain();
        var worktree = AddWorktree(main, Path.Combine(TempDirectories.CreateSubdirectory().FullName, "wt"), "wt");

        AssertIsMainAppHost(WorktreeHome.Resolve(Path.Combine(worktree, "app"), RealProbe()));
    }

    [Fact]
    public void WorktreeOfAWorktree_ResolvesToTheMainWorktree()
    {
        var main = CreateMain();
        var first = AddWorktree(main, Path.Combine(TempDirectories.CreateSubdirectory().FullName, "wt1"), "wt1");
        var second = AddWorktree(TestRepository.At(first), Path.Combine(TempDirectories.CreateSubdirectory().FullName, "wt2"), "wt2");

        AssertIsMainAppHost(WorktreeHome.Resolve(Path.Combine(second, "app"), RealProbe()));
    }

    [Fact]
    public void WorktreeNestedInsideTheMainWorktree_ResolvesToTheMainWorktree()
    {
        var main = CreateMain();
        var nested = AddWorktree(main, Path.Combine(main.Path, ".worktrees", "x"), "nested");

        AssertIsMainAppHost(WorktreeHome.Resolve(Path.Combine(nested, "app"), RealProbe()));
    }

    [Fact]
    public void BareRepositoryLayout_ResolvesToNull()
    {
        var main = CreateMain();
        var parent = TempDirectories.CreateSubdirectory().FullName;
        TestRepository.At(parent).Git("clone", "--bare", "--quiet", main.Path, "bare.git");
        var worktree = Path.Combine(parent, "wt");
        TestRepository.At(Path.Combine(parent, "bare.git")).Git("worktree", "add", "--quiet", "-b", "wt", worktree);

        Assert.Null(WorktreeHome.Resolve(Path.Combine(worktree, "app"), RealProbe()));
    }

    [Fact]
    public void MainWorktree_ResolvesToNullWithoutAskingGit()
    {
        var main = CreateMain();
        var probe = new RecordingProbe();

        Assert.Null(WorktreeHome.Resolve(Path.Combine(main.Path, "app"), probe));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void DirectoryOutsideAnyRepository_ResolvesToNullWithoutAskingGit()
    {
        var probe = new RecordingProbe();

        Assert.Null(WorktreeHome.Resolve(TempDirectories.CreateSubdirectory().FullName, probe));
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public void HomeWithoutTheAppHostDirectory_ResolvesToNull()
    {
        var fake = FakeWorktree.Siblings(createMainAppHost: false);

        Assert.Null(WorktreeHome.Resolve(fake.WorktreeAppHost, fake));
    }

    [Fact]
    public void ParseMainWorktree_ReadsTheFirstRecordAndItsBareFlag()
    {
        Assert.Equal<(string, bool)?>(("/r/main", false), GitWorktreeProbe.ParseMainWorktree(
            "worktree /r/main\nHEAD abc\nbranch refs/heads/main\n\nworktree /r/wt\nHEAD def\nbranch refs/heads/wt\n"));
        Assert.Equal<(string, bool)?>(("/r/bare.git", true), GitWorktreeProbe.ParseMainWorktree(
            "worktree /r/bare.git\nbare\n\nworktree /r/wt\nHEAD def\nbranch refs/heads/wt\n"));
        Assert.Null(GitWorktreeProbe.ParseMainWorktree(""));
    }

    [Fact]
    public void TryResolve_IsCachedPerBuilder()
    {
        var fake = FakeWorktree.Siblings();
        var builder = fake.CreateWorktreeBuilder();

        var first = WorktreeHome.TryResolve(builder);
        var second = WorktreeHome.TryResolve(builder);

        Assert.Same(first, second);
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void UseProbe_AfterResolution_Throws()
    {
        var fake = FakeWorktree.Siblings();
        var builder = fake.CreateWorktreeBuilder();
        WorktreeHome.TryResolve(builder);

        Assert.Throws<InvalidOperationException>(() => WorktreeHome.UseProbe(builder, fake));
    }
}
