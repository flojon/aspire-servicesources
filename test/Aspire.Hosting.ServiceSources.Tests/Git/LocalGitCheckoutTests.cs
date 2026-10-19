using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The questions about a service's checkout that are answerable without resolving anything — from
/// the AppHost directory, the service name, the developer's configuration, or the catalog entry.
/// Pinned here rather than only through their callers because the speculative prefetch and the
/// deferral decision are both built on the same answer, for services nobody has added yet, and the
/// prefetch's filter (#76/#177) is only safe while the two agree.
/// </summary>
[Trait("IO", "true")]
public class LocalGitCheckoutTests
{
    private static ServiceDeveloperConfig Managed() =>
        new() { Source = "repository" };

    private static ServiceDeveloperConfig WithPathOverride(string path) =>
        new() { Source = "repository", Repository = new LocalDeveloperConfig { Path = path } };

    private static string NewAppHostDirectory() => TempDirectories.CreateSubdirectory().FullName;

    /// <summary>
    /// A directory to compose paths against, never created on disk — for the tests that only ask
    /// <see cref="LocalGitCheckout.ManagedRepoRoot"/> to build a path. It does no I/O, so a real
    /// temp directory there would be one more the suite leaves behind for nothing.
    /// </summary>
    private static readonly string UnusedAppHostDirectory = Path.Combine(Path.GetTempPath(), "apphost");

    [Fact]
    public void IsManagedCheckout_NoPathOverride_IsTrue() =>
        Assert.True(LocalGitCheckout.IsManagedCheckout(Managed()));

    [Fact]
    public void IsManagedCheckout_PathOverride_IsFalse() =>
        Assert.False(LocalGitCheckout.IsManagedCheckout(WithPathOverride("/somewhere/of/their/own")));

