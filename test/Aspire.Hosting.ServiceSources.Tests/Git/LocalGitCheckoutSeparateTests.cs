using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Tests.Worktrees;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The managed clone with a separate git directory, through <see cref="LocalGitCheckout"/> and a real
/// git. The layout is forced by injection so these run on any platform.
/// </summary>
[Trait("IO", "true")]
public class LocalGitCheckoutSeparateTests
{
    private const string FakeUrl = "https://example.com/orders.git";

    internal static ServiceDefinition Definition(string url) => new()
    {
        Repository = new RepositoryDefinition { Url = url, CheckoutName = "orders" },
        Project = "Orders.csproj",
        Kind = LocalKinds.Dotnet,
        Origin = CatalogOrigin.FromYaml("servicesources.yaml"),
    };

    private static ServiceDeveloperConfig DevConfig() => new() { Source = "repository" };

    internal sealed record Environment(string AppHost, string GitRoot, CloneLayoutOptions Layout)
    {
        public string Checkout => LocalGitCheckout.ManagedRepoRoot(AppHost, "orders");

        public string[] GitDirectories => Directory.Exists(GitRoot)
            ? [.. Directory.GetDirectories(GitRoot)]
            : [];
    }

    internal static Environment NewEnvironment(bool separate = true)
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var gitRoot = Path.Combine(temp, "gitroot");
        var layout = separate
            ? new CloneLayoutOptions(gitRoot, ForceSeparate: true)
            : new CloneLayoutOptions(gitRoot, IsWindows: false);
        return new Environment(Path.Combine(temp, "apphost"), gitRoot, layout);
    }

    internal static string OriginUrl() => new Uri(TestRepository.CreateOrigin().Path).AbsoluteUri;

    internal static LocalGitCheckout.PreparedCheckout PrepareManaged(
        Environment environment, IGitClient git, string url, Func<string?>? home = null) =>
        LocalGitCheckout.PrepareRepoRoot(
            "orders", Definition(url), DevConfig(), repositoryConfig: null, environment.AppHost, git,
            progress: null, home, environment.Layout);

    internal static GitCliClient RealGit() => new(TestRepository.IsolatedEnvironment());

    /// <summary>Forwards to a real git, counting clones and optionally acting once one has landed.</summary>
    internal sealed class RecordingGitClient(IGitClient inner) : IGitClient
    {
        public int Clones { get; private set; }

        public Action<string>? AfterClone { get; init; }

        public List<string> References { get; } = [];

        public void CloneWithReference(
            string repositoryUrl, string destinationPath, string referenceRepository, IGitProgressSink? progress = null,
            string? separateGitDir = null)
        {
            References.Add(referenceRepository);
            Clones++;
            inner.CloneWithReference(repositoryUrl, destinationPath, referenceRepository, progress, separateGitDir);
        }

        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null, string? separateGitDir = null)
        {
            Clones++;
            inner.Clone(repositoryUrl, destinationPath, progress, separateGitDir);
            AfterClone?.Invoke(destinationPath);
        }

        public void Checkout(string repositoryPath, string reference) => inner.Checkout(repositoryPath, reference);

        public void Fetch(string repositoryPath) => inner.Fetch(repositoryPath);

        public bool HasUncommittedChanges(string repositoryPath) => inner.HasUncommittedChanges(repositoryPath);

        public bool IsRefCheckedOut(string repositoryPath, string reference) => inner.IsRefCheckedOut(repositoryPath, reference);

        public string? GetOriginUrl(string repositoryPath) => inner.GetOriginUrl(repositoryPath);
    }

    [Fact]
    public void Clone_ForcedSeparate_LeavesGitFileAtCheckoutAndGitDirUnderRoot()
    {
        var environment = NewEnvironment();

        var prepared = PrepareManaged(environment, RealGit(), OriginUrl());

        Assert.False(prepared.NeedsReconciliation);
        Assert.True(File.Exists(Path.Combine(environment.Checkout, ".git")));
        var gitDirectory = Assert.Single(environment.GitDirectories);
        Assert.Matches(ManagedGitDirectory.NamePattern, Path.GetFileName(gitDirectory));
        Assert.Equal([gitDirectory], Directory.GetFileSystemEntries(environment.GitRoot));
        Assert.Equal(gitDirectory, ManagedGitDirectory.Resolve(environment.Checkout, environment.GitRoot).GitDirectory);
        Assert.Equal("main content", TestRepository.At(environment.Checkout).Read("file.txt"));
        TestRepository.At(environment.Checkout).Git("status");
    }

    [Fact]
    public void Clone_Separate_OwnerRecordNamesFinalPath()
    {
        var environment = NewEnvironment();

        PrepareManaged(environment, RealGit(), OriginUrl());

        var owner = ManagedGitDirectory.ReadOwner(Assert.Single(environment.GitDirectories));
        Assert.NotNull(owner);
        Assert.Equal(environment.Checkout, owner.Checkout, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void Clone_Separate_FailedClone_RemovesGitDirectory()
    {
        var environment = NewEnvironment();
        var missing = new Uri(Path.Combine(TempDirectories.CreateSubdirectory().FullName, "no-such-origin")).AbsoluteUri;

        Assert.Throws<ServiceSourcesConfigurationException>(() => PrepareManaged(environment, RealGit(), missing));

        Assert.Empty(Directory.GetFileSystemEntries(environment.GitRoot));
    }

    [Fact]
    public void Clone_Separate_FailedMove_RemovesGitDirectory()
    {
        var environment = NewEnvironment();
        Directory.CreateDirectory(Path.GetDirectoryName(environment.Checkout)!);
        File.WriteAllText(environment.Checkout, "in the way");

        Assert.Throws<ServiceSourcesConfigurationException>(() => PrepareManaged(environment, RealGit(), OriginUrl()));

        Assert.Empty(Directory.GetFileSystemEntries(environment.GitRoot));
    }

    [Fact]
    public void PrepareRepoRoot_ExistingManagedSeparateCheckout_IsAdoptedNotRecloned()
    {
        var environment = NewEnvironment();
        var git = new RecordingGitClient(RealGit());
        var url = OriginUrl();
        PrepareManaged(environment, git, url);

        var second = PrepareManaged(environment, git, url);

        Assert.True(second.NeedsReconciliation);
        Assert.Equal(1, git.Clones);
    }

    [Fact]
    public void PrepareRepoRoot_GitFileResolveRejects_KeepsRefusalWithSpecificReason()
    {
        var environment = NewEnvironment();
        Directory.CreateDirectory(environment.Checkout);
        File.WriteAllText(Path.Combine(environment.Checkout, ".git"), "gitdir: elsewhere");
        var git = new RecordingGitClient(RealGit());

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => PrepareManaged(environment, git, OriginUrl()));

        Assert.Contains("'.git' file", ex.Message, StringComparison.Ordinal);
        Assert.Contains("absolute git directory", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, git.Clones);
        Assert.True(File.Exists(Path.Combine(environment.Checkout, ".git")));
    }

    [Fact]
    public void PrepareRepoRoot_DebrisWithoutGitEntry_StillDeleted()
    {
        var environment = NewEnvironment();
        Directory.CreateDirectory(environment.Checkout);
        File.WriteAllText(Path.Combine(environment.Checkout, "stale.txt"), "debris");

        PrepareManaged(environment, RealGit(), OriginUrl());

        Assert.False(File.Exists(Path.Combine(environment.Checkout, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(environment.Checkout, ".git")));
    }

    [Fact]
    public void Clone_Separate_OtherCheckoutsGitFileIsNeverDeleted()
    {
        var environment = NewEnvironment();
        var git = new RecordingGitClient(RealGit())
        {
            // A concurrent resolution that landed a working tree whose '.git' this tool did not write.
            AfterClone = _ =>
            {
                Directory.CreateDirectory(environment.Checkout);
                File.WriteAllText(Path.Combine(environment.Checkout, ".git"), "gitdir: elsewhere");
                File.WriteAllText(Path.Combine(environment.Checkout, "theirs.txt"), "their work");
            },
        };

        Assert.Throws<ServiceSourcesConfigurationException>(() => PrepareManaged(environment, git, OriginUrl()));

        Assert.Equal("their work", File.ReadAllText(Path.Combine(environment.Checkout, "theirs.txt")));
        Assert.Empty(Directory.GetFileSystemEntries(environment.GitRoot));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Clone_Separate_RunsOrphanSweepOnlyForSeparateClones(bool separate, bool orphanSurvives)
    {
        var environment = NewEnvironment(separate);
        var orphan = Path.Combine(environment.GitRoot, ManagedGitDirectory.NameFor(Path.Combine(environment.AppHost, "gone"), "deadbeef"));
        Directory.CreateDirectory(orphan);
        var ancient = DateTimeOffset.UtcNow - TimeSpan.FromDays(90);
        ManagedGitDirectory.WriteOwner(orphan, new OwnerRecord(Path.Combine(environment.AppHost, "gone"), ancient, ancient));

        PrepareManaged(environment, RealGit(), OriginUrl());

        Assert.Equal(orphanSurvives, Directory.Exists(orphan));
    }

    [Fact]
    public void Clone_Separate_ReferenceFailureRetry_RemovesOldPairAndPairsTheNewScratch()
    {
        var environment = NewEnvironment();
        var home = TempDirectories.CreateSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(LocalGitCheckout.ManagedRepoRoot(home, "orders"), ".git"));
        var git = new ScriptedCloneGitClient
        {
            HomeOrigin = FakeUrl,
            ReferenceFailure = GitReferenceFailedException.For(
                $"alternate object failure", GitCommandFailedException.For($"alternate object failure")),
        };

        PrepareManaged(environment, git, FakeUrl, () => home);

        var attempts = git.SeparateGitDirs;
        Assert.Equal(2, attempts.Count);
        Assert.NotEqual(attempts[0], attempts[1]);
        var survivor = Assert.Single(environment.GitDirectories);
        Assert.Equal(attempts[1], survivor);
        Assert.Equal([survivor], Directory.GetFileSystemEntries(environment.GitRoot));
    }

    [Fact]
    public void Clone_BeyondCeiling_ThrowsBeforeAnyGitCall()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        var appHost = Path.Combine(temp, new string('a', 230));
        var environment = new Environment(appHost, Path.Combine(temp, "gitroot"), new CloneLayoutOptions(Path.Combine(temp, "gitroot"), IsWindows: true));
        var git = new RecordingGitClient(RealGit());

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => PrepareManaged(environment, git, OriginUrl()));

        Assert.Contains("characters shorter", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, git.Clones);
    }
}

[Trait("IO", "true")]
[Trait("Concurrency", "true")]
public class LocalGitCheckoutSeparateRaceTests
{
    [Fact]
    public async Task TwoAppHostsSameFinalPath_OneCheckoutOneGitDirectoryKept()
    {
        var environment = LocalGitCheckoutSeparateTests.NewEnvironment();
        var url = LocalGitCheckoutSeparateTests.OriginUrl();
        using var barrier = new Barrier(2);

        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            LocalGitCheckoutSeparateTests.PrepareManaged(environment, new GitCliClient(TestRepository.IsolatedEnvironment()), url);
        })));

        var gitDirectory = Assert.Single(environment.GitDirectories);
        Assert.Equal([gitDirectory], Directory.GetFileSystemEntries(environment.GitRoot));
        Assert.Equal(gitDirectory, ManagedGitDirectory.Resolve(environment.Checkout, environment.GitRoot).GitDirectory);
    }
}
