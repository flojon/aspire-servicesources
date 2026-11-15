using Aspire.Hosting.ServiceSources.Git;
using Xunit;
using static Aspire.Hosting.ServiceSources.Tests.Git.LocalGitCheckoutSeparateTests;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The real thing on Windows, with an AppHost directory too deep for the in-place pack path and
/// nothing forced: the layout is decided from the lengths, and git runs for real.
/// </summary>
[Trait("IO", "true")]
public class DeepPathCloneTests
{
    // A staging root is this plus '\.servicesources\checkouts\.incoming-xxxxxxxx' (46), so 185 leaves
    // it under the 235 ceiling and well over the 174 an in-place pack path allows.
    private const int DeepAppHostLength = 185;

    private static LocalGitCheckoutSeparateTests.Environment DeepEnvironment(int length = DeepAppHostLength, string? gitRoot = null)
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var appHost = Path.Combine(temp, new string('d', length - temp.Length - 1));
        var root = gitRoot ?? Path.Combine(temp, "gitroot");
        return new LocalGitCheckoutSeparateTests.Environment(appHost, root, new CloneLayoutOptions(root));
    }

    private static (string Out, string Err) GitIn(string checkout, params string[] arguments) =>
        TestRepository.At(checkout).GitAll(arguments);

    [WindowsFact]
    public void DeepAppHost_ClonesWithGitDirUnderGitRoot()
    {
        var environment = DeepEnvironment();
        Assert.Equal(DeepAppHostLength, environment.AppHost.Length);

        PrepareManaged(environment, RealGit(), OriginUrl());

        Assert.True(File.Exists(Path.Combine(environment.Checkout, ".git")));
        var gitDirectory = Assert.Single(environment.GitDirectories);
        Assert.Equal(gitDirectory, ManagedGitDirectory.Resolve(environment.Checkout, environment.GitRoot).GitDirectory);
        Assert.Equal("main content", TestRepository.At(environment.Checkout).Read("file.txt"));
        Assert.DoesNotContain("Filename too long", GitIn(environment.Checkout, "status").Err, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void SecondRun_ReusesTheCheckout()
    {
        var environment = DeepEnvironment();
        var git = new RecordingGitClient(RealGit());
        var url = OriginUrl();
        PrepareManaged(environment, git, url);

        var second = PrepareManaged(environment, git, url);

        Assert.True(second.NeedsReconciliation);
        Assert.Equal(1, git.Clones);
        Assert.Single(environment.GitDirectories);
    }

    [WindowsFact]
    public void Fetch_AndGcAuto_AndBorrowedObjectClone_Work()
    {
        var url = OriginUrl();
        var home = DeepEnvironment();
        PrepareManaged(home, RealGit(), url);
        RealGit().Fetch(home.Checkout);
        Assert.DoesNotContain("Filename too long", GitIn(home.Checkout, "gc", "--auto").Err, StringComparison.Ordinal);
        Assert.DoesNotContain("Filename too long", GitIn(home.Checkout, "repack", "-a", "-d").Err, StringComparison.Ordinal);

        var worktree = DeepEnvironment(gitRoot: home.GitRoot);
        var git = new RecordingGitClient(RealGit());
        PrepareManaged(worktree, git, url, home: () => home.AppHost);

        Assert.Single(git.References);
        Assert.Equal(2, worktree.GitDirectories.Length);
        Assert.DoesNotContain("Filename too long", GitIn(worktree.Checkout, "status").Err, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(worktree.GitDirectories.Single(d => d != home.GitDirectories[0]), "objects", "info", "alternates")));
    }

    [WindowsFact]
    public void AppHost_240Characters_IsRefusedWithCeilingMessage()
    {
        var environment = DeepEnvironment(length: 240);
        var git = new RecordingGitClient(RealGit());

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => PrepareManaged(environment, git, OriginUrl()));

        Assert.Contains("characters shorter", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, git.Clones);
    }

    [WindowsFact]
    public void GitFileAndMovedWorkingTree_StatusWorks()
    {
        var environment = DeepEnvironment();
        PrepareManaged(environment, RealGit(), OriginUrl());
        var gitDirectory = Assert.Single(environment.GitDirectories);

        Assert.DoesNotContain("worktree", File.ReadAllText(Path.Combine(gitDirectory, "config")), StringComparison.OrdinalIgnoreCase);
        var moved = environment.Checkout + "-moved";
        Directory.Move(environment.Checkout, moved);

        GitIn(moved, "status");
    }
}
