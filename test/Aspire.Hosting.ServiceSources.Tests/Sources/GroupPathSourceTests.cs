using System.Text.Json.Nodes;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// A <c>repositories.&lt;name&gt;</c> entry with <c>"source": "path"</c> points every grouped member
/// that would use the group's checkout at one directory.
/// </summary>
[Trait("IO", "true")]
public class GroupPathSourceTests
{
    private const string Catalog = """
        repositories:
          monorepo:
            repository: https://example.com/monorepo.git
        services:
          orders:
            repositoryRef: monorepo
            project: src/Orders/Orders.csproj
          basket:
            repositoryRef: monorepo
            project: src/Basket/Basket.csproj
          payments:
            repositoryRef: monorepo
            project: src/Payments/Payments.csproj
            container:
              image: ghcr.io/company/payments
              port: 8080
          solo:
            repository: https://example.com/solo.git
            project: src/Solo/Solo.csproj
        """;

    private static string ProjectTree(params string[] projects)
    {
        var root = TempDirectories.CreateSubdirectory().FullName;

        foreach (var project in projects)
        {
            var directory = Directory.CreateDirectory(Path.Combine(root, "src", project)).FullName;
            File.WriteAllText(Path.Combine(directory, $"{project}.csproj"), "<Project />");
        }

        return root;
    }

