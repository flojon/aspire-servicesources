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

        public Dictionary<string, string> OriginUrlsByPath { get; } = [];

        public Exception? CloneException { get; set; }

        public Exception? CheckoutException { get; set; }

        public void Clone(string repositoryUrl, string destinationPath)
        {
            if (CloneException is not null)
            {
                throw CloneException;
            }

            ClonedRepos.Add((repositoryUrl, destinationPath));
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "Orders.csproj"), "<Project />");
            OriginUrlsByPath[destinationPath] = repositoryUrl;
        }

        public void Checkout(string repositoryPath, string reference)
        {
            if (CheckoutException is not null)
            {
                throw CheckoutException;
            }

            CheckedOutRefs.Add((repositoryPath, reference));
        }

        public string? GetOriginUrl(string repositoryPath) =>
            OriginUrlsByPath.GetValueOrDefault(repositoryPath);

        public List<string> FetchedRepos { get; } = [];

        public bool UncommittedChanges { get; set; }

        public string? CurrentlyCheckedOutRef { get; set; }

        public void Fetch(string repositoryPath) => FetchedRepos.Add(repositoryPath);

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
            ServiceName, Metadata(project: "Orders.csproj"), DevConfig(path: repoDir), "/unused/cache", UnusedAppHostDirectory, gitClient);

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
                ServiceName, Metadata(project: "Orders.csproj"), DevConfig(path: repoDir, @ref: "feature/x"), "/unused/cache", UnusedAppHostDirectory, gitClient));

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
            ServiceName, Metadata(project: "Orders.csproj"), DevConfig(path: relativePath), "/unused/cache", appHostDirectory, gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_ClonesIntoCacheDirectoryUnderRepoName()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var projectPath = LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), cacheDirectory, UnusedAppHostDirectory, gitClient);

        var (repositoryUrl, destinationPath) = Assert.Single(gitClient.ClonedRepos);
        Assert.Equal("https://github.com/company/orders", repositoryUrl);
        Assert.Equal(Path.Combine(cacheDirectory, "orders"), destinationPath);
        Assert.Equal(Path.Combine(destinationPath, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_UsesDeveloperRefOverCatalogDefaultRef()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), cacheDirectory, UnusedAppHostDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("feature/x", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_FallsBackToCatalogDefaultRefWhenDeveloperRefUnset()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(@ref: null), cacheDirectory, UnusedAppHostDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("main", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_NoRefConfigured_SkipsCheckout()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: null), DevConfig(@ref: null), cacheDirectory, UnusedAppHostDirectory, gitClient);

        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_DoesNotCloneOrCheckout()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(cacheDirectory, "orders");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(defaultRef: "main"), DevConfig(), cacheDirectory, UnusedAppHostDirectory, gitClient);

        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_ProjectFileMissing_ThrowsNamingServiceProjectAndRoot()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(project: "src/Missing.csproj"), DevConfig(path: repoDir), "/unused/cache", UnusedAppHostDirectory, new FakeGitClient()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("src/Missing.csproj", ex.Message);
        Assert.Contains(repoDir, ex.Message);
    }

    [Fact]
    public void ResolveProjectPath_CloneFails_WrapsAsConfigurationExceptionNamingServiceAndRepository()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient { CloneException = new InvalidOperationException("network unreachable") };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(repository: "https://github.com/company/orders"), DevConfig(), cacheDirectory, UnusedAppHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("https://github.com/company/orders", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void ResolveProjectPath_CheckoutFails_WrapsAsConfigurationExceptionNamingServiceAndRef()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient
        {
            CheckoutException = new ServiceSourcesConfigurationException(
                "Ref 'missing-ref' was not found in repository at '/tmp/x'."),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(defaultRef: "missing-ref"), DevConfig(), cacheDirectory, UnusedAppHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("missing-ref", ex.Message);
        Assert.IsType<ServiceSourcesConfigurationException>(ex.InnerException);
    }

    [Fact]
    public void ResolveProjectPath_CacheDirectoryOriginMismatch_ThrowsNamingServiceAndBothUrls()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(cacheDirectory, "orders");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");

        var gitClient = new FakeGitClient();
        gitClient.OriginUrlsByPath[repoDir] = "https://github.com/team-b/orders";

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                ServiceName, Metadata(repository: "https://github.com/team-a/orders"), DevConfig(), cacheDirectory, UnusedAppHostDirectory, gitClient));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("https://github.com/team-a/orders", ex.Message);
        Assert.Contains("https://github.com/team-b/orders", ex.Message);
        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_CacheDirectoryOriginUnknown_DoesNotThrow()
    {
        // Simulates a cache-hit directory whose origin cannot be determined (e.g. not a git
        // repo, or no "origin" remote) — must not block resolution, and must never re-clone.
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(cacheDirectory, "orders");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        var projectPath = LocalProjectSource.ResolveProjectPath(
            ServiceName, Metadata(repository: "https://github.com/team-a/orders"), DevConfig(), cacheDirectory, UnusedAppHostDirectory, gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
        Assert.Empty(gitClient.ClonedRepos);
    }
}
