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

        public Exception? CheckoutException { get; set; }

        private int _checkoutAttempts;

        public bool FailFirstCheckoutOnly { get; set; }

        public Exception? FetchException { get; set; }

        public void Clone(string repositoryUrl, string destinationPath)
        {
            if (CloneException is not null)
            {
                throw CloneException;
            }

            ClonedRepos.Add((repositoryUrl, destinationPath));
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "Orders.csproj"), "<Project />");
        }

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
        new() { Source = "local", Path = path, Ref = @ref };

    private static string UnusedAppHostDirectory => Directory.CreateTempSubdirectory().FullName;

    [Fact]
    public void ResolveProjectPath_PathIsSet_UsesItDirectlyWithoutTouchingGit()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        var projectPath = LocalProjectSource.ResolveProjectPath(
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
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(project: "Orders.csproj"), DevConfig(path: repoDir, @ref: "feature/x"), UnusedAppHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("ref", ex.Message);
        Assert.Contains("path", ex.Message);
        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_RelativePathOverride_AnchorsToAppHostDirectoryNotProcessCwd()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var relativePath = Path.GetRelativePath(appHostDirectory, repoDir);
        var gitClient = new FakeGitClient();

        var projectPath = LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(project: "Orders.csproj"), DevConfig(path: relativePath), appHostDirectory, gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_ClonesIntoAppHostDirectoryUnderServiceName()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var projectPath = LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient);

        var (repositoryUrl, destinationPath) = Assert.Single(gitClient.ClonedRepos);
        Assert.Equal("https://github.com/company/orders", repositoryUrl);
        Assert.Equal(Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName), destinationPath);
        Assert.Equal(Path.Combine(destinationPath, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_UsesDeveloperRefOverCatalogDefaultRef()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("feature/x", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_FallsBackToCatalogDefaultRefWhenDeveloperRefUnset()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: null), appHostDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("main", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_NoRefConfigured_SkipsCheckout()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
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

        LocalProjectSource.ResolveProjectPath(
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

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient);

        var (repositoryUrl, destinationPath) = Assert.Single(gitClient.ClonedRepos);
        Assert.Equal("https://github.com/company/orders", repositoryUrl);
        Assert.Equal(repoDir, destinationPath);
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

        LocalProjectSource.ResolveProjectPath(
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

        LocalProjectSource.ResolveProjectPath(
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
            LocalProjectSource.ResolveProjectPath(
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

        var projectPath = LocalProjectSource.ResolveProjectPath(
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

        var projectPath = LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_SshOriginNeedingFetch_ThrowsWithoutAttemptingFetch()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        // The checkout's origin is SSH, which LibGit2Sharp cannot fetch over. The clone path's
        // up-front check never ran for this pre-existing checkout, so the fetch path must catch it.
        var gitClient = new FakeGitClient
        {
            OriginUrl = "git@github.com:company/orders.git",
            FailFirstCheckoutOnly = true,
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName,
                Metadata(repository: "https://github.com/company/orders", defaultRef: "feature/late"),
                DevConfig(),
                appHostDirectory,
                gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("SSH", ex.Message);
        Assert.Empty(gitClient.FetchedRepos);
    }

    [Fact]
    public void ResolveProjectPath_CheckoutFailsWithNonRefException_DoesNotAttemptFetchAndWrapsOriginalException()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient { CheckoutException = new IOException("disk error") };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
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

        var projectPath = LocalProjectSource.ResolveProjectPath(
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
            LocalProjectSource.ResolveProjectPath(
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
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(project: "src/Missing.csproj"), DevConfig(path: repoDir), UnusedAppHostDirectory, new FakeGitClient()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("src/Missing.csproj", ex.Message);
        Assert.Contains(repoDir, ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_CloneFails_WrapsAsConfigurationExceptionNamingServiceAndRepository()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient { CloneException = new InvalidOperationException("network unreachable") };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
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
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("https://github.com/company/orders", ex.Message);
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveProjectPath_SshRepositoryUrl_ThrowsWithoutAttemptingClone()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(repository: "git@github.com:company/orders.git"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("SSH", ex.Message);
        Assert.Empty(gitClient.ClonedRepos);
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
            LocalProjectSource.ResolveProjectPath(
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

        LocalProjectSource.ResolveProjectPath(
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
            LocalProjectSource.ResolveProjectPath(
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
            LocalProjectSource.ResolveProjectPath(
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
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "feature/late"), DevConfig(), appHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("authentication", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveProjectPath_TwoServicesSameRepo_GetIndependentNonCollidingPaths()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var ordersPath = LocalProjectSource.ResolveProjectPath(
            "orders", Metadata(repository: "https://github.com/team-a/orders"), DevConfig(), appHostDirectory, gitClient);
        var billingPath = LocalProjectSource.ResolveProjectPath(
            "billing", Metadata(repository: "https://github.com/team-a/orders"), DevConfig(), appHostDirectory, gitClient);

        Assert.NotEqual(ordersPath, billingPath);
        Assert.Equal(2, gitClient.ClonedRepos.Count);
    }

    [Fact]
    public void ResolveProjectPath_ManagedClone_WritesGitignoreUnderServiceSourcesDirectory()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(), DevConfig(), appHostDirectory, gitClient);

        var gitignorePath = Path.Combine(appHostDirectory, ".servicesources", ".gitignore");
        Assert.True(File.Exists(gitignorePath));
        Assert.Equal("*\n!.gitignore\n", File.ReadAllText(gitignorePath));
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

        LocalProjectSource.ResolveProjectPath(
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
            LocalProjectSource.ResolveProjectPath(
                serviceName, Metadata(), DevConfig(), appHostDirectory, new FakeGitClient());
        });

        var gitignorePath = Path.Combine(appHostDirectory, ".servicesources", ".gitignore");
        Assert.Equal("*\n!.gitignore\n", File.ReadAllText(gitignorePath));
    }

    [Fact]
    public void Resolve_DoesNotCloneOrRegisterSynchronously_DefersUntilBeforeStartEvent()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDir,
            Args = [],
        });
        var gitClient = new FakeGitClient();
        var source = new LocalProjectSource(gitClient);

        var facade = source.Resolve(builder, ServiceName, Metadata(), DevConfig());

        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
        Assert.Empty(facade.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.DoesNotContain(builder.Resources, r => r.Name == ServiceName);
    }
}
