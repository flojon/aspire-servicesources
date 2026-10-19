using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Messages;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

[Trait("IO", "true")]
public class PackPathLimitTests
{
    private const int PackSuffixLength = 69;

    private static readonly Raw Label = Raw.Literal("Service 'orders'");

    /// <summary>A checkouts root whose full path is exactly <paramref name="length"/> characters.</summary>
    private static string RootOfLength(int length)
    {
        var start = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var padding = length - start.Length - 1;
        return Path.Combine(start, new string('a', padding));
    }

    [Fact]
    public void LongestPackPath_ShortCheckoutName_IsTheStagingDirectoryPlusThePackFile()
    {
        var root = RootOfLength(100);

        var longest = PackPathLimit.LongestPackPath(root, Path.Combine(root, "orders"));

        Assert.Equal(100 + ".incoming-00000000".Length + 1 + PackSuffixLength, longest.Length);
        Assert.EndsWith(".pack", longest, StringComparison.Ordinal);
    }

    [Fact]
    public void LongestPackPath_CheckoutNameLongerThanTheStagingName_IsTheCheckoutPlusThePackFile()
    {
        var root = RootOfLength(100);
        var repoRoot = Path.Combine(root, new string('n', 40));

        Assert.Equal(repoRoot.Length + PackSuffixLength, PackPathLimit.LongestPackPath(root, repoRoot).Length);
    }

    [Fact]
    public void EnsureFits_AtTheLimit_DoesNotThrow()
    {
        var root = RootOfLength(PackPathLimit.MaxLength - PackSuffixLength - ".incoming-00000000".Length - 1);

        PackPathLimit.EnsureFits(Label, root, Path.Combine(root, "orders"), isWindows: true);
    }

    [Fact]
    public void EnsureFits_OneOverTheLimit_ThrowsNamingTheLimitTheLengthAndTheFix()
    {
        var root = RootOfLength(PackPathLimit.MaxLength - PackSuffixLength - ".incoming-00000000".Length);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => PackPathLimit.EnsureFits(Label, root, Path.Combine(root, "orders"), isWindows: true));

        Assert.Contains("Service 'orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("paths of 260 characters", ex.Message, StringComparison.Ordinal);
        Assert.Contains("limits them to 259", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Move the AppHost, or the git worktree it is in", ex.Message, StringComparison.Ordinal);
        Assert.Contains("at least 1 characters shorter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureFits_OffWindows_NeverThrows()
    {
        var root = RootOfLength(400);

        PackPathLimit.EnsureFits(Label, root, Path.Combine(root, "orders"), isWindows: false);
    }

    /// <summary>The check sits in front of the clone, so git is never asked to fail on it.</summary>
    [Fact]
    public void OnWindows_PrepareRepoRoot_TooDeepAppHost_RefusesBeforeCloning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appHostDirectory = Path.Combine(
            TempDirectories.CreateSubdirectory().FullName, new string('d', 100), new string('e', 100));
        Directory.CreateDirectory(appHostDirectory);
        var gitClient = new CloneRefusingGitClient();
        var definition = new ServiceDefinition
        {
            Repository = new RepositoryDefinition { Url = "https://github.com/company/orders", CheckoutName = "orders" },
            Project = "Orders.csproj",
            Kind = LocalKinds.Dotnet,
            Origin = CatalogOrigin.FromYaml("servicesources.yaml"),
        };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalGitCheckout.PrepareRepoRoot(
                "orders", definition, new ServiceDeveloperConfig { Source = "repository" }, repositoryConfig: null,
                appHostDirectory, gitClient));

        Assert.Contains("limits them to 259", ex.Message, StringComparison.Ordinal);
        Assert.False(gitClient.CloneCalled);
    }

    private sealed class CloneRefusingGitClient : IGitClient
    {
        public bool CloneCalled { get; private set; }

        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
            CloneCalled = true;
            throw new InvalidOperationException("clone should not have been attempted");
        }

        public void Checkout(string repositoryPath, string reference) => throw new NotImplementedException();

        public void Fetch(string repositoryPath) => throw new NotImplementedException();

        public bool HasUncommittedChanges(string repositoryPath) => throw new NotImplementedException();

        public bool IsRefCheckedOut(string repositoryPath, string reference) => throw new NotImplementedException();

        public string? GetOriginUrl(string repositoryPath) => throw new NotImplementedException();
    }
}
