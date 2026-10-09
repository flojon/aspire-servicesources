using System.Text.Json.Nodes;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Worktrees;
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

[Trait("IO", "true")]
public class PathOverrideCheckTests
{
    private static void WriteHomeOverride(FakeWorktree fake, string section, string value)
    {
        var json = new JsonObject
        {
            ["services"] = new JsonObject
            {
                ["orders"] = new JsonObject
                {
                    ["source"] = section == "path" ? "path" : "repository",
                    [section] = new JsonObject { ["path"] = value },
                },
            },
        };

        fake.WriteHome(DeveloperConfiguration.FileName, json.ToJsonString());
    }

    private static IReadOnlyList<string> PathNotices(FakeWorktree fake, string? configuredSource = null)
    {
        var builder = fake.CreateWorktreeBuilder();
        if (configuredSource is not null)
        {
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { [$"{DeveloperConfiguration.ServicesKey}:orders:source"] = configuredSource });
        }

        DeveloperConfigFileSource.EnsureRegistered(builder);
        return [.. ServiceSourcesWarnings.For(builder).Messages.Where(message => message.Contains("Service 'orders'", StringComparison.Ordinal))];
    }

    [Fact]
    public void AbsoluteExternalPath_IsSilent()
    {
        var fake = FakeWorktree.Siblings();
        WriteHomeOverride(fake, "path", TempDirectories.CreateSubdirectory().FullName);

        Assert.Empty(PathNotices(fake));
    }

    [Fact]
    public void InRepositoryRelativePath_IsSilent()
    {
        var fake = FakeWorktree.Siblings();
        WriteHomeOverride(fake, "path", "../services/orders");

        Assert.Empty(PathNotices(fake));
    }

    [Fact]
    public void AbsolutePathIntoTheMainWorktree_Warns()
    {
        var fake = FakeWorktree.Siblings();
        var intoMain = Path.Combine(fake.MainAppHost, ".servicesources", "checkouts", "orders");
        WriteHomeOverride(fake, "path", intoMain);

        var notice = Assert.Single(PathNotices(fake));
        Assert.Contains("'path.path'", notice);
        Assert.Contains("main worktree", notice);
        Assert.Contains(DeveloperConfiguration.FileName, notice);
    }

    [Fact]
    public void EscapingRelativePathFromANestedWorktree_Warns()
    {
        var fake = FakeWorktree.Nested();
        var parent = Path.GetDirectoryName(fake.MainTop)!;
        Directory.CreateDirectory(Path.Combine(parent, "other-repo"));
        Directory.CreateDirectory(Path.Combine(fake.MainTop, ".worktrees", "other-repo"));
        WriteHomeOverride(fake, "path", "../../other-repo");

        var notice = Assert.Single(PathNotices(fake));
        Assert.Contains("different directory", notice);
        Assert.DoesNotContain("build and edit the main worktree's files", notice);
    }

    [Fact]
    public void BlockTheSourceDoesNotRead_IsSilent()
    {
        var fake = FakeWorktree.Siblings();
        fake.WriteHome(
            DeveloperConfiguration.FileName,
            new JsonObject
            {
                ["services"] = new JsonObject
                {
                    ["orders"] = new JsonObject
                    {
                        ["source"] = "Repository",
                        ["path"] = new JsonObject { ["path"] = fake.MainTop },
                    },
                },
            }.ToJsonString());

        Assert.Empty(PathNotices(fake));
    }

    private static void WriteHomePathBlockWithoutSource(FakeWorktree fake) =>
        fake.WriteHome(
            DeveloperConfiguration.FileName,
            new JsonObject
            {
                ["services"] = new JsonObject
                {
                    ["orders"] = new JsonObject { ["path"] = new JsonObject { ["path"] = Path.Combine(fake.MainTop, "x") } },
                },
            }.ToJsonString());

    [Fact]
    public void BlockWithNoSourceAnywhere_Warns()
    {
        var fake = FakeWorktree.Siblings();
        WriteHomePathBlockWithoutSource(fake);

        Assert.Single(PathNotices(fake));
    }

    [Fact]
    public void BlockWithNoSourceInTheFile_WarnsWhenAHigherLayerSelectsItsSource()
    {
        var fake = FakeWorktree.Siblings();
        WriteHomePathBlockWithoutSource(fake);

        Assert.Single(PathNotices(fake, configuredSource: "path"));
    }

    [Fact]
    public void BlockWithNoSourceInTheFile_IsSilentWhenAHigherLayerSelectsAnotherSource()
    {
        var fake = FakeWorktree.Siblings();
        WriteHomePathBlockWithoutSource(fake);

        Assert.Empty(PathNotices(fake, configuredSource: "container"));
    }

    [Fact]
    public void InRepositoryRelativePathFromANestedWorktree_IsSilent()
    {
        var fake = FakeWorktree.Nested();
        WriteHomeOverride(fake, "path", "../services/orders");

        Assert.Empty(PathNotices(fake));
    }

    [Fact]
    public void DeprecatedRepositoryPathIntoTheMainWorktree_WarnsTheSameWay()
    {
        var fake = FakeWorktree.Siblings();
        WriteHomeOverride(fake, "repository", Path.Combine(fake.MainTop, "orders"));

        var notice = Assert.Single(PathNotices(fake));
        Assert.Contains("'repository.path'", notice);
    }

    [Fact]
    public void AnEntryWrittenAfterSeeding_IsNotInspected()
    {
        var fake = FakeWorktree.Siblings();
        DeveloperConfigFileSource.EnsureRegistered(fake.CreateWorktreeBuilder());
        File.WriteAllText(
            Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName),
            new JsonObject
            {
                ["services"] = new JsonObject
                {
                    ["orders"] = new JsonObject { ["source"] = "path", ["path"] = new JsonObject { ["path"] = fake.MainTop } },
                },
            }.ToJsonString());

        Assert.Empty(PathNotices(fake));
    }

    // The verdict travels as a string: PathOverrideVerdict is internal, and a public theory cannot take it (CS0051).
    [Theory]
    // Row 1: the same external directory from both.
    [InlineData("/x/elsewhere", "/x/elsewhere", nameof(PathOverrideVerdict.Unchanged))]
    // Row 2: each worktree's own copy at the same position.
    [InlineData("/x/main/services/orders", "/x/wt/services/orders", nameof(PathOverrideVerdict.MirrorsMainWorktree))]
    // Row 3: the worktree would use main's files.
    [InlineData("/x/main/orders", "/x/main/orders", nameof(PathOverrideVerdict.IntoMainWorktree))]
    // Row 4: a sibling at a different depth.
    [InlineData("/x/other", "/other", nameof(PathOverrideVerdict.ResolvesDifferently))]
    // Row 4, nested: another worktree beside this one under main's .worktrees is not main's own files.
    [InlineData("/x/other-repo", "/x/main/.worktrees/other-repo", nameof(PathOverrideVerdict.ResolvesDifferently), "/x/main/.worktrees/x")]
    // Row 1, nested: an override into the nested worktree itself is its own directory from both.
    [InlineData("/x/main/.worktrees/x/orders", "/x/main/.worktrees/x/orders", nameof(PathOverrideVerdict.Unchanged), "/x/main/.worktrees/x")]
    public void Classify_FollowsTheTable(string fromHome, string fromWorktree, string expected, string worktreeTop = "/x/wt")
    {
        static string Full(string path) => Path.GetFullPath(path);

        Assert.Equal(
            Enum.Parse<PathOverrideVerdict>(expected),
            PathOverrideCheck.Classify(Full(fromHome), Full(fromWorktree), Full("/x/main"), Full(worktreeTop)));
    }
}
