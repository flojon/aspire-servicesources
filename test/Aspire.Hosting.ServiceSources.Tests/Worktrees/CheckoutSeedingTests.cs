using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
using Aspire.Hosting.ServiceSources.Tests.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

[Trait("IO", "true")]
public class CheckoutSeedingTests
{
    private const string Url = "https://example.com/orders.git";

    private static ServiceDefinition Definition(string url = Url) => new()
    {
        Repository = new RepositoryDefinition { Url = url, CheckoutName = "orders" },
        Project = "Orders.csproj",
        Kind = LocalKinds.Dotnet,
        Origin = CatalogOrigin.FromYaml("servicesources.yaml"),
    };

    private static ServiceDeveloperConfig DevConfig() => new() { Source = "repository" };

    private static string NewAppHost() => TempDirectories.CreateSubdirectory().FullName;

    private static string HomeCheckout(string homeAppHost)
    {
        var root = LocalGitCheckout.ManagedRepoRoot(CheckoutsLocation.Default(homeAppHost), "orders");
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        return root;
    }

    private static LocalGitCheckout.PreparedCheckout Prepare(
        string appHost, string? home, IGitClient git, IGitProgressSink? progress = null, string url = Url) =>
        LocalGitCheckout.PrepareRepoRoot(
            "orders", Definition(url), DevConfig(), repositoryConfig: null, appHost, CheckoutsLocation.Default(appHost), git, progress, () => home);

    private static IEnumerable<string> Scratch(string appHost) =>
        Directory.EnumerateDirectories(
            Path.GetDirectoryName(LocalGitCheckout.ManagedRepoRoot(CheckoutsLocation.Default(appHost), "orders"))!, ".incoming-*");

    [Fact]
    public void MatchingHomeCheckout_IsBorrowedFrom()
    {
        var home = NewAppHost();
        var homeCheckout = HomeCheckout(home);
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Prepare(NewAppHost(), home, git);

        Assert.Equal(homeCheckout, Assert.Single(git.ReferenceClones).Reference);
        Assert.Empty(git.PlainClones);
    }

    [Fact]
    public void HomeCheckoutOfAnotherRepository_IsNotBorrowedFrom()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        var git = new ScriptedCloneGitClient { HomeOrigin = "https://example.com/billing.git" };

        Prepare(NewAppHost(), home, git);

