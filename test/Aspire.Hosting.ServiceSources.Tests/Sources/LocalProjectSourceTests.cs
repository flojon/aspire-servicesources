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

        public void Clone(string repositoryUrl, string destinationPath)
        {
            ClonedRepos.Add((repositoryUrl, destinationPath));
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "Orders.csproj"), "<Project />");
        }

        public void Checkout(string repositoryPath, string reference)
        {
            CheckedOutRefs.Add((repositoryPath, reference));
        }
    }

    private static ServiceMetadata Metadata(string repository = "https://github.com/company/orders", string project = "Orders.csproj", string? defaultRef = null) =>
        new() { Repository = repository, Project = project, DefaultRef = defaultRef };

    private static ServiceDeveloperConfig DevConfig(string? path = null, string? @ref = null) =>
        new() { Source = "local", Path = path, Ref = @ref };

    [Fact]
    public void ResolveProjectPath_PathIsSet_UsesItDirectlyWithoutTouchingGit()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        var projectPath = LocalProjectSource.ResolveProjectPath(
            Metadata(project: "Orders.csproj"), DevConfig(path: repoDir), "/unused/cache", gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_ClonesIntoCacheDirectoryUnderRepoName()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var projectPath = LocalProjectSource.ResolveProjectPath(
            Metadata(repository: "https://github.com/company/orders"), DevConfig(), cacheDirectory, gitClient);

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
            Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), cacheDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("feature/x", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_FallsBackToCatalogDefaultRefWhenDeveloperRefUnset()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            Metadata(defaultRef: "main"), DevConfig(@ref: null), cacheDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("main", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_NoRefConfigured_SkipsCheckout()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            Metadata(defaultRef: null), DevConfig(@ref: null), cacheDirectory, gitClient);

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
            Metadata(defaultRef: "main"), DevConfig(), cacheDirectory, gitClient);

        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_ProjectFileMissing_ThrowsNamingProjectAndRoot()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                Metadata(project: "src/Missing.csproj"), DevConfig(path: repoDir), "/unused/cache", new FakeGitClient()));

        Assert.Contains("src/Missing.csproj", ex.Message);
        Assert.Contains(repoDir, ex.Message);
    }
}
