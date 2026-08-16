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
                throw new InvalidOperationException("ref not resolvable locally");
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
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: null), DevConfig(), appHostDirectory, gitClient);

        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_CleanTree_ReconcilesChangedRef()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), appHostDirectory, gitClient);

        var (repositoryPath, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal(repoDir, repositoryPath);
        Assert.Equal("feature/x", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_DirtyTreeButAlreadyOnConfiguredRef_DoesNothing()
    {
        var appHostDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(appHostDirectory, ".servicesources", "checkouts", ServiceName);
        Directory.CreateDirectory(repoDir);
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
}
