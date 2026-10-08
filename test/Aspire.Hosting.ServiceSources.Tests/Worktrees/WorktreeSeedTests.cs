using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.Sources;
using Aspire.Hosting.ServiceSources.Worktrees;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

[Trait("IO", "true")]
public class WorktreeSeedTests
{
    private const string HomeConfig = """{ "services": { "orders": { "source": "repository" } } }""";
    private const string HomeSelection = """{ "orders": "repository" }""";

    private static string SelectionRelative => Path.Combine(ToolDirectory.Name, SourceSelectionStore.FileName);

    private static IDistributedApplicationBuilder Run(FakeWorktree fake)
    {
        var builder = fake.CreateWorktreeBuilder();
        DeveloperConfigFileSource.EnsureRegistered(builder);
        return builder;
    }

    private static IReadOnlyList<string> Notices(IDistributedApplicationBuilder builder) =>
        ServiceSourcesWarnings.For(builder).Messages;

    [Fact]
    public void AbsentFiles_AreCopiedFromHome_AndReported()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(DeveloperConfiguration.FileName, HomeConfig);
        fake.WriteHome(SelectionRelative, HomeSelection);

        var builder = Run(fake);

        Assert.Equal(HomeConfig, File.ReadAllText(Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName)));
        Assert.Equal(HomeSelection, File.ReadAllText(SourceSelectionStore.PathIn(fake.WorktreeAppHost)));
        Assert.True(File.Exists(WorktreeSeed.MarkerPath(fake.WorktreeAppHost)));

        var notice = Assert.Single(Notices(builder));
        Assert.Contains(DeveloperConfiguration.FileName, notice);
        Assert.Contains("selection.json", notice);
        Assert.Contains(Name.Escape(fake.MainAppHost), notice);
    }

    [Fact]
    public void SeededConfig_IsWhatTheFirstRunReads()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(DeveloperConfiguration.FileName, HomeConfig);

        var builder = Run(fake);

        Assert.Equal("repository", builder.Configuration["ServiceSources:Services:orders:source"]);
    }

    [Fact]
    public void ExistingWorktreeFiles_AreNeverOverwritten()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(DeveloperConfiguration.FileName, HomeConfig);
        var own = """{ "services": { "orders": { "source": "disabled" } } }""";
        File.WriteAllText(Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName), own);

        var builder = Run(fake);

        Assert.Equal(own, File.ReadAllText(Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName)));
        Assert.Empty(Notices(builder));
        Assert.True(File.Exists(WorktreeSeed.MarkerPath(fake.WorktreeAppHost)));
    }

    [Fact]
    public void Marker_StopsASecondSeed_SoADeletedCopyStaysDeleted()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(DeveloperConfiguration.FileName, HomeConfig);
        Run(fake);
        var copy = Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName);
        File.Delete(copy);

        var second = Run(fake);

        Assert.False(File.Exists(copy));
        Assert.Empty(Notices(second));
        Assert.Equal(1, fake.Calls);
    }

    [Fact]
    public void HomeWithNothingToCopy_StillWritesTheMarker()
    {
        var fake = FakeWorktree.Siblings();

        var builder = Run(fake);

        Assert.True(File.Exists(WorktreeSeed.MarkerPath(fake.WorktreeAppHost)));
        Assert.Empty(Notices(builder));
    }

    [Fact]
    public void UnresolvedHome_WritesNoMarker_AndCreatesNoToolDirectory()
    {
        var fake = FakeWorktree.Siblings(createMainAppHost: false);

        Run(fake);

        Assert.False(Directory.Exists(ToolDirectory.PathIn(fake.WorktreeAppHost)));
    }

    [Fact]
    public void UnreadableHomeFile_IsReported_LeavesNoMarker_AndIsRetriedNextRun()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(DeveloperConfiguration.FileName, HomeConfig);
        var homeConfig = Path.Combine(fake.MainAppHost, DeveloperConfiguration.FileName);
        var copy = Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName);

        using (new FileStream(homeConfig, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var builder = Run(fake);

            Assert.False(File.Exists(copy));
            Assert.False(File.Exists(WorktreeSeed.MarkerPath(fake.WorktreeAppHost)));
            Assert.Contains(Notices(builder), notice => notice.Contains("next run", StringComparison.Ordinal));
        }

        Run(fake);

        Assert.Equal(HomeConfig, File.ReadAllText(copy));
        Assert.True(File.Exists(WorktreeSeed.MarkerPath(fake.WorktreeAppHost)));
    }

    [Fact]
    public void MainWorktree_IsLeftAlone()
    {
        var fake = FakeWorktree.Siblings();
        var builder = TestHelpers.CreateBuilder(fake.MainAppHost);
        WorktreeHome.UseProbe(builder, fake);

        DeveloperConfigFileSource.EnsureRegistered(builder);

        Assert.Equal(0, fake.Calls);
        Assert.False(Directory.Exists(ToolDirectory.PathIn(fake.MainAppHost)));
    }
}