    private static string Fixture(JsonObject local, out string appHost, string catalog = Catalog)
    {
        appHost = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHost, "servicesources.yaml"), catalog);
        File.WriteAllText(Path.Combine(appHost, "servicesources.local.json"), local.ToJsonString());

        return appHost;
    }

    private static JsonObject Repository(string source) => new() { ["source"] = source };

    private static JsonObject Group(string? directory, string source = "path", string? @ref = null)
    {
        var entry = new JsonObject { ["source"] = source };

        if (directory is not null)
        {
            entry["path"] = new JsonObject { ["path"] = directory };
        }

        if (@ref is not null)
        {
            entry["ref"] = @ref;
        }

        return entry;
    }

    private static JsonObject Local(JsonObject group, JsonObject services) =>
        new() { ["services"] = services, ["repositories"] = new JsonObject { ["monorepo"] = group } };

    private static string ProjectPathOf(IDistributedApplicationBuilder builder, string name) =>
        Assert.Single(
            Assert.IsAssignableFrom<ProjectResource>(Assert.Single(builder.Resources, r => r.Name == name))
                .Annotations.OfType<IProjectMetadata>()).ProjectPath;

    [Fact]
    public void GroupPath_RedirectsEveryRepositorySourcedMemberToTheGroupDirectory()
    {
        var groupDir = ProjectTree("Orders", "Basket");
        Fixture(
            Local(Group(groupDir), new JsonObject { ["orders"] = Repository("repository"), ["basket"] = Repository("repository") }),
            out var appHost);
        var builder = TestHelpers.CreateBuilder(appHost);

        builder.AddService("orders");
        builder.AddService("basket");

        Assert.Equal(Path.Combine(groupDir, "src", "Orders", "Orders.csproj"), ProjectPathOf(builder, "orders"));
        Assert.Equal(Path.Combine(groupDir, "src", "Basket", "Basket.csproj"), ProjectPathOf(builder, "basket"));
        Assert.False(Directory.Exists(Path.Combine(appHost, ".servicesources", "checkouts", "monorepo")));
    }

    [Fact]
    public void GroupPath_OnAMemberAlreadyOnPath_SuppliesTheDirectoryItLacks()
    {
        var groupDir = ProjectTree("Orders");
        Fixture(Local(Group(groupDir), new JsonObject { ["orders"] = Repository("path") }), out var appHost);
        var builder = TestHelpers.CreateBuilder(appHost);

        builder.AddService("orders");

        Assert.Equal(Path.Combine(groupDir, "src", "Orders", "Orders.csproj"), ProjectPathOf(builder, "orders"));
    }

    [Fact]
    public void GroupPath_RelativeValue_AnchorsToTheAppHostDirectory()
    {
        var groupDir = ProjectTree("Orders");
        var appHost = TempDirectories.CreateSubdirectory().FullName;
        var relative = Path.GetRelativePath(appHost, groupDir);
        Assert.NotEqual(Path.GetFullPath(relative), groupDir);
        File.WriteAllText(Path.Combine(appHost, "servicesources.yaml"), Catalog);
        File.WriteAllText(
            Path.Combine(appHost, "servicesources.local.json"),
            Local(Group(relative), new JsonObject { ["orders"] = Repository("repository") }).ToJsonString());
        var builder = TestHelpers.CreateBuilder(appHost);

        builder.AddService("orders");

        Assert.Equal(Path.Combine(groupDir, "src", "Orders", "Orders.csproj"), ProjectPathOf(builder, "orders"));
    }

    [Fact]
    public void GroupPath_MissingDirectory_NamesTheRepositoryAndTheKeyNotTheMember()
    {
        var missing = Path.Combine(TempDirectories.CreateSubdirectory().FullName, "nowhere");
        Fixture(Local(Group(missing), new JsonObject { ["orders"] = Repository("repository") }), out var appHost);
        var builder = TestHelpers.CreateBuilder(appHost);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("Repository 'monorepo'", ex.Message);
        Assert.Contains("ServiceSources:Repositories:monorepo:path:path", ex.Message);
        Assert.DoesNotContain("Service 'orders'", ex.Message);
    }

    [Fact]
    public void GroupPathSourceWithoutADirectory_NamesTheKeyToSet()
    {
        Fixture(Local(Group(directory: null), new JsonObject { ["orders"] = Repository("repository") }), out var appHost);
        var builder = TestHelpers.CreateBuilder(appHost);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("Repository 'monorepo'", ex.Message);
        Assert.Contains("ServiceSources:Repositories:monorepo:path:path", ex.Message);
    }

    [Fact]
    public void GroupSourceRepository_LeavesMembersOnTheManagedCheckout()
    {
        var groupDir = ProjectTree("Orders");
        Fixture(Local(Group(groupDir, source: "repository"), new JsonObject { ["orders"] = Repository("repository") }), out var appHost);
        var builder = TestHelpers.CreateBuilder(appHost);

        builder.AddService("orders");

        var planned = Path.Combine(appHost, ".servicesources", "checkouts", "monorepo", "src", "Orders", "Orders.csproj");
        Assert.Equal(planned, Path.GetFullPath(ProjectPathOf(builder, "orders")));
    }

    [Fact]
    public void MemberPathPath_BeatsTheGroup_AndTheSiblingStillGetsTheGroup()
    {
        var groupDir = ProjectTree("Orders", "Basket");
        var own = ProjectTree("Orders");
        Fixture(
            Local(Group(groupDir), new JsonObject
            {
                ["orders"] = new JsonObject { ["source"] = "path", ["path"] = new JsonObject { ["path"] = own } },
                ["basket"] = Repository("repository"),
            }),
            out var appHost);
        var builder = TestHelpers.CreateBuilder(appHost);

        builder.AddService("orders");
        builder.AddService("basket");

        Assert.Equal(Path.Combine(own, "src", "Orders", "Orders.csproj"), ProjectPathOf(builder, "orders"));
        Assert.Equal(Path.Combine(groupDir, "src", "Basket", "Basket.csproj"), ProjectPathOf(builder, "basket"));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("local")]
    public void MemberRepositoryPath_BeatsTheGroup_AndTheSiblingStillGetsTheGroup(string block)
    {
        var groupDir = ProjectTree("Orders", "Basket");
        var own = ProjectTree("Orders");
        Fixture(
            Local(Group(groupDir), new JsonObject
            {
                ["orders"] = new JsonObject { ["source"] = "repository", [block] = new JsonObject { ["path"] = own } },
                ["basket"] = Repository("repository"),
            }),
            out var appHost);
        var builder = TestHelpers.CreateBuilder(appHost);

        builder.AddService("orders");
        builder.AddService("basket");

        Assert.Equal(Path.Combine(own, "src", "Orders", "Orders.csproj"), ProjectPathOf(builder, "orders"));
        Assert.Equal(Path.Combine(groupDir, "src", "Basket", "Basket.csproj"), ProjectPathOf(builder, "basket"));
    }

    [Fact]
    public void ContainerAndUrlMembers_AreLeftUntouched()
    {
        var groupDir = ProjectTree("Orders");
        Fixture(
            Local(Group(groupDir), new JsonObject
            {
                ["orders"] = new JsonObject { ["source"] = "url", ["url"] = new JsonObject { ["url"] = "https://orders.example.com" } },
                ["payments"] = Repository("container"),
            }),
            out var appHost);
        var builder = TestHelpers.CreateBuilder(appHost);

        builder.AddService("orders");
        builder.AddService("payments");

        Assert.DoesNotContain(builder.Resources, r => r is ProjectResource);
        Assert.IsAssignableFrom<ContainerResource>(Assert.Single(builder.Resources, r => r.Name == "payments"));
    }

    [Fact]
    public void UngroupedServiceWithItsOwnRepository_IsNotReachedByTheGroup()
    {
        var groupDir = ProjectTree("Orders");
        Fixture(Local(Group(groupDir), new JsonObject { ["solo"] = Repository("repository") }), out var appHost);
        var builder = TestHelpers.CreateBuilder(appHost);

        builder.AddService("solo");

        var planned = Path.Combine(appHost, ".servicesources", "checkouts", "solo", "src", "Solo", "Solo.csproj");
        Assert.Equal(planned, Path.GetFullPath(ProjectPathOf(builder, "solo")));
    }

    [Fact]
    public void ServiceNamedLikeAGroupedRepository_IsRefusedAtLoad_SoTheGroupCannotReachIt()
    {
        var groupDir = ProjectTree("Billing");
        Fixture(
            new JsonObject
            {
                ["services"] = new JsonObject { ["billing"] = Repository("repository") },
                ["repositories"] = new JsonObject { ["billing"] = Group(groupDir) },
            },
            out var appHost,
            catalog: """
                repositories:
                  billing:
                    repository: https://example.com/billing.git
                services:
                  billing:
                    repository: https://example.com/billing-own.git
                    project: src/Billing/Billing.csproj
                """);
        var builder = TestHelpers.CreateBuilder(appHost);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("billing"));

        Assert.Contains("names both an ungrouped service", ex.Message);
    }

    [Fact]
    public void MemberOwnRepositoryRef_IsStillRefusedWhenTheGroupIsOnPath()
    {
        var groupDir = ProjectTree("Orders");
        Fixture(
            Local(Group(groupDir), new JsonObject
            {
                ["orders"] = new JsonObject { ["source"] = "repository", ["repository"] = new JsonObject { ["ref"] = "feature/x" } },
            }),
            out var appHost);
        var builder = TestHelpers.CreateBuilder(appHost);
        builder.SetCheckoutTiming(CheckoutTiming.Eager);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("'repository.ref' cannot be set", ex.Message);
    }

    [Fact]
    public void MemberProjectEscapingTheGroupDirectory_IsRefused()
    {
        var groupDir = ProjectTree("Orders");
        var outside = Path.Combine(Path.GetDirectoryName(groupDir)!, "escape");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Escape.csproj"), "<Project />");
        Fixture(
            Local(Group(groupDir), new JsonObject { ["orders"] = Repository("repository") }),
            out var appHost,
            catalog: """
                repositories:
                  monorepo:
                    repository: https://example.com/monorepo.git
                services:
                  orders:
                    repositoryRef: monorepo
                    project: ../escape/Escape.csproj
                """);
        var builder = TestHelpers.CreateBuilder(appHost);

        Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));
    }

    [Fact]
    public void PathMemberWhoseCatalogDefinesPath_IsDisplacedByTheGroupDirectory()
    {
        var groupDir = ProjectTree("Orders");
        Fixture(
            Local(Group(groupDir), new JsonObject { ["orders"] = Repository("path") }),
            out var appHost,
            catalog: """
                repositories:
                  monorepo:
                    repository: https://example.com/monorepo.git
                services:
                  orders:
                    repositoryRef: monorepo
                    path: legacy/orders
                    project: src/Orders/Orders.csproj
                """);
        var builder = TestHelpers.CreateBuilder(appHost);

        builder.AddService("orders");

        Assert.Equal(Path.Combine(groupDir, "src", "Orders", "Orders.csproj"), ProjectPathOf(builder, "orders"));
    }

    [Fact]
    public void GroupRef_BesideAPathSource_IsIgnoredRatherThanAnError()
    {
        var groupDir = ProjectTree("Orders");
        Fixture(
            Local(Group(groupDir, @ref: "feature/x"), new JsonObject { ["orders"] = Repository("repository") }),
            out var appHost);
        var builder = TestHelpers.CreateBuilder(appHost);

        builder.AddService("orders");

        Assert.Equal(Path.Combine(groupDir, "src", "Orders", "Orders.csproj"), ProjectPathOf(builder, "orders"));
    }

    [Fact]
    public void GroupSettingsFromHigherLayers_RedirectMembers()
    {
        var groupDir = ProjectTree("Orders");
        var appHost = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHost, "servicesources.yaml"), Catalog);
        File.WriteAllText(
            Path.Combine(appHost, "servicesources.local.json"),
            new JsonObject { ["services"] = new JsonObject { ["orders"] = Repository("repository") } }.ToJsonString());
        var builder = TestHelpers.CreateBuilder(appHost);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceSources:Repositories:monorepo:Source"] = "path",
            ["ServiceSources:Repositories:monorepo:Path:Path"] = groupDir,
        });

        builder.AddService("orders");

        Assert.Equal(Path.Combine(groupDir, "src", "Orders", "Orders.csproj"), ProjectPathOf(builder, "orders"));
    }

    [Fact]
    public async Task MemberOwnRepositoryPath_StillGetsTheDeprecationNotice_PointingAtTheGroupForm()
    {
        var groupDir = ProjectTree("Orders");
        var own = ProjectTree("Basket");
        Fixture(
            Local(Group(groupDir), new JsonObject
            {
                ["basket"] = new JsonObject { ["source"] = "repository", ["repository"] = new JsonObject { ["path"] = own } },
            }),
            out var appHost);
        var builder = TestHelpers.CreateBuilderThatCanStart(appHost);

        builder.AddService("basket");

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        var notice = Assert.Single(warnings, w => w.Contains("'repository.path' is deprecated", StringComparison.Ordinal));
        Assert.Contains("repositories.monorepo", notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GroupPathSource_EmitsNoDeprecationNoticeAndNoneGroupedWarning()
    {
        var groupDir = ProjectTree("Orders", "Basket");
        Fixture(
            Local(Group(groupDir), new JsonObject { ["orders"] = Repository("repository"), ["basket"] = Repository("repository") }),
            out var appHost);
        var builder = TestHelpers.CreateBuilderThatCanStart(appHost);

        builder.AddService("orders");
        builder.AddService("basket");

        Assert.Empty(await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder));
    }

    [Fact]
    public async Task UngroupedSameRepositoryEntries_StillGetTheNoneGroupedWarning()
    {
        var appHost = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHost, "servicesources.yaml"), """
            services:
              orders:
                repository: https://example.com/monorepo.git
                project: src/Orders/Orders.csproj
              basket:
                repository: https://example.com/monorepo.git
                project: src/Basket/Basket.csproj
            """);
        File.WriteAllText(
            Path.Combine(appHost, "servicesources.local.json"),
            new JsonObject
            {
                ["services"] = new JsonObject { ["orders"] = Repository("repository"), ["basket"] = Repository("repository") },
            }.ToJsonString());
        var builder = TestHelpers.CreateBuilderThatCanStart(appHost);
        ServiceSourcesConfigCache.ResolveService(builder, "orders");
        ServiceSourcesConfigCache.ResolveService(builder, "basket");

        var warnings = await TestHelpers.PublishBeforeStartEventCapturingWarningsAsync(builder);

        Assert.Contains(warnings, w => w.Contains("none of them are grouped", StringComparison.Ordinal));
    }

    // === Redirects: the rule the above rely on ===

    private static ServiceDefinition Grouped(string checkoutName = "monorepo") =>
        new()
        {
            Repository = new RepositoryDefinition { Url = "https://example.com/monorepo.git", CheckoutName = checkoutName },
            Project = "src/Orders/Orders.csproj",
            Kind = LocalKinds.Dotnet,
            Origin = CatalogOrigin.FromYaml("servicesources.yaml"),
        };

    private static RepositoryDeveloperConfig PathGroup(string source = "path") =>
        new() { Source = source, Path = new() { Path = "/group" } };

    [Theory]
    [InlineData("repository", null, null, true)]
    [InlineData("Repository", null, null, true)]
    [InlineData("path", null, null, true)]
    [InlineData("repository", "/own", null, false)]
    [InlineData("path", null, "/own", false)]
    [InlineData("url", null, null, false)]
    [InlineData("container", null, null, false)]
    [InlineData("kubernetes", null, null, false)]
    [InlineData("disabled", null, null, false)]
    public void Redirects_DependsOnTheMembersSourceAndItsOwnDirectory(
        string source, string? repositoryPath, string? pathPath, bool expected)
    {
        var config = new ServiceDeveloperConfig
        {
            Source = source,
            Repository = new() { Path = repositoryPath },
            Path = new() { Path = pathPath },
        };

        Assert.Equal(expected, GroupPathSource.Redirects("orders", Grouped(), config, PathGroup()));
    }

    [Fact]
    public void Redirects_IsFalseForARepositoryMemberThatSetsItsOwnRef()
    {
        var config = new ServiceDeveloperConfig { Source = "repository", Repository = new() { Ref = "feature/x" } };

        Assert.False(GroupPathSource.Redirects("orders", Grouped(), config, PathGroup()));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData(null)]
    public void Redirects_IsFalseUnlessTheGroupIsOnPath(string? groupSource)
    {
        var config = new ServiceDeveloperConfig { Source = "repository" };
        var group = new RepositoryDeveloperConfig { Source = groupSource, Path = new() { Path = "/group" } };

        Assert.False(GroupPathSource.Redirects("orders", Grouped(), config, group));
        Assert.False(GroupPathSource.Redirects("orders", Grouped(), config, repositoryConfig: null));
    }

    [Fact]
    public void Redirects_IsFalseForAnUngroupedServiceWhoseNameIsTheRepositoryName()
    {
        var config = new ServiceDeveloperConfig { Source = "repository" };

        Assert.False(GroupPathSource.Redirects("monorepo", Grouped("monorepo"), config, PathGroup()));
    }

    // === members sharing the group directory share one build gate ===

    private sealed class ConcurrencyTrackingRunner : IBuildRunner
    {
        private int _running;

        public int MaxConcurrent { get; private set; }

        public int Builds { get; private set; }

        public async Task<int> RunAsync(
            string projectFile, string? configuration, Action<string> onLine, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _running);

            lock (this)
            {
                MaxConcurrent = Math.Max(MaxConcurrent, now);
                Builds++;
            }

            await Task.Delay(100, cancellationToken);
            Interlocked.Decrement(ref _running);

            return 0;
        }
    }

    [Fact]
    public async Task GroupMembers_ShareOneBuildGateKey_AndBuildOneAtATime()
    {
        var groupDir = ProjectTree("Orders", "Basket");
        Directory.CreateDirectory(Path.Combine(groupDir, ".git"));
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var runner = new ConcurrencyTrackingRunner();
        var source = new PathSource(buildRunner: runner);
        var group = PathGroup();
        group.Path!.Path = groupDir;
        var config = new ServiceDeveloperConfig { Source = "repository" };

        source.Resolve(builder, "orders", Grouped(), config, group);
        source.Resolve(
            builder, "basket",
            new ServiceDefinition
            {
                Repository = Grouped().Repository, Project = "src/Basket/Basket.csproj",
                Kind = LocalKinds.Dotnet, Origin = CatalogOrigin.FromYaml("servicesources.yaml"),
            },
            config, group);

        var key = BuildGroupKey.For(groupDir, null, () => null);
        Assert.Equal(2, PathBuildGate.For(builder).MemberCount(key));

        var services = builder.Services.BuildServiceProvider();
        await Task.WhenAll(builder.Resources.OfType<ProjectResource>().Select(
            resource => builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(resource, services))));

        Assert.Equal(2, runner.Builds);
        Assert.Equal(1, runner.MaxConcurrent);
    }

    // === prepare: only a declared path.prepare runs in the group directory ===

    private sealed class DirectoryRecordingRunner : IPrepareCommandRunner
    {
        public List<(string Directory, IReadOnlyList<string> Command)> Runs { get; } = [];

        public int Run(
            string workingDirectory, IReadOnlyList<string> command, CancellationToken cancellationToken,
            Action<string> onLine)
        {
            Runs.Add((workingDirectory, command));
            return 0;
        }
    }

    private static (DirectoryRecordingRunner Runner, string GroupDir) ResolveRedirectedWithCatalogPrepare(
        PrepareDeveloperConfig? memberPrepare)
    {
        var groupDir = ProjectTree("Orders");
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var runner = new DirectoryRecordingRunner();
        var definition = new ServiceDefinition
        {
            Repository = new RepositoryDefinition
            {
                Url = "https://example.com/monorepo.git",
                CheckoutName = "monorepo",
                Prepare = new PrepareMetadata { Command = ["npm", "ci"] },
            },
            Project = "src/Orders/Orders.csproj",
            Kind = LocalKinds.Dotnet,
            Origin = CatalogOrigin.FromYaml("servicesources.yaml"),
        };
        var group = PathGroup();
        group.Path!.Path = groupDir;
        var config = new ServiceDeveloperConfig { Source = "repository", Path = new() { Prepare = memberPrepare } };

        new PathSource(prepareRunner: runner).Resolve(builder, "orders", definition, config, group);

        return (runner, groupDir);
    }

    [Fact]
    public void RedirectedMember_DoesNotRunTheGroupsCatalogPrepare()
    {
        var (runner, _) = ResolveRedirectedWithCatalogPrepare(memberPrepare: null);

        Assert.Empty(runner.Runs);
    }

    [Fact]
    public void RedirectedMember_RunsItsOwnPathPrepareInTheGroupDirectory()
    {
        var (runner, groupDir) = ResolveRedirectedWithCatalogPrepare(new PrepareDeveloperConfig { Command = ["make", "dev"] });

        var run = Assert.Single(runner.Runs);
        Assert.Equal(["make", "dev"], run.Command);
        Assert.Equal(Path.GetFullPath(groupDir), Path.GetFullPath(run.Directory));
    }
}
