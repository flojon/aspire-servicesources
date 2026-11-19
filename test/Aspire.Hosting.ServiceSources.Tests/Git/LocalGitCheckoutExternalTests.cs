using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Sources;
using Aspire.Hosting.ServiceSources.Tests.Worktrees;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>Managed checkouts placed by <c>ServiceSources:CheckoutsDirectory</c>, through a real git.</summary>
[Trait("IO", "true")]
public class LocalGitCheckoutExternalTests
{
    private const string Url = "https://example.com/orders.git";

    private static ServiceDefinition Definition(string url) => LocalGitCheckoutSeparateTests.Definition(url);

    private static ServiceDeveloperConfig DevConfig() => new() { Source = "repository" };

    private static (string Root, string AppHost, CloneLayoutOptions Layout) NewEnvironment()
    {
        var temp = TempDirectories.CreateSubdirectory().FullName;
        return (
            Path.Combine(temp, "ss"),
            Path.Combine(temp, "apphost"),
            new CloneLayoutOptions(Path.Combine(temp, "gitroot"), IsWindows: false));
    }

    private static LocalGitCheckout.PreparedCheckout Prepare(
        string appHost, CheckoutsLocation location, IGitClient git, string url) =>
        LocalGitCheckout.PrepareRepoRoot(
            "orders", Definition(url), DevConfig(), repositoryConfig: null, appHost, location, git,
            progress: null, homeAppHostDirectory: null, layout: new CloneLayoutOptions(null, IsWindows: false));

    [Fact]
    public void Managed_checkout_lands_under_root_hash_checkouts_name()
    {
        var (root, appHost, _) = NewEnvironment();
        var location = CheckoutsLocation.Resolve(appHost, root);

        var prepared = Prepare(appHost, location, LocalGitCheckoutSeparateTests.RealGit(), LocalGitCheckoutSeparateTests.OriginUrl());

        var expected = Path.Combine(root, CheckoutsLocation.HashOf(appHost), "checkouts", "orders");
        Assert.Equal(expected, prepared.RepoRoot);
        Assert.Equal("main content", TestRepository.At(expected).Read("file.txt"));
        Assert.True(File.Exists(Path.Combine(root, CheckoutsLocation.HashOf(appHost), CheckoutsLocation.OwnerFileName)));
        Assert.False(Directory.Exists(Path.Combine(appHost, ".servicesources", "checkouts")));
        Assert.Same(location, prepared.Location);
    }

    [Fact]
    public void Default_location_path_is_unchanged()
    {
        var (_, appHost, _) = NewEnvironment();
        var location = CheckoutsLocation.Default(appHost);

        var prepared = Prepare(appHost, location, LocalGitCheckoutSeparateTests.RealGit(), LocalGitCheckoutSeparateTests.OriginUrl());

        Assert.Equal(Path.Combine(appHost, ".servicesources", "checkouts", "orders"), prepared.RepoRoot);
    }

    [Fact]
    public void Problem_location_throws_from_PrepareRepoRoot_before_any_write()
    {
        var (_, appHost, _) = NewEnvironment();
        var location = CheckoutsLocation.Resolve(appHost, "relative");
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => Prepare(appHost, location, git, Url));

        Assert.Contains(DeveloperConfiguration.CheckoutsDirectoryKey, ex.Message);
        Assert.False(Directory.Exists(appHost));
        Assert.Empty(git.PlainClones);
    }

    [Fact]
    public void A_path_override_service_needs_no_checkouts_directory_even_when_the_setting_is_bad()
    {
        var (_, appHost, _) = NewEnvironment();
        var own = TempDirectories.CreateSubdirectory().FullName;
        var location = CheckoutsLocation.Resolve(appHost, "relative");
        var config = new ServiceDeveloperConfig { Source = "repository", Repository = new LocalDeveloperConfig { Path = own } };

        var prepared = LocalGitCheckout.PrepareRepoRoot(
            "orders", Definition(Url), config, repositoryConfig: null, appHost, location, new ScriptedCloneGitClient());

        Assert.Equal(own, prepared.RepoRoot);
    }

    [Fact]
    public void Path_checkout_markers_and_selection_json_stay_under_the_apphost()
    {
        var (root, appHost, _) = NewEnvironment();
        var location = CheckoutsLocation.Resolve(appHost, root);

        var marker = PrepareMarker.LocationFor("orders", Path.Combine(root, "own"), appHost, managedCheckout: false);

        Assert.StartsWith(ToolDirectory.PathIn(appHost), marker);
        Assert.DoesNotContain(location.OwnedDirectory, marker);
    }

    [Fact]
    public void Prefetch_with_a_problem_location_does_not_throw_and_AddService_reports_it()
    {
        var appHost = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(
            Path.Combine(appHost, "servicesources.yaml"),
            $"services:\n  orders:\n    repository: {Url}\n    project: Service.csproj\n");
        File.WriteAllText(
            Path.Combine(appHost, "servicesources.local.json"),
            """{ "checkoutsDirectory": "relative", "services": { "orders": { "source": "repository" } } }""");
        var builder = TestHelpers.CreateBuilder(appHost);
        builder.SetCheckoutTiming(CheckoutTiming.Eager);
        var git = new ScriptedCloneGitClient { HomeOrigin = Url };

        var prefetch = LocalCheckoutPrefetch.For(builder, git);
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.NotNull(prefetch);
        Assert.Contains(DeveloperConfiguration.CheckoutsDirectoryKey, ex.Message);
        Assert.Contains("relative", ex.Message);
        Assert.Empty(git.PlainClones);
    }
}