    /// <summary>
    /// The single definition of "is there anything to clone for this service", pinned where it is
    /// named rather than at each of its four callers.
    /// </summary>
    /// <remarks>
    /// The whitespace rows are the load-bearing ones: <c>IsNullOrEmpty</c> would call "   " a
    /// repository and hand it to git.
    /// </remarks>
    [Theory]
    [InlineData("https://github.com/company/orders", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\t\n", false)]
    public void HasRepositoryToClone_TreatsABlankOrWhitespaceUrlAsNothingToClone(
        string url, bool expected)
    {
        var definition = new ServiceDefinition
        {
            Repository = new RepositoryDefinition { Url = url, CheckoutName = "orders" },
            Project = "Orders.csproj",
            Kind = LocalKinds.Dotnet,
            Origin = CatalogOrigin.FromYaml("servicesources.yaml"),
        };

        Assert.Equal(expected, LocalGitCheckout.HasRepositoryToClone(definition));
    }

    [Fact]
    public void IsColdManagedCheckout_NothingAtTheManagedRoot_IsCold()
    {
        var appHostDirectory = NewAppHostDirectory();

        Assert.True(LocalGitCheckout.IsColdManagedCheckout(appHostDirectory, "orders", Managed()));
    }

    [Fact]
    public void IsColdManagedCheckout_ACompleteCheckoutAtTheManagedRoot_IsNotCold()
    {
        var appHostDirectory = NewAppHostDirectory();
        var repoRoot = LocalGitCheckout.ManagedRepoRoot(appHostDirectory, "orders");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

        Assert.False(LocalGitCheckout.IsColdManagedCheckout(appHostDirectory, "orders", Managed()));
    }

    [Fact]
    public void IsColdManagedCheckout_DebrisAtTheManagedRoot_IsNotCold()
    {
        // A directory with no ".git" in it — an interrupted clone. Not cold either: telling that
        // apart from a real working tree, and deciding what to do about it, belongs to
        // PrepareRepoRoot rather than to anything speculating from the path alone.
        var appHostDirectory = NewAppHostDirectory();
        Directory.CreateDirectory(LocalGitCheckout.ManagedRepoRoot(appHostDirectory, "orders"));

        Assert.False(LocalGitCheckout.IsColdManagedCheckout(appHostDirectory, "orders", Managed()));
    }

    [Fact]
    public void IsColdManagedCheckout_PathOverride_IsNotCold()
    {
        // Nothing at the managed root, so the directory probe on its own would say "cold". The
        // override outranks it: that path is the developer's own directory, and this package never
        // clones into it.
        var appHostDirectory = NewAppHostDirectory();
        var theirs = TempDirectories.CreateSubdirectory().FullName;

        Assert.False(LocalGitCheckout.IsColdManagedCheckout(appHostDirectory, "orders", WithPathOverride(theirs)));
    }

    [Fact]
    public void IsColdManagedCheckout_PathOverrideThatDoesNotExist_IsStillNotCold()
    {
        // A stale override is a configuration failure for whoever actually resolves the service,
        // reported there by name. It must not read as a clone waiting to happen: speculating over
        // one used to invent a failure about a repository nobody was going to download (#76).
        var appHostDirectory = NewAppHostDirectory();
        var missing = Path.Combine(appHostDirectory, "gone");

        Assert.False(LocalGitCheckout.IsColdManagedCheckout(appHostDirectory, "orders", WithPathOverride(missing)));
    }

    /// <summary>
    /// The service name becomes a directory name under <c>.servicesources/checkouts/</c>, so one
    /// containing <c>..</c> would put the checkout outside the directory the ignore file and the
    /// build barrier are written to cover. Refused at the one function every route to that path
    /// goes through, rather than trusted to have been validated before it got here (#224).
    /// </summary>
    [Theory]
    [InlineData("../escapee")]
    [InlineData("..\\escapee")]
    [InlineData("/etc/escapee")]
    [InlineData("nested/escapee")]
    [InlineData("..")]
    [InlineData(".")]
    // Windows strips trailing dots and spaces off a path component before resolving it, so each of
    // these reaches the filesystem as ".." or "." there while naming an ordinary directory here.
    // Refused on every platform: the verdict on shared configuration cannot depend on who reads it,
    // and a Linux-only CI would never see the escape.
    [InlineData(".. ")]
    [InlineData("...")]
    [InlineData(". ")]
    [InlineData("  ..  ")]
    public void ManagedRepoRoot_ANameThatIsNotADirectoryNameOfItsOwn_IsRefused(string serviceName)
    {
        var exception = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalGitCheckout.ManagedRepoRoot(UnusedAppHostDirectory, serviceName));

        Assert.Contains("cannot be given a managed checkout directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedRepoRoot_AnOrdinaryName_IsTheCheckoutDirectoryForIt()
    {
        Assert.Equal(
            Path.Combine(UnusedAppHostDirectory, ".servicesources", "checkouts", "orders"),
            LocalGitCheckout.ManagedRepoRoot(UnusedAppHostDirectory, "orders"));
    }

    /// <summary>
    /// The criterion-6 guard the design's Testing section names explicitly (#291): an ungrouped
    /// service's checkout path must stay byte-identical to what it was before grouping existed, both
    /// before and after a <see cref="Config.Catalog.RepositoryDefinition"/> exists to route the path
    /// through. Keeping this green through the rest of Stage 2 is what makes "an existing catalog,
    /// unmodified, keeps every developer's working tree exactly where it already is" true rather than
    /// asserted.
    /// </summary>
    [Fact]
    public void UngroupedService_ManagedRepoRootIsExactlyCheckoutsService_BeforeAndAfter()
    {
        // Before: the plain string a caller with no RepositoryDefinition in hand would use — the
        // shape every route into ManagedRepoRoot took prior to #291.
        var beforePath = LocalGitCheckout.ManagedRepoRoot(UnusedAppHostDirectory, "orders");

        // After: an ungrouped service's own anonymous RepositoryDefinition, whose CheckoutName is
        // always the owning service's own name (design finding 2) — the value every real caller
        // (LocalProjectSource, LocalCheckoutPrefetch, DeferredCheckout) now passes.
        var repository = new RepositoryDefinition { Url = "https://example.com/orders.git", CheckoutName = "orders" };
        var afterPath = LocalGitCheckout.ManagedRepoRoot(UnusedAppHostDirectory, repository.CheckoutName);

        Assert.Equal(beforePath, afterPath);
        Assert.Equal(Path.Combine(UnusedAppHostDirectory, ".servicesources", "checkouts", "orders"), afterPath);
    }

    private static ServiceDefinition OrdersDefinition() => new()
    {
        Repository = new RepositoryDefinition { Url = "https://github.com/company/orders", CheckoutName = "orders" },
        Project = "Orders.csproj",
        Kind = LocalKinds.Dotnet,
        Origin = CatalogOrigin.FromYaml("servicesources.yaml"),
    };

    [Fact]
    public void ResolveRepoRoot_ExistingCheckout_BackfillsLongPaths()
    {
        var appHostDirectory = NewAppHostDirectory();
        var repoRoot = LocalGitCheckout.ManagedRepoRoot(appHostDirectory, "orders");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));
        var gitClient = new RecordingGitClient();

        LocalGitCheckout.ResolveRepoRoot("orders", OrdersDefinition(), Managed(), null, appHostDirectory, gitClient);

        Assert.Equal([repoRoot], gitClient.LongPathsPersisted);
    }

    [Fact]
    public void ResolveRepoRoot_ExistingCheckoutOfAnotherRepository_IsNotWrittenTo()
    {
        var appHostDirectory = NewAppHostDirectory();
        Directory.CreateDirectory(Path.Combine(LocalGitCheckout.ManagedRepoRoot(appHostDirectory, "orders"), ".git"));
        var gitClient = new RecordingGitClient { OriginUrl = "https://github.com/company/billing" };

        Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalGitCheckout.ResolveRepoRoot("orders", OrdersDefinition(), Managed(), null, appHostDirectory, gitClient));

        Assert.Empty(gitClient.LongPathsPersisted);
    }