        Assert.Empty(git.ReferenceClones);
        Assert.Single(git.PlainClones);
    }

    [Fact]
    public void HomeCheckoutWithAGitFile_IsNotBorrowedFrom()
    {
        var home = NewAppHost();
        var root = LocalGitCheckout.ManagedRepoRoot(CheckoutsLocation.Default(home), "orders");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: elsewhere");
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Prepare(NewAppHost(), home, git);

        Assert.Empty(git.ReferenceClones);
        Assert.Single(git.PlainClones);
    }

    [Fact]
    public void NoHome_ClonesPlainly()
    {
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Prepare(NewAppHost(), home: null, git);

        Assert.Empty(git.ReferenceClones);
        Assert.Single(git.PlainClones);
    }

    [Fact]
    public void HomeCheckoutThatCannotBeInspected_ClonesPlainly_AndSaysSo()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        var sink = new RecordingProgressSink();
        var git = new ScriptedCloneGitClient { OriginFailure = new InvalidOperationException("dubious ownership") };

        var prepared = Prepare(NewAppHost(), home, git, sink);

        Assert.True(Directory.Exists(Path.Combine(prepared.RepoRoot, ".git")));
        Assert.Empty(git.ReferenceClones);
        Assert.Single(git.PlainClones);
        Assert.Contains(sink.Lines, line => line.Contains("dubious ownership", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceFailure_RetriesPlainlyInAFreshScratchDirectory_AndSaysSo()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        var appHost = NewAppHost();
        var sink = new RecordingProgressSink();
        var git = new ScriptedCloneGitClient
        {
            HomeOrigin = Url,
            ReferenceFailure = GitReferenceFailedException.For($"fatal: cannot repack to clean up", new InvalidOperationException()),
        };

        var prepared = Prepare(appHost, home, git, sink);

        Assert.True(Directory.Exists(Path.Combine(prepared.RepoRoot, ".git")));
        Assert.NotEqual(Assert.Single(git.ReferenceClones).Destination, Assert.Single(git.PlainClones));
        Assert.Empty(Scratch(appHost));
        Assert.Contains(sink.Lines, line => line.Contains("cloning without it", StringComparison.Ordinal));
    }

    [Fact]
    public void ReferenceFailure_WithNobodyWatching_StillClones()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        var git = new ScriptedCloneGitClient
        {
            HomeOrigin = Url,
            ReferenceFailure = GitReferenceFailedException.For($"fatal: cannot repack to clean up", new InvalidOperationException()),
        };

        var prepared = Prepare(NewAppHost(), home, git, progress: null);

        Assert.True(Directory.Exists(Path.Combine(prepared.RepoRoot, ".git")));
    }

    [Fact]
    public void PlainCloneFailure_IsAttemptedOnce()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        var git = new ScriptedCloneGitClient
        {
            HomeOrigin = Url,
            ReferenceFailure = GitCommandFailedException.For($"fatal: unable to access: Could not resolve host"),
        };

        Assert.Throws<ServiceSourcesConfigurationException>(() => Prepare(NewAppHost(), home, git));

        Assert.Single(git.ReferenceClones);
        Assert.Empty(git.PlainClones);
    }

    [Fact]
    public void AuthenticationFailure_IsAttemptedOnce()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        var git = new ScriptedCloneGitClient
        {
            HomeOrigin = Url,
            ReferenceFailure = GitAuthenticationFailedException.For($"Authentication failed", new InvalidOperationException()),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => Prepare(NewAppHost(), home, git));

        Assert.Contains("authentication failed", ex.Message);
        Assert.Empty(git.PlainClones);
    }

    private static string SharedRoot() => Path.Combine(TempDirectories.CreateSubdirectory().FullName, "ss");

    private static string HomeCheckoutUnder(string root, string homeAppHost)
    {
        var checkout = LocalGitCheckout.ManagedRepoRoot(CheckoutsLocation.Resolve(homeAppHost, root), "orders");
        Directory.CreateDirectory(Path.Combine(checkout, ".git"));
        return checkout;
    }

    [Fact]
    public void Home_under_the_same_root_is_found()
    {
        var root = SharedRoot();
        var home = NewAppHost();
        var thisRun = NewAppHost();
        var homeCheckout = HomeCheckoutUnder(root, home);
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        Assert.Equal(
            homeCheckout,
            LocalGitCheckout.ReferenceCheckout(home, CheckoutsLocation.Resolve(thisRun, root), Definition(), git));
    }

    [Fact]
    public void Home_under_the_same_root_is_borrowed_from_by_a_clone()
    {
        var root = SharedRoot();
        var home = NewAppHost();
        var thisRun = NewAppHost();
        var homeCheckout = HomeCheckoutUnder(root, home);
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        LocalGitCheckout.PrepareRepoRoot(
            "orders", Definition(), DevConfig(), repositoryConfig: null, thisRun, CheckoutsLocation.Resolve(thisRun, root),
            git, progress: null, () => home);

        Assert.Equal(homeCheckout, Assert.Single(git.ReferenceClones).Reference);
    }

    [Fact]
    public void Origin_mismatch_is_still_refused()
    {
        var root = SharedRoot();
        var home = NewAppHost();
        HomeCheckoutUnder(root, home);
        var git = new ScriptedCloneGitClient { HomeOrigin = "https://example.com/billing.git" };

        Assert.Null(LocalGitCheckout.ReferenceCheckout(home, CheckoutsLocation.Resolve(NewAppHost(), root), Definition(), git));
    }

    [Fact]
    public void Home_at_a_different_root_or_default_falls_back_to_a_plain_clone_without_error()
    {
        var home = NewAppHost();
        HomeCheckout(home);
        HomeCheckoutUnder(SharedRoot(), home);
        var thisRun = NewAppHost();
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        var prepared = LocalGitCheckout.PrepareRepoRoot(
            "orders", Definition(), DevConfig(), repositoryConfig: null, thisRun, CheckoutsLocation.Resolve(thisRun, SharedRoot()),
            git, progress: null, () => home);

        Assert.Empty(git.ReferenceClones);
        Assert.Single(git.PlainClones);
        Assert.True(Directory.Exists(Path.Combine(prepared.RepoRoot, ".git")));
    }

    [Fact]
    public void Spelling_difference_misses_and_clones_plainly()
    {
        var root = SharedRoot();
        var home = NewAppHost();
        HomeCheckoutUnder(root, home);
        var spelledDifferently = Path.Combine(TempDirectories.CreateSubdirectory().FullName, "home-link");
        try
        {
            Directory.CreateSymbolicLink(spelledDifferently, home);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var git = new ScriptedCloneGitClient { HomeOrigin = Url };
        var thisRun = NewAppHost();

        var prepared = LocalGitCheckout.PrepareRepoRoot(
            "orders", Definition(), DevConfig(), repositoryConfig: null, thisRun, CheckoutsLocation.Resolve(thisRun, root),
            git, progress: null, () => spelledDifferently);

        Assert.Empty(git.ReferenceClones);
        Assert.Single(git.PlainClones);
        Assert.True(Directory.Exists(Path.Combine(prepared.RepoRoot, ".git")));
    }

    [Fact]
    public void RealGit_ColdCheckoutInAWorktree_IsAnOrdinaryCloneOfTheCatalogUrl()
    {
        var origin = TestRepository.CreateOrigin();
        var url = new Uri(origin.Path).AbsoluteUri;
        var git = new GitCliClient(TestRepository.IsolatedEnvironment());
        var home = NewAppHost();
        var homeCheckout = LocalGitCheckout.ManagedRepoRoot(CheckoutsLocation.Default(home), "orders");
        git.Clone(url, homeCheckout);
        TestRepository.At(homeCheckout).Git("branch", "home-only");

        Assert.Equal(homeCheckout, LocalGitCheckout.ReferenceCheckout(home, CheckoutsLocation.Default(Path.GetTempPath()), Definition(url), git));

        var prepared = Prepare(NewAppHost(), home, git, url: url);

        var checkout = prepared.RepoRoot;
        var remoteRefs = TestRepository.At(checkout).Git("for-each-ref", "--format=%(refname)", "refs/remotes");
        Assert.False(File.Exists(Path.Combine(checkout, ".git", "objects", "info", "alternates")));
        Assert.Equal(url, git.GetOriginUrl(checkout));
        Assert.Contains("refs/remotes/origin/feature/x", remoteRefs);
        Assert.DoesNotContain("home-only", remoteRefs);
    }

    [Fact]
    public void Prefetch_PassesTheResolvedHomeToTheClone()
    {
        var fake = FakeWorktree.Siblings();
        File.WriteAllText(
            Path.Combine(fake.WorktreeAppHost, "servicesources.yaml"),
            $"services:\n  orders:\n    repository: {Url}\n    project: Service.csproj\n");
        File.WriteAllText(
            Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName),
            """{ "services": { "orders": { "source": "repository" } } }""");
        var homeCheckout = HomeCheckout(fake.MainAppHost);
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };
        var builder = fake.CreateWorktreeBuilder();
        builder.SetCheckoutTiming(CheckoutTiming.Eager);
        var definition = new ServiceMetadata { Repository = Url, Project = "Service.csproj" }
            .ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

        LocalCheckoutPrefetch.For(builder, git)
            .GetRepoRoot("orders", definition, DevConfig(), repositoryConfig: null, fake.WorktreeAppHost, git);

        Assert.Equal(homeCheckout, Assert.Single(git.ReferenceClones).Reference);
    }

    [Fact]
    public void Prefetch_InASeededWorktreeWithAWarmCheckout_NeverAsksGitForHome()
    {
        var fake = FakeWorktree.Siblings();
        File.WriteAllText(
            Path.Combine(fake.WorktreeAppHost, "servicesources.yaml"),
            $"services:\n  orders:\n    repository: {Url}\n    project: Service.csproj\n");
        File.WriteAllText(
            Path.Combine(fake.WorktreeAppHost, DeveloperConfiguration.FileName),
            """{ "services": { "orders": { "source": "repository" } } }""");
        ToolDirectory.Ensure(fake.WorktreeAppHost);
        File.WriteAllText(Path.Combine(fake.WorktreeAppHost, ".servicesources", "worktree-seed.json"), "{}");
        Directory.CreateDirectory(Path.Combine(LocalGitCheckout.ManagedRepoRoot(CheckoutsLocation.Default(fake.WorktreeAppHost), "orders"), ".git"));
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };
        var builder = fake.CreateWorktreeBuilder();
        builder.SetCheckoutTiming(CheckoutTiming.Eager);
        var definition = new ServiceMetadata { Repository = Url, Project = "Service.csproj" }
            .ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

        LocalCheckoutPrefetch.For(builder, git)
            .GetRepoRoot("orders", definition, DevConfig(), repositoryConfig: null, fake.WorktreeAppHost, git);

        Assert.Equal(0, fake.Calls);
        Assert.Empty(git.ReferenceClones);
        Assert.Empty(git.PlainClones);
    }
}
