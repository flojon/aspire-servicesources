using Aspire.Hosting.ServiceSources.Worktrees;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

/// <summary>
/// A main worktree and a linked one as plain directories, with a probe answering the way git would.
/// </summary>
internal sealed class FakeWorktree : IWorktreeProbe
{
    private int _calls;

    private FakeWorktree(string mainTop, string worktreeTop, bool createMainAppHost)
    {
        MainTop = mainTop;
        WorktreeTop = worktreeTop;

        Directory.CreateDirectory(Path.Combine(mainTop, ".git"));
        if (createMainAppHost)
        {
            Directory.CreateDirectory(MainAppHost);
        }

        Directory.CreateDirectory(WorktreeAppHost);
        File.WriteAllText(Path.Combine(worktreeTop, ".git"), $"gitdir: {Path.Combine(mainTop, ".git", "worktrees", "wt")}");
    }

    public string MainTop { get; }

    public string WorktreeTop { get; }

    public string MainAppHost => Path.Combine(MainTop, "app");

    public string WorktreeAppHost => Path.Combine(WorktreeTop, "app");

    public int Calls => Volatile.Read(ref _calls);

    public static FakeWorktree Siblings(bool createMainAppHost = true)
    {
        var root = TempDirectories.CreateSubdirectory().FullName;
        return new FakeWorktree(Path.Combine(root, "main"), Path.Combine(root, "wt"), createMainAppHost);
    }

    /// <summary>The layout <c>git worktree add .worktrees/x</c> makes.</summary>
    public static FakeWorktree Nested()
    {
        var main = Path.Combine(TempDirectories.CreateSubdirectory().FullName, "main");
        return new FakeWorktree(main, Path.Combine(main, ".worktrees", "x"), createMainAppHost: true);
    }

    public WorktreeLayout? Probe(string directory)
    {
        Interlocked.Increment(ref _calls);

        return new WorktreeLayout(
            GitDir: Path.Combine(MainTop, ".git", "worktrees", "wt"),
            CommonDir: Path.Combine(MainTop, ".git"),
            Top: WorktreeTop,
            Prefix: "app/",
            MainTop: MainTop,
            MainIsBare: false);
    }

    public IDistributedApplicationBuilder CreateWorktreeBuilder()
    {
        var builder = TestHelpers.CreateBuilder(WorktreeAppHost);
        WorktreeHome.UseProbe(builder, this);
        return builder;
    }

    public void WriteHome(string relativePath, string content)
    {
        var path = Path.Combine(MainAppHost, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