    [Fact]
    public void PrepareRepoRoot_SweepRemovingALeftover_ReportsItToTheProgressSink()
    {
        var appHostDirectory = NewAppHostDirectory();
        var checkoutsRoot = Path.GetDirectoryName(LocalGitCheckout.ManagedRepoRoot(appHostDirectory, "orders"))!;
        var leftover = Path.Combine(checkoutsRoot, ".incoming-deadbeef");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(leftover + ".lock", "");
        var progress = new RecordingProgressSink();

        LocalGitCheckout.PrepareRepoRoot(
            "orders", OrdersDefinition(), Managed(), null, appHostDirectory, new RecordingGitClient(), progress);

        Assert.Contains(progress.Lines, line => line.StartsWith("Removed 1 leftover clone staging directory", StringComparison.Ordinal));
    }

    /// <summary>The check sits in front of the clone, so git is never asked to fail on it.</summary>
    [Fact]
    public void OnWindows_PrepareRepoRoot_TooDeepAppHost_RefusesBeforeCloning()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var appHostDirectory = Path.Combine(NewAppHostDirectory(), new string('d', 100), new string('e', 100));
        Directory.CreateDirectory(appHostDirectory);
        var gitClient = new RecordingGitClient();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalGitCheckout.PrepareRepoRoot("orders", OrdersDefinition(), Managed(), null, appHostDirectory, gitClient));

        Assert.Contains("limits them to 259", ex.Message, StringComparison.Ordinal);
        Assert.Empty(gitClient.Cloned);
    }

    private sealed class RecordingGitClient : IGitClient
    {
        public List<string> LongPathsPersisted { get; } = [];

        public List<string> Cloned { get; } = [];

        public string? OriginUrl { get; init; }

        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
        {
            Cloned.Add(destinationPath);
            Directory.CreateDirectory(Path.Combine(destinationPath, ".git"));
        }

        public void Checkout(string repositoryPath, string reference)
        {
        }

        public void Fetch(string repositoryPath)
        {
        }

        public bool HasUncommittedChanges(string repositoryPath) => false;

        public bool IsRefCheckedOut(string repositoryPath, string reference) => true;

        public string? GetOriginUrl(string repositoryPath) => OriginUrl;

        public void PersistLongPaths(string repositoryPath) => LongPathsPersisted.Add(repositoryPath);
    }
}
