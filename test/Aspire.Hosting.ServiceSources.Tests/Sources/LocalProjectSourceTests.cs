using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class LocalProjectSourceTests
{
    private sealed class FakeGitClient : IGitClient
    {
        public List<(string RepositoryUrl, string DestinationPath)> ClonedRepos { get; } = [];

        public List<(string RepositoryPath, string Reference)> CheckedOutRefs { get; } = [];

        public Exception? CloneException { get; set; }

        /// <summary>
        /// Makes <see cref="Clone"/> write into the destination and only then throw, reproducing a
        /// clone interrupted partway through (a killed process, a dropped connection) rather than
        /// one that fails before touching the disk.
        /// </summary>
        public Exception? PartialCloneException { get; set; }

        public Exception? CheckoutException { get; set; }

        private int _checkoutAttempts;

        public bool FailFirstCheckoutOnly { get; set; }

        public Exception? FetchException { get; set; }

        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
            // libgit2 refuses to clone into a directory that already has content ("exists and is
            // not an empty directory"), which is precisely what turns an interrupted clone into a
            // permanently unresolvable service. The fake has to model that, or the tests guarding
            // against it pass vacuously.
            if (Directory.Exists(destinationPath) && Directory.EnumerateFileSystemEntries(destinationPath).Any())
            {
                throw new IOException($"'{destinationPath}' exists and is not an empty directory");
            }

            if (CloneException is not null)
            {
                throw CloneException;
            }

            if (PartialCloneException is not null)
            {
                ClonedRepos.Add((repositoryUrl, destinationPath));
                Directory.CreateDirectory(destinationPath);
                File.WriteAllText(Path.Combine(destinationPath, "partial.pack"), "half a clone");
                throw PartialCloneException;
            }

            ClonedRepos.Add((repositoryUrl, destinationPath));
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "Orders.csproj"), "<Project />");

            // Runs while this clone is notionally still in flight, so a test can land a second
            // AppHost's checkout in the destination the way a concurrent process would.
            DuringClone?.Invoke();
        }

        /// <summary>
        /// Invoked from inside <see cref="Clone"/>, after it has written its output but before
        /// resolution decides what to do with the destination directory.
        /// </summary>
        public Action? DuringClone { get; set; }

        public void Checkout(string repositoryPath, string reference)
        {
            _checkoutAttempts++;

            if (CheckoutException is not null)
            {
                throw CheckoutException;
            }

            if (FailFirstCheckoutOnly && _checkoutAttempts == 1)
            {
                throw new ServiceSourcesConfigurationException($"Ref '{reference}' was not found in repository at '{repositoryPath}'.");
            }

            CheckedOutRefs.Add((repositoryPath, reference));
            CurrentlyCheckedOutRef = reference;
        }

        public List<string> FetchedRepos { get; } = [];

        public bool UncommittedChanges { get; set; }

        public string? CurrentlyCheckedOutRef { get; set; }

        public void Fetch(string repositoryPath)
        {
            if (FetchException is not null)
            {
                throw FetchException;
            }

            FetchedRepos.Add(repositoryPath);
        }

        public bool HasUncommittedChanges(string repositoryPath) => UncommittedChanges;

        public bool IsRefCheckedOut(string repositoryPath, string reference) => CurrentlyCheckedOutRef == reference;

        public string? OriginUrl { get; set; }

        public string? GetOriginUrl(string repositoryPath) => OriginUrl;
    }

    private const string ServiceName = "orders";

    private static ServiceMetadata Metadata(string repository = "https://github.com/company/orders", string project = "Orders.csproj", string? defaultRef = null) =>
        new() { Repository = repository, Project = project, DefaultRef = defaultRef };

    private static ServiceDeveloperConfig DevConfig(string? path = null, string? @ref = null) =>
        new() { Source = "local", Local = new() { Path = path, Ref = @ref } };

    private static string UnusedAppHostDirectory => Directory.CreateTempSubdirectory().FullName;

    /// <summary>
    /// Mirrors the exact composition <see cref="LocalProjectSource"/> uses in production
    /// (<see cref="LocalGitCheckout.ResolveRepoRoot"/> then
    /// <see cref="LocalProjectSource.ResolveProjectFile"/>), so these tests exercise the real
    /// resolution path rather than a separate one that could drift from it.
    /// </summary>
    private static string ResolveProjectPath(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        string appHostDirectory,
        IGitClient gitClient)
    {
        var repoRoot = LocalGitCheckout.ResolveRepoRoot(serviceName, metadata, config, appHostDirectory, gitClient);

        return LocalProjectSource.ResolveProjectFile(serviceName, repoRoot, metadata.Project);
    }

    [Fact]
    public void ResolveProjectPath_PathIsSet_UsesItDirectlyWithoutTouchingGit()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(project: "Orders.csproj"), DevConfig(path: repoDir), UnusedAppHostDirectory, gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_PathAndRefBothSet_ThrowsNamingServiceAndDoesNotTouchGit()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(project: "Orders.csproj"), DevConfig(path: repoDir, @ref: "feature/x"), UnusedAppHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("ref", ex.Message);
        Assert.Contains("path", ex.Message);
        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveRepoRoot_PathOverridePointsAtMissingDirectory_ThrowsNamingServiceAndPath()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var missing = Path.Combine(appHostDirectory, "frontned");
        var gitClient = new FakeGitClient();

        // Asserted on ResolveRepoRoot rather than the full project-path composition because this is
        // the guard every kind shares: for a non-dotnet kind nothing downstream would catch it.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalGitCheckout.ResolveRepoRoot(
                ServiceName, Metadata(), DevConfig(path: "frontned"), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains(missing, ex.Message);
        Assert.Empty(gitClient.ClonedRepos);
    }

    [Fact]
    public void ResolveProjectPath_RelativePathOverride_AnchorsToAppHostDirectoryNotProcessCwd()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var relativePath = Path.GetRelativePath(appHostDirectory, repoDir);
        var gitClient = new FakeGitClient();

        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(project: "Orders.csproj"), DevConfig(path: relativePath), appHostDirectory, gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_ClonesIntoAppHostDirectoryUnderServiceName()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient);

        var expectedRepoRoot = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);

        var (repositoryUrl, destinationPath) = Assert.Single(gitClient.ClonedRepos);
        Assert.Equal("https://github.com/company/orders", repositoryUrl);

        // Git clones into a scratch sibling that is renamed into place (LocalGitCheckout.CloneIntoPlace),
        // so the contract is where the checkout ends up, not where git was pointed.
        Assert.Equal(Path.Combine(appHostDirectory, ".servicesources", "checkouts"), Path.GetDirectoryName(destinationPath));
        Assert.NotEqual(expectedRepoRoot, destinationPath);
        Assert.False(Directory.Exists(destinationPath), "the scratch directory should not outlive the rename");
        Assert.Equal(Path.Combine(expectedRepoRoot, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_UsesDeveloperRefOverCatalogDefaultRef()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("feature/x", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_FallsBackToCatalogDefaultRefWhenDeveloperRefUnset()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: null), appHostDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("main", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_NoRefConfigured_SkipsCheckout()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        ResolveProjectPath(
            ServiceName, Metadata(defaultRef: null), DevConfig(@ref: null), appHostDirectory, gitClient);

        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_DoesNotCloneOrCheckout()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        ResolveProjectPath(
            ServiceName, Metadata(defaultRef: null), DevConfig(), appHostDirectory, gitClient);

        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_DirectoryExistsWithoutGitMarker_TreatedAsCacheMissAndReClones()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        var gitClient = new FakeGitClient();

        ResolveProjectPath(
            ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient);

        Assert.Equal("https://github.com/company/orders", Assert.Single(gitClient.ClonedRepos).RepositoryUrl);
        Assert.True(File.Exists(Path.Combine(repoDir, "Orders.csproj")));
    }

    [Fact]
    public void ResolveProjectPath_DirectoryHasContentButNoGitMarker_ClearsTheDebrisAndReClones()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        // What an interrupted clone leaves behind: content, but no ".git". libgit2 refuses to clone
        // into a non-empty directory, so before this was cleared the service stayed unresolvable on
        // every subsequent run until someone deleted the directory by hand.
        File.WriteAllText(Path.Combine(repoDir, "partial.pack"), "half a clone");
        var gitClient = new FakeGitClient();

        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient);

        Assert.Single(gitClient.ClonedRepos);
        Assert.False(File.Exists(Path.Combine(repoDir, "partial.pack")));
        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CloneDiesPartway_LeavesNoCheckoutDirectoryBehind()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        var gitClient = new FakeGitClient { PartialCloneException = new InvalidOperationException("connection reset") };

        Assert.Throws<ServiceSourcesConfigurationException>(() => ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient));

        // The half-written clone must not be observable as the checkout: leaving it there is what
        // poisons the directory for every later run.
        Assert.False(Directory.Exists(repoDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            Path.Combine(appHostDirectory, ".servicesources", "checkouts")));
    }

    [Fact]
    public void ResolveProjectPath_CheckoutHasAGitFileRatherThanADirectory_RefusesToDeleteIt()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        // A linked worktree ("git worktree add") or a clone made with --separate-git-dir: ".git" is a
        // file pointing at the real git directory, not a directory. Resolution reaches the clone path
        // because it probes for a ".git" *directory*, and this is a complete checkout that can hold
        // work nobody else has a copy of — so the debris sweep must not treat it as debris.
        File.WriteAllText(Path.Combine(repoDir, ".git"), "gitdir: /elsewhere/.git/worktrees/orders\n");
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("worktree", ex.Message);
        Assert.True(File.Exists(Path.Combine(repoDir, "Orders.csproj")));
        Assert.True(File.Exists(Path.Combine(repoDir, ".git")));
        Assert.Empty(gitClient.ClonedRepos);
    }

    [Fact]
    public void ResolveProjectPath_ConcurrentResolutionLandsItsCloneFirst_UsesItRatherThanDeletingIt()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var checkoutsRoot = Path.Combine(appHostDirectory, ".servicesources", "checkouts");
        var repoDir = Path.Combine(checkoutsRoot, ServiceName);
        var gitClient = new FakeGitClient();
        // A second AppHost over the same directory — a restart while the first is still starting,
        // or two "aspire run"s — finishes its clone and renames it into place while ours is still
        // downloading. Resolution got this far on a "no .git directory" probe taken before that
        // happened; acting on that stale probe would recursively delete a complete checkout,
        // including work only it has.
        gitClient.DuringClone = () =>
        {
            Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
            File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(repoDir, "theirs.txt"), "work only the other process has");
        };

        var projectPath = ResolveProjectPath(ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient);

        Assert.True(File.Exists(Path.Combine(repoDir, "theirs.txt")));
        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
        // Ours was redundant rather than wrong, so it is discarded rather than left as a leak.
        Assert.Empty(Directory.EnumerateDirectories(checkoutsRoot, ".incoming-*"));
    }

    [Fact]
    public void ResolveProjectPath_ConcurrentResolutionLandsACloneOfAnotherRepository_ThrowsRatherThanUsingIt()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        // Same service name, a different repository behind it: two AppHost directories sharing a
        // checkouts root, or a catalog edited between the two runs. Adopting the winner's clone has
        // to apply the same origin check that finding it there on a later run would.
        var gitClient = new FakeGitClient { OriginUrl = "https://github.com/company/billing" };
        gitClient.DuringClone = () =>
        {
            Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
            File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("https://github.com/company/billing", ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_ConcurrentResolutionLandsACheckoutWithUncommittedChanges_DoesNotCheckOutOverThem()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        // The checkout that won the race is on another ref and has work in flight — the winner is
        // still starting up, or someone is editing in it. Forcing our ref onto it would discard
        // that, which is exactly what the existing-checkout path refuses to do.
        var gitClient = new FakeGitClient { UncommittedChanges = true, CurrentlyCheckedOutRef = "main" };
        gitClient.DuringClone = () =>
        {
            Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
            File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(ServiceName, Metadata(), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("uncommitted changes", ex.Message);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_ConcurrentResolutionLandsACheckoutAlreadyOnTheRef_LeavesItAlone()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        var gitClient = new FakeGitClient { CurrentlyCheckedOutRef = "feature/x" };
        gitClient.DuringClone = () =>
        {
            Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
            File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        };

        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
        // Already where it needs to be: adopting it is not a reason to re-run a checkout over a
        // working tree another process owns.
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_CloneFailsOverDebris_LeavesTheDebrisForARetryToClear()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "partial.pack"), "half a clone");

        Assert.Throws<ServiceSourcesConfigurationException>(() => ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory,
            new FakeGitClient { CloneException = new InvalidOperationException("connection reset") }));

        // Removing the destination is deferred until a replacement is in hand, so a clone that never
        // arrives costs nothing. Deleting up front and then failing would have left the service with
        // neither its debris nor a checkout.
        Assert.True(File.Exists(Path.Combine(repoDir, "partial.pack")));

        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, new FakeGitClient());

        Assert.False(File.Exists(Path.Combine(repoDir, "partial.pack")));
        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_AbandonedScratchDirectory_IsSweptOnALaterClone()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var checkoutsRoot = Path.Combine(appHostDirectory, ".servicesources", "checkouts");
        var abandoned = Path.Combine(checkoutsRoot, ".incoming-billing-deadbeef");
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "partial.pack"), "half a clone");
        // A clone killed mid-flight: the finally that normally removes this never ran. Aged past the
        // sweep threshold, because nothing is still cloning a day later.
        Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow - TimeSpan.FromDays(3));
        var gitClient = new FakeGitClient();

        ResolveProjectPath(ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient);

        Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public void ResolveProjectPath_RecentScratchDirectory_IsLeftAlone_SoAConcurrentCloneSurvives()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var checkoutsRoot = Path.Combine(appHostDirectory, ".servicesources", "checkouts");
        var inFlight = Path.Combine(checkoutsRoot, ".incoming-billing-cafe");
        Directory.CreateDirectory(inFlight);
        File.WriteAllText(Path.Combine(inFlight, "partial.pack"), "a clone happening right now");
        var gitClient = new FakeGitClient();

        ResolveProjectPath(ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient);

        // Sweeping on age is what keeps a second AppHost's in-flight clone safe from this one.
        Assert.True(Directory.Exists(inFlight));
    }

    [Fact]
    public void ResolveProjectPath_RetriedAfterAPartialClone_Succeeds()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var failing = new FakeGitClient { PartialCloneException = new InvalidOperationException("connection reset") };

        Assert.Throws<ServiceSourcesConfigurationException>(() => ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, failing));

        // The whole point of the rename: a failed attempt costs nothing but the download.
        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, new FakeGitClient());

        Assert.Equal(
            Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName, "Orders.csproj"),
            projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_CleanTree_ReconcilesChangedRef()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient);

        var (repositoryPath, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal(repoDir, repositoryPath);
        Assert.Equal("feature/x", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_CleanTreeAlreadyOnConfiguredRef_SkipsCheckout()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient { CurrentlyCheckedOutRef = "feature/x" };

        ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient);

        Assert.Empty(gitClient.CheckedOutRefs);
        Assert.Empty(gitClient.FetchedRepos);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_OriginMismatchesConfiguredRepository_Throws()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient { OriginUrl = "https://github.com/company/other-repo.git" };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("company/other-repo", ex.Message);
        Assert.Contains("company/orders", ex.Message);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_OriginMatchesConfiguredRepositoryModuloDotGitSuffix_DoesNotThrow()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient { OriginUrl = "https://github.com/company/orders.git" };

        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_OriginMatchesConfiguredRepositoryAsSshRemote_DoesNotThrow()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient { OriginUrl = "git@github.com:company/orders.git" };

        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_SshOriginNeedingFetch_FetchesIt()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        // An SSH origin is fetched like any other: git carries the developer's own SSH agent and
        // config, so there is no transport here this package has to rule out in advance.
        var gitClient = new FakeGitClient
        {
            OriginUrl = "git@github.com:company/orders.git",
            FailFirstCheckoutOnly = true,
        };

        var projectPath = ResolveProjectPath(
            ServiceName,
            Metadata(repository: "https://github.com/company/orders", defaultRef: "feature/late"),
            DevConfig(),
            appHostDirectory,
            gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
        Assert.Equal(repoDir, Assert.Single(gitClient.FetchedRepos));
    }

    [Fact]
    public void ResolveProjectPath_CheckoutFailsWithNonRefException_DoesNotAttemptFetchAndWrapsOriginalException()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient { CheckoutException = new IOException("disk error") };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "main"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("main", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
        Assert.Empty(gitClient.FetchedRepos);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_DirtyTreeButAlreadyOnConfiguredRef_DoesNothing()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient
        {
            UncommittedChanges = true,
            CurrentlyCheckedOutRef = "feature/x",
        };

        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_DirtyTreeAndDifferentRef_ThrowsWithoutTouchingWorkingTree()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient
        {
            UncommittedChanges = true,
            CurrentlyCheckedOutRef = "main",
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("feature/x", ex.Message);
        Assert.Contains("uncommitted", ex.Message);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_ProjectFileMissing_ThrowsNamingServiceProjectAndRoot()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(project: "src/Missing.csproj"), DevConfig(path: repoDir), UnusedAppHostDirectory, new FakeGitClient()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("src/Missing.csproj", ex.Message);
        Assert.Contains(repoDir, ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_ProjectIsAbsolute_IsRefusedRatherThanResolvedOutsideTheCheckout()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        var elsewhere = Path.Combine(Directory.CreateTempSubdirectory().FullName, "Evil.csproj");
        File.WriteAllText(elsewhere, "<Project />");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(project: elsewhere), DevConfig(path: repoDir), UnusedAppHostDirectory,
                new FakeGitClient()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains(elsewhere, ex.Message);
        Assert.Contains("absolute", ex.Message);
    }

    [Theory]
    [InlineData(@"C:\repos\Evil.csproj")]
    [InlineData(@"\\server\share\Evil.csproj")]
    public void ResolveProjectPath_ProjectAbsoluteOnAnotherPlatform_IsStillRefusedAsAbsolute(string project)
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(project: project), DevConfig(path: repoDir), UnusedAppHostDirectory,
                new FakeGitClient()));

        // Reported as the absolute path it is on whichever platform the AppHost runs, rather than as
        // a file missing from the checkout — the distinction CheckoutRelativePath exists to keep.
        Assert.Contains("absolute", ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_ProjectClimbsOutOfTheCheckout_IsRefusedRatherThanResolvedOutsideIt()
    {
        var parent = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Directory.CreateDirectory(Path.Combine(parent, "orders")).FullName;
        File.WriteAllText(Path.Combine(parent, "Evil.csproj"), "<Project />");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(project: "../Evil.csproj"), DevConfig(path: repoDir), UnusedAppHostDirectory,
                new FakeGitClient()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("../Evil.csproj", ex.Message);
        Assert.Contains("outside", ex.Message);
    }

    /// <summary>
    /// A segment of nothing but dots and spaces used to be counted as an ordinary directory (#241),
    /// matching neither <c>"."</c> nor <c>".."</c> exactly, and the value was then reported as a
    /// project file missing from the checkout. It is refused instead, because the same written
    /// value does not mean the same thing everywhere: an ordinary directory name here, and erased
    /// as the last segment of a path on Windows.
    /// </summary>
    [Theory]
    [InlineData(".../Api.csproj", "...")]
    [InlineData("src/.. /Api.csproj", ".. ")]
    [InlineData(".../../Api.csproj", "...")]
    public void ResolveProjectPath_ProjectSegmentIsOnlyDotsAndSpaces_IsRefusedNamingTheSegment(
        string project, string segment)
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(project: project), DevConfig(path: repoDir), UnusedAppHostDirectory,
                new FakeGitClient()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains(project, ex.Message);

        // Not the "points outside the checkout" message a climbing path gets: on the platform this
        // test runs on, the value names a file that would be sitting inside the checkout. The
        // segment is quoted back because for '.. ' the trailing space is invisible on screen.
        Assert.Contains($"path segment '{segment}'", ex.Message);
        Assert.Contains("dots and spaces", ex.Message);
        Assert.DoesNotContain("points outside", ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_ProjectClimbsAndComesBack_StaysInsideAndIsAccepted()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(repoDir, "src"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");

        // Counted rather than pattern-matched: a '..' that a preceding segment pays for never leaves
        // the checkout, so it is not the thing being refused.
        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(project: "src/../Orders.csproj"), DevConfig(path: repoDir), UnusedAppHostDirectory,
            new FakeGitClient());

        // Compared resolved: the accepted value keeps the separators it was written with, and only
        // '\' is rewritten — so on Windows the combine leaves 'src/../Orders.csproj' mixed, which
        // names the same file and would fail a literal comparison.
        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), Path.GetFullPath(projectPath));
    }

    [Fact]
    public void ResolveProjectPath_ProjectWrittenWithWindowsSeparators_ResolvesOnEveryPlatform()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(repoDir, "src"));
        File.WriteAllText(Path.Combine(repoDir, "src", "Orders.csproj"), "<Project />");

        var projectPath = ResolveProjectPath(
            ServiceName, Metadata(project: @"src\Orders.csproj"), DevConfig(path: repoDir), UnusedAppHostDirectory,
            new FakeGitClient());

        Assert.Equal(Path.Combine(repoDir, "src", "Orders.csproj"), projectPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ResolveProjectPath_ProjectMissing_IsReportedAsTheRequiredKeyItIs(string? project)
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;

        // Three spellings of the same catalog mistake. Null is the one nothing else produces: a
        // `project:` written with nothing after it parses as null rather than as "", overriding the
        // default — the behaviour ServiceCatalogLoader normalizes `kind:` for, and does not normalize
        // this. Whitespace survives quoting. All three are a service that names no project file.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(project: project!), DevConfig(path: repoDir), UnusedAppHostDirectory,
                new FakeGitClient()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("'project' is required", ex.Message);
    }

    [Fact]
    public void Resolve_EagerPath_ProjectClimbsOutOfTheCheckout_IsRefusedBeforeTheCloneRuns()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDir,
            Args = [],
        });
        var gitClient = new FakeGitClient();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new LocalProjectSource(gitClient).Resolve(
                builder, ServiceName, Metadata(project: "../../Evil.csproj"), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("outside", ex.Message);

        // The check needs no working tree, so it belongs in front of the clone rather than after it
        // — the same place the prepare command's identical check sits. Deferral is off by default,
        // so without this the commonest configuration pays for a cold clone before being told the
        // value was wrong before any of it started.
        Assert.Empty(gitClient.ClonedRepos);
    }

    [Fact]
    public void ResolveProjectPath_CloneFails_WrapsAsConfigurationExceptionNamingServiceAndRepository()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient { CloneException = new InvalidOperationException("network unreachable") };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("https://github.com/company/orders", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void ResolveProjectPath_CloneFailsWithAuthError_MessageNamesAuthenticationAsCause()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            CloneException = new GitAuthenticationFailedException("401 unauthorized", new InvalidOperationException()),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("https://github.com/company/orders", ex.Message);
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
        // A credential was offered, so the failure keeps both readings open: hosts answer an
        // unauthenticated request for a private repository with "not found" as often as with
        // "unauthorized". Without this the two branches could collapse into one unnoticed.
        Assert.Contains("not visible to the credentials in use", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveProjectPath_CloneFailsAndNoCredentialWasResolved_MessageSaysNoneWereResolved()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            CloneException = new GitAuthenticationFailedException(
                "could not find appropriate mechanism for credentials",
                new InvalidOperationException(),
                noCredentialsResolved: true),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient));

        // "Authentication failed" would send the developer hunting for a token that was rejected.
        // Nothing was ever sent: the helper yielded nothing and no environment token was set.
        Assert.Contains("no git credentials were resolved", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SERVICESOURCES_GIT_TOKEN", ex.Message, StringComparison.Ordinal);
    }

    // A repository URL may legitimately carry a personal access token, and these exception messages
    // travel to the console and every log sink the AppHost is wired to. Each test below covers a
    // different message, because each one formats the URL itself.
    private const string EmbeddedToken = "ghp_secrettoken";

    private const string RepositoryWithToken = $"https://alice:{EmbeddedToken}@github.com/company/orders";

    [Fact]
    public void ResolveProjectPath_CloneFailsWithAuthError_DoesNotEchoATokenEmbeddedInTheRepositoryUrl()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            CloneException = new GitAuthenticationFailedException("401 unauthorized", new InvalidOperationException()),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(repository: RepositoryWithToken), DevConfig(), appHostDirectory, gitClient));

        Assert.DoesNotContain(EmbeddedToken, ex.Message);
        // Still has to name the repository well enough for the developer to act on it.
        Assert.Contains("github.com/company/orders", ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_CloneFails_DoesNotEchoATokenEmbeddedInTheRepositoryUrl()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient { CloneException = new InvalidOperationException("network unreachable") };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(repository: RepositoryWithToken), DevConfig(), appHostDirectory, gitClient));

        Assert.DoesNotContain(EmbeddedToken, ex.Message);
        Assert.Contains("github.com/company/orders", ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_FetchFailsWithAuthError_DoesNotEchoATokenEmbeddedInTheRepositoryUrl()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            FailFirstCheckoutOnly = true,
            FetchException = new GitAuthenticationFailedException("401 unauthorized", new InvalidOperationException()),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName,
                Metadata(repository: RepositoryWithToken, defaultRef: "feature/late"),
                DevConfig(),
                appHostDirectory,
                gitClient));

        Assert.DoesNotContain(EmbeddedToken, ex.Message);
        Assert.Contains("github.com/company/orders", ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_RefMissingAfterFetch_DoesNotEchoATokenEmbeddedInTheRepositoryUrl()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            CheckoutException = new ServiceSourcesConfigurationException(
                "Ref 'does-not-exist' was not found in repository at '/tmp/x'."),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName,
                Metadata(repository: RepositoryWithToken, defaultRef: "does-not-exist"),
                DevConfig(),
                appHostDirectory,
                gitClient));

        Assert.DoesNotContain(EmbeddedToken, ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_OriginMismatch_DoesNotEchoATokenEmbeddedInEitherUrl()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        // A checkout cloned with the token embedded keeps it in its own remote config, so the origin
        // side of this message is just as much a leak as the configured side.
        var gitClient = new FakeGitClient
        {
            OriginUrl = $"https://bob:{EmbeddedToken}@github.com/company/other-repo.git",
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(repository: RepositoryWithToken), DevConfig(), appHostDirectory, gitClient));

        Assert.DoesNotContain(EmbeddedToken, ex.Message);
        Assert.Contains("company/other-repo", ex.Message);
        Assert.Contains("company/orders", ex.Message);
    }

    [Theory]
    [InlineData("git@github.com:company/orders.git")]
    [InlineData("ssh://git@github.com/company/orders.git")]
    [InlineData("gitserver:company/orders.git")]
    public void ResolveProjectPath_SshRepositoryUrl_IsClonedLikeAnyOther(string repository)
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        // Handed to git as written, in every form git accepts: the developer's SSH agent and
        // ~/.ssh/config are what resolve it, and this package has no reason to second-guess them.
        ResolveProjectPath(
            ServiceName, Metadata(repository: repository), DevConfig(), appHostDirectory, gitClient);

        Assert.Equal(repository, Assert.Single(gitClient.ClonedRepos).RepositoryUrl);
    }

    [Fact]
    public void ResolveProjectPath_SshRepositoryUrlWithEmbeddedCredentials_DoesNotEchoThemWhenTheCloneFails()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient { CloneException = new InvalidOperationException("clone failed") };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName,
                Metadata(repository: $"ssh://alice:{EmbeddedToken}@github.com/company/orders"),
                DevConfig(),
                appHostDirectory,
                gitClient));

        Assert.DoesNotContain(EmbeddedToken, ex.Message);
        Assert.Contains("github.com/company/orders", ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_CheckoutFails_WrapsAsConfigurationExceptionNamingServiceAndRef()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            CheckoutException = new ServiceSourcesConfigurationException(
                "Ref 'missing-ref' was not found in repository at '/tmp/x'."),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "missing-ref"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("missing-ref", ex.Message);
        Assert.IsType<ServiceSourcesConfigurationException>(ex.InnerException);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_CheckoutFailsOnce_FetchesThenRetriesSuccessfully()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient { FailFirstCheckoutOnly = true };

        ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "feature/late"), DevConfig(), appHostDirectory, gitClient);

        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Assert.Equal(new[] { repoDir }, gitClient.FetchedRepos);
        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("feature/late", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_RefMissingEvenAfterFetch_WrapsAsConfigurationException()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            CheckoutException = new ServiceSourcesConfigurationException(
                "Ref 'does-not-exist' was not found in repository at '/tmp/x'."),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "does-not-exist"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("does-not-exist", ex.Message);
        Assert.Single(gitClient.FetchedRepos);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_FetchItselfFails_WrapsAsConfigurationExceptionNamingService()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            FailFirstCheckoutOnly = true,
            FetchException = new InvalidOperationException("network unreachable"),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "feature/late"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_FetchFailsWithAuthError_MessageNamesAuthenticationAsCause()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            FailFirstCheckoutOnly = true,
            FetchException = new GitAuthenticationFailedException("401 unauthorized", new InvalidOperationException()),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "feature/late"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveProjectPath_TwoServicesSameRepo_GetIndependentNonCollidingPaths()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var ordersPath = ResolveProjectPath(
            "orders", Metadata(repository: "https://github.com/team-a/orders"), DevConfig(), appHostDirectory, gitClient);
        var billingPath = ResolveProjectPath(
            "billing", Metadata(repository: "https://github.com/team-a/orders"), DevConfig(), appHostDirectory, gitClient);

        Assert.NotEqual(ordersPath, billingPath);
        Assert.Equal(2, gitClient.ClonedRepos.Count);
    }

    [Fact]
    public void ResolveProjectPath_ManagedClone_WritesGitignoreUnderServiceSourcesDirectory()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient);

        var gitignorePath = Path.Combine(appHostDirectory, ".servicesources", ".gitignore");
        Assert.True(File.Exists(gitignorePath));
        Assert.Equal("*\n!.gitignore\n", File.ReadAllText(gitignorePath));
    }

    [Fact]
    public void ResolveProjectPath_ManagedClone_WritesBuildBarrierUnderServiceSourcesDirectory()
    {
        // The checkout lives inside the AppHost's own repository, so without these MSBuild and
        // NuGet walk past .servicesources and apply the host repository's build settings to it.
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient);

        var dir = Path.Combine(appHostDirectory, ".servicesources");
        Assert.True(File.Exists(Path.Combine(dir, "Directory.Build.props")));
        Assert.True(File.Exists(Path.Combine(dir, "Directory.Build.targets")));
        Assert.True(File.Exists(Path.Combine(dir, "Directory.Packages.props")));
        Assert.True(File.Exists(Path.Combine(dir, "nuget.config")));
        Assert.True(File.Exists(Path.Combine(dir, ".editorconfig")));
        Assert.True(File.Exists(Path.Combine(dir, "global.json")));
    }

    [Fact]
    public void ResolveProjectPath_PathOverride_WritesNoBuildBarrier()
    {
        // A 'path' override points at a checkout the developer manages, wherever they keep it —
        // often inside a repository of their own. Nothing is cloned, nothing is placed under
        // .servicesources, and writing tool-owned files into someone else's tree would be
        // overreach: that tree's own MSBuild and NuGet settings are the ones that should apply.
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var overridePath = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(overridePath, "Orders.csproj"), "<Project />");

        ResolveProjectPath(
            ServiceName,
            Metadata(project: "Orders.csproj"),
            DevConfig(path: overridePath),
            appHostDirectory,
            new FakeGitClient());

        Assert.False(Directory.Exists(Path.Combine(appHostDirectory, ".servicesources")));
        Assert.False(File.Exists(Path.Combine(overridePath, "Directory.Build.props")));
        Assert.False(File.Exists(Path.Combine(overridePath, "nuget.config")));
    }

    [Fact]
    public void ResolveProjectPath_ManagedClone_DoesNotOverwriteExistingGitignore()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var dir = Path.Combine(appHostDirectory, ".servicesources");
        Directory.CreateDirectory(dir);
        var gitignorePath = Path.Combine(dir, ".gitignore");
        File.WriteAllText(gitignorePath, "custom content");
        var gitClient = new FakeGitClient();

        ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient);

        Assert.Equal("custom content", File.ReadAllText(gitignorePath));
    }

    [Fact]
    public void ResolveProjectPath_ConcurrentResolutionsOfDifferentServices_DoNotRaceOnGitignoreCreation()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var serviceNames = Enumerable.Range(0, 8).Select(i => $"service-{i}").ToArray();

        Parallel.ForEach(serviceNames, serviceName =>
        {
            ResolveProjectPath(
                serviceName, Metadata(), DevConfig(), appHostDirectory, new FakeGitClient());
        });

        var gitignorePath = Path.Combine(appHostDirectory, ".servicesources", ".gitignore");
        Assert.Equal("*\n!.gitignore\n", File.ReadAllText(gitignorePath));
    }

    [Fact]
    public void Resolve_ClonesAndRegistersTheRealResourceBeforeReturning()
    {
        // The inverse of the old deferred contract. AddService has to hand back the resource Aspire
        // actually runs, so resolution can no longer wait for BeforeStartEvent. Checkouts stay
        // parallel across services via the prefetch — see LocalCheckoutPrefetchTests.
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDir,
            Args = [],
        });
        var gitClient = new FakeGitClient();
        var source = new LocalProjectSource(gitClient);

        var service = source.Resolve(builder, ServiceName, Metadata(), DevConfig());

        Assert.NotEmpty(gitClient.ClonedRepos);
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, service.Resource));
        Assert.IsAssignableFrom<ProjectResource>(service.Resource);
    }

    /// <summary>
    /// A name that would put the checkout outside <c>.servicesources/checkouts/</c> is refused
    /// before the clone rather than after it (#224). Aspire's own <c>[ResourceName]</c> validation
    /// is no help here: it runs when the resource is added to the application model, which is
    /// several steps past the point where the name has already been made into a path and cloned
    /// into.
    /// </summary>
    [Fact]
    public void ResolveRepoRoot_ServiceNameThatWouldEscapeTheCheckoutDirectory_ThrowsBeforeCloning()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalGitCheckout.ResolveRepoRoot(
                "../escapee", Metadata(), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains("../escapee", ex.Message);
        Assert.Empty(gitClient.ClonedRepos);
        Assert.False(Directory.Exists(Path.Combine(appHostDirectory, ".servicesources", "escapee")));
    }
}
