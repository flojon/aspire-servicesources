using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// The <c>"path"</c> source: no clone, no ref, no deferral — a directory that is either already
/// checked out beside the AppHost (a catalog-declared <c>path:</c>, confined) or the developer's own,
/// anywhere on disk (a <c>path.path</c> override, unconfined). See design
/// "docs/superpowers/specs/2026-09-26-servicesources-workspace-source-design.md".
/// </summary>
[Trait("IO", "true")]
[Trait("Concurrency", "true")]
public class PathSourceTests
{
    private const string ServiceName = "orders";

    private sealed class CountingPrepareRunner : IPrepareCommandRunner
    {
        public int Runs { get; private set; }

        public int Run(
            string workingDirectory, IReadOnlyList<string> command, CancellationToken cancellationToken,
            Action<string> onLine)
        {
            Runs++;
            return 0;
        }
    }

    private sealed class DelegatePrepareRunner(
        Func<string, IReadOnlyList<string>, CancellationToken, Action<string>, int> run) : IPrepareCommandRunner
    {
        public int Run(
            string workingDirectory, IReadOnlyList<string> command, CancellationToken cancellationToken,
            Action<string> onLine) =>
            run(workingDirectory, command, cancellationToken, onLine);
    }

    private sealed class DelegatePrepareRunnerRecordingCommands : IPrepareCommandRunner
    {
        public List<IReadOnlyList<string>> Commands { get; } = [];

        public int Run(
            string workingDirectory, IReadOnlyList<string> command, CancellationToken cancellationToken,
            Action<string> onLine)
        {
            Commands.Add(command);
            return 0;
        }
    }

    private static ServiceDefinition Definition(
        string? path = null, string project = "Orders.csproj", PrepareMetadata? prepare = null,
        string kind = LocalKinds.Dotnet, object? kindConfig = null, string serviceName = ServiceName) =>
        new ServiceMetadata
        {
            Path = path,
            Project = project,
            Prepare = prepare,
            Kind = kind,
            KindConfig = kindConfig,
        }.ToDefinition("servicesources.yaml", serviceName, TestHelpers.EmptyRepositories);

    private static ServiceDeveloperConfig DevConfig(
        string? path = null, string? @ref = null, PrepareDeveloperConfig? preparePath = null) =>
        new() { Source = "path", Repository = new() { Ref = @ref }, Path = new() { Path = path, Prepare = preparePath } };

    private static string CreateAppHostDirectoryWithService(out string serviceDir, string relativePath = "services/orders")
    {
        var appHostDir = TempDirectories.CreateSubdirectory().FullName;
        serviceDir = Directory.CreateDirectory(
            Path.Combine(appHostDir, relativePath.Replace('/', Path.DirectorySeparatorChar))).FullName;
        File.WriteAllText(Path.Combine(serviceDir, "Orders.csproj"), "<Project />");
        return appHostDir;
    }

    // === Dotnet-kind resolution ===

    [Fact]
    public void Resolve_CatalogPathDotnetKind_ReturnsTheRealRegisteredProject()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var builder = TestHelpers.CreateBuilder(appHostDir);
        var definition = Definition(path: "services/orders");

        var service = new PathSource(new GitCliClient()).Resolve(builder, ServiceName, definition, DevConfig());

        Assert.IsType<ServiceResource>(service.Resource);
        Assert.IsAssignableFrom<ProjectResource>(Assert.Single(builder.Resources, r => r.Name == ServiceName));
    }

    [Fact]
    public void Resolve_DeveloperOverride_IsUnconfinedLikeLocalPath()
    {
        var elsewhere = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(elsewhere, "Orders.csproj"), "<Project />");
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var definition = Definition(path: null);

        var service = new PathSource(new GitCliClient()).Resolve(
            builder, ServiceName, definition, DevConfig(path: elsewhere));

        Assert.IsAssignableFrom<ProjectResource>(Assert.Single(builder.Resources, r => r.Name == ServiceName));
        Assert.IsType<ServiceResource>(service.Resource);
    }

    [Fact]
    public void Resolve_DeveloperOverrideOutsideTheAppHostDirectory_IsNotConfined()
    {
        // The whole point of the developer-vs-catalog asymmetry (design "Confinement differs by who
        // wrote the value"): a catalog path: could never be this, but path.path can, exactly like
        // repository.path today.
        var appHostDir = TempDirectories.CreateSubdirectory().FullName;
        var elsewhere = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(elsewhere, "Orders.csproj"), "<Project />");
        Assert.False(elsewhere.StartsWith(appHostDir, StringComparison.Ordinal));

        var builder = TestHelpers.CreateBuilder(appHostDir);
        var service = new PathSource(new GitCliClient()).Resolve(
            builder, ServiceName, Definition(path: null), DevConfig(path: elsewhere));

        Assert.IsType<ServiceResource>(service.Resource);
    }

    [Fact]
    public void Resolve_DeveloperOverrideDoesNotExist_Throws()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);
        var missing = Path.Combine(TempDirectories.CreateSubdirectory().FullName, "nope");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(
                builder, ServiceName, Definition(path: null), DevConfig(path: missing)));

        Assert.Contains("path.path", ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
    }

    // === Confinement of a catalog-declared path: ===

    [Fact]
    public void Resolve_CatalogPathIsAbsolute_IsRefusedRatherThanResolvedOutsideTheAppHostDirectory()
    {
        var elsewhere = TempDirectories.CreateSubdirectory().FullName;
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(
                builder, ServiceName, Definition(path: elsewhere), DevConfig()));

        Assert.Contains(ServiceName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("absolute", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_CatalogPathClimbsOutOfTheAppHostDirectory_IsRefused()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(
                builder, ServiceName, Definition(path: "../escapee"), DevConfig()));

        Assert.Contains("../escapee", ex.Message, StringComparison.Ordinal);
        Assert.Contains("outside", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_CatalogPathHasAnUnusableSegment_IsRefused()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(
                builder, ServiceName, Definition(path: "services/.../orders"), DevConfig()));

        Assert.Contains("services/.../orders", ex.Message, StringComparison.Ordinal);
    }

    // === Confinement is to the repository, not the AppHost directory ===

    /// <summary>
    /// A repository root (a directory holding <c>.git</c>) with the AppHost one level down in its own
    /// project directory — the usual Aspire layout — and a service beside it.
    /// </summary>
    private static string CreateRepositoryWithAppHostBesideTheService(out string repositoryRoot)
    {
        repositoryRoot = TempDirectories.CreateSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(repositoryRoot, ".git"));
        var appHostDir = Directory.CreateDirectory(Path.Combine(repositoryRoot, "src", "MyApp.AppHost")).FullName;
        var serviceDir = Directory.CreateDirectory(Path.Combine(repositoryRoot, "src", "Orders.Api")).FullName;
        File.WriteAllText(Path.Combine(serviceDir, "Orders.csproj"), "<Project />");
        return appHostDir;
    }

    [Fact]
    public void Resolve_CatalogPathBesideTheAppHostInsideTheRepository_Resolves()
    {
        var appHostDir = CreateRepositoryWithAppHostBesideTheService(out _);
        var builder = TestHelpers.CreateBuilder(appHostDir);

        var service = new PathSource(new GitCliClient()).Resolve(
            builder, ServiceName, Definition(path: "../Orders.Api"), DevConfig());

        Assert.IsType<ServiceResource>(service.Resource);
        Assert.IsAssignableFrom<ProjectResource>(Assert.Single(builder.Resources, r => r.Name == ServiceName));
    }

    [Fact]
    public void Resolve_CatalogPathClimbingOutOfTheRepository_IsRefusedNamingTheRepository()
    {
        var appHostDir = CreateRepositoryWithAppHostBesideTheService(out var repositoryRoot);
        var builder = TestHelpers.CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(
                builder, ServiceName, Definition(path: "../../../elsewhere"), DevConfig()));

        Assert.Contains("../../../elsewhere", ex.Message, StringComparison.Ordinal);
        Assert.Contains(
            $"points outside the repository at '{Name.Escape(repositoryRoot)}'", ex.Message, StringComparison.Ordinal);
    }

    // === A copy with no .git: ServiceSources:RepositoryRoot ===

    /// <summary>The sibling layout, with no <c>.git</c> anywhere above it — a source archive's shape.</summary>
    private static string CreateCopyWithAppHostBesideTheService(out string copyRoot)
    {
        copyRoot = TempDirectories.CreateSubdirectory().FullName;
        var appHostDir = Directory.CreateDirectory(Path.Combine(copyRoot, "src", "MyApp.AppHost")).FullName;
        var serviceDir = Directory.CreateDirectory(Path.Combine(copyRoot, "src", "Orders.Api")).FullName;
        File.WriteAllText(Path.Combine(serviceDir, "Orders.csproj"), "<Project />");
        return appHostDir;
    }

    /// <summary>
    /// As its own configuration source, the way an environment variable arrives — a value set through
    /// the indexer is lost when the file's source registers and the configuration rebuilds.
    /// </summary>
    private static void SetRepositoryRoot(IDistributedApplicationBuilder builder, string value) =>
        builder.Configuration.AddInMemoryCollection([new(DeveloperConfiguration.RepositoryRootKey, value)]);

    [Fact]
    public void Resolve_NoGitAndNoSetting_RefusesTheClimbAndSaysHowToSetTheRoot()
    {
        var appHostDir = CreateCopyWithAppHostBesideTheService(out _);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(
                TestHelpers.CreateBuilder(appHostDir), ServiceName, Definition(path: "../Orders.Api"), DevConfig()));

        Assert.Contains(
            $"points outside the AppHost directory '{Name.Escape(appHostDir)}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("No '.git' was found", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'ServiceSources:RepositoryRoot'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ServiceSources__RepositoryRoot", ex.Message, StringComparison.Ordinal);
        Assert.Contains("\"repositoryRoot\"", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_NoGitButTheRootIsSet_AllowsTheClimb(bool relative)
    {
        var appHostDir = CreateCopyWithAppHostBesideTheService(out var copyRoot);
        var builder = TestHelpers.CreateBuilder(appHostDir);
        SetRepositoryRoot(builder, relative ? "../.." : copyRoot);

        new PathSource(new GitCliClient()).Resolve(builder, ServiceName, Definition(path: "../Orders.Api"), DevConfig());

        Assert.IsAssignableFrom<ProjectResource>(Assert.Single(builder.Resources, r => r.Name == ServiceName));
    }

    [Fact]
    public void Resolve_NoGitAndTheSetRootIsClimbedOutOf_IsRefusedNamingTheSetting()
    {
        var appHostDir = CreateCopyWithAppHostBesideTheService(out var copyRoot);
        var builder = TestHelpers.CreateBuilder(appHostDir);
        SetRepositoryRoot(builder, copyRoot);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(
                builder, ServiceName, Definition(path: "../../../elsewhere"), DevConfig()));

        Assert.Contains(
            $"points outside the repository root '{Name.Escape(copyRoot)}' set by 'ServiceSources:RepositoryRoot'",
            ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("No '.git' was found", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The setting names the root of the repository the AppHost is in, so one that does not contain
    /// the AppHost is a mistake — reported, not applied as a boundary nothing could satisfy.
    /// </summary>
    [Fact]
    public void Resolve_TheSetRootDoesNotContainTheAppHost_IsRefused()
    {
        var appHostDir = CreateCopyWithAppHostBesideTheService(out _);
        var elsewhere = TempDirectories.CreateSubdirectory().FullName;
        var builder = TestHelpers.CreateBuilder(appHostDir);
        SetRepositoryRoot(builder, elsewhere);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(builder, ServiceName, Definition(path: "../Orders.Api"), DevConfig()));

        Assert.Contains("does not contain the AppHost directory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_TheSetRootDoesNotExist_IsRefused()
    {
        var appHostDir = CreateCopyWithAppHostBesideTheService(out _);
        var builder = TestHelpers.CreateBuilder(appHostDir);
        SetRepositoryRoot(builder, "/no/such/root");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(builder, ServiceName, Definition(path: "../Orders.Api"), DevConfig()));

        Assert.Contains("does not exist", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A found <c>.git</c> is the boundary, and the setting is not even read — so it can never widen
    /// a boundary a repository already draws, and a stale value left in the environment breaks nothing.
    /// </summary>
    [Fact]
    public void Resolve_GitFound_TheSettingIsNotRead()
    {
        var appHostDir = CreateRepositoryWithAppHostBesideTheService(out var repositoryRoot);
        var builder = TestHelpers.CreateBuilder(appHostDir);
        SetRepositoryRoot(builder, Path.GetDirectoryName(repositoryRoot)!);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(
                builder, ServiceName, Definition(path: "../../../elsewhere"), DevConfig()));
        Assert.Contains(
            $"points outside the repository at '{Name.Escape(repositoryRoot)}'", ex.Message, StringComparison.Ordinal);

        SetRepositoryRoot(builder, "/no/such/root");
        new PathSource(new GitCliClient()).Resolve(builder, ServiceName, Definition(path: "../Orders.Api"), DevConfig());
    }

    [Fact]
    public void ConfinementRootOf_FindsAGitFileAsWellAsAGitDirectory()
    {
        // A worktree or submodule has a .git file rather than a directory; it is still a repository root.
        var repositoryRoot = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(repositoryRoot, ".git"), "gitdir: /elsewhere/.git/worktrees/x");
        var appHostDir = Directory.CreateDirectory(Path.Combine(repositoryRoot, "src", "MyApp.AppHost")).FullName;

        Assert.Equal(
            new PathSource.ConfinementRoot(repositoryRoot, PathSource.ConfinementRootKind.Git),
            PathSource.ConfinementRootOf(appHostDir, configuredRoot: () => null));
    }

    // === Missing directory ===

    [Fact]
    public void Resolve_CatalogPathDoesNotExist_ReportsTheDraftedMessage()
    {
        var appHostDir = TempDirectories.CreateSubdirectory().FullName;
        var builder = TestHelpers.CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(
                builder, ServiceName, Definition(path: "services/orders"), DevConfig()));

        Assert.Equal(
            $"Service '{ServiceName}': path 'services/orders' does not exist under '{Name.Escape(appHostDir)}'. A 'path' " +
            "service names a directory that should already be checked out beside the AppHost — there is " +
            "nothing here to clone. If this service actually lives in a separate repository, give it a " +
            "'repository:' instead (or add one alongside 'path:' so each developer can pick).",
            ex.Message);
    }

    [Fact]
    public void Resolve_NoCatalogPathAndNoOverride_NamesTheMissingConfiguration()
    {
        var builder = TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient()).Resolve(
                builder, ServiceName, Definition(path: null), DevConfig()));

        Assert.Contains(ServiceName, ex.Message, StringComparison.Ordinal);
        Assert.Contains("no directory to use", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"{ServiceName}:path:path", ex.Message, StringComparison.Ordinal);
    }

    // === ref is not offered ===

    /// <summary>
    /// <c>repository</c> is the <c>"repository"</c> source's block, and a block for a source that is
    /// not selected survives but nothing reads it — so a <c>repository.ref</c> left in a lower
    /// configuration layer must not break a higher layer that switches the service to <c>"path"</c>.
    /// </summary>
    [Fact]
    public void Resolve_LeftoverLocalRef_IsIgnoredRatherThanRejected()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var builder = TestHelpers.CreateBuilder(appHostDir);

        var service = new PathSource(new GitCliClient()).Resolve(
            builder, ServiceName, Definition(path: "services/orders"), DevConfig(@ref: "feature/x"));

        Assert.IsType<ServiceResource>(service.Resource);
    }

    // === prepare: once/always/never only ===

    /// <summary>
    /// A catalog <c>oncePerCommit</c> may be right for the <c>"repository"</c> source the same entry
    /// serves, so under <c>"path"</c> it is read as <c>once</c> rather than failing every developer who
    /// picks this source.
    /// </summary>
    [Fact]
    public void Resolve_CatalogModeOncePerCommit_IsReadAsOnce()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var runner = new CountingPrepareRunner();
        var source = new PathSource(new GitCliClient(), runner);
        var prepare = new PrepareMetadata { Command = ["./prepare.sh"], Mode = "oncePerCommit" };
        var definition = Definition(path: "services/orders", prepare: prepare);

        source.Resolve(TestHelpers.CreateBuilder(appHostDir), ServiceName, definition, DevConfig());
        Assert.Equal(1, runner.Runs);

        source.Resolve(TestHelpers.CreateBuilder(appHostDir), ServiceName, definition, DevConfig());
        Assert.Equal(1, runner.Runs);
    }

    [Fact]
    public void Resolve_PrepareModeOncePerCommitInTheDeveloperBlock_NamesThatBlockNotTheCatalogs()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var builder = TestHelpers.CreateBuilder(appHostDir);

        // The command comes from the catalog, the mode from the developer: the message names where
        // the mode was written, not where the command was.
        var prepare = new PrepareMetadata { Command = ["./prepare.sh"] };
        var developerPrepare = new PrepareDeveloperConfig { Mode = "oncePerCommit" };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient(), new CountingPrepareRunner()).Resolve(
                builder, ServiceName, Definition(path: "services/orders", prepare: prepare),
                DevConfig(preparePath: developerPrepare)));

        Assert.Equal(
            $"Service '{ServiceName}': path.prepare.mode 'oncePerCommit' does not apply to a 'path' service — " +
            "there is no separate commit for this directory to move to on its own. Use 'once' (re-run only " +
            "when the command itself changes) or 'always' (an incremental script that decides its own work) " +
            "instead.",
            ex.Message);
    }

    /// <summary>
    /// A command with no mode written defaults to <c>once</c> for a <c>path</c> service, not to the
    /// managed checkout's <c>oncePerCommit</c> — which has no commit to key on here. The common catalog
    /// shape, a <c>prepare:</c> with no mode, must not make the <c>path</c> source unusable for a mode
    /// nobody wrote.
    /// </summary>
    [Fact]
    public void Resolve_PrepareCommandWithNoModeWritten_DefaultsToOnce()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var runner = new CountingPrepareRunner();
        var source = new PathSource(new GitCliClient(), runner);
        var prepare = new PrepareMetadata { Command = ["./prepare.sh"] };
        var definition = Definition(path: "services/orders", prepare: prepare);

        source.Resolve(TestHelpers.CreateBuilder(appHostDir), ServiceName, definition, DevConfig());
        Assert.Equal(1, runner.Runs);

        // 'once': the marker satisfies the unchanged command on the next start.
        source.Resolve(TestHelpers.CreateBuilder(appHostDir), ServiceName, definition, DevConfig());
        Assert.Equal(1, runner.Runs);
    }

    /// <summary>
    /// A developer's own <c>path.path</c> points at their working tree, anywhere on disk: exactly as for
    /// <c>repository.path</c>, the catalog's step is not run there, and a notice names the command so
    /// they can opt in.
    /// </summary>
    [Fact]
    public void Resolve_DeveloperOverrideWithACatalogPrepare_DoesNotRunIt()
    {
        var elsewhere = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(elsewhere, "Orders.csproj"), "<Project />");
        var runner = new CountingPrepareRunner();
        var prepare = new PrepareMetadata { Command = ["./bootstrap.sh"] };

        new PathSource(new GitCliClient(), runner).Resolve(
            TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName), ServiceName,
            Definition(path: "services/orders", prepare: prepare), DevConfig(path: elsewhere));

        Assert.Equal(0, runner.Runs);
    }

    [Fact]
    public void ForPathOverride_ACatalogPrepare_IsNotInheritedAndTheNoticeNamesPathPath()
    {
        var plan = PreparePlan.ForPathOverride(
            ServiceName, new PrepareMetadata { Command = ["./bootstrap.sh"] }, developer: null, windows: false);

        Assert.Null(plan.Step);
        var notice = plan.IgnoredCatalogNotice!.Value.ToString();
        Assert.Contains("'path.path'", notice, StringComparison.Ordinal);
        Assert.Contains("\"./bootstrap.sh\"", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("repository.path", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DeveloperOverrideWithItsOwnPrepare_RunsOnlyThat()
    {
        var elsewhere = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(elsewhere, "Orders.csproj"), "<Project />");
        var runner = new DelegatePrepareRunnerRecordingCommands();
        var catalogPrepare = new PrepareMetadata { Command = ["./bootstrap.sh"] };
        var developerPrepare = new PrepareDeveloperConfig { Command = ["make", "dev"] };

        new PathSource(new GitCliClient(), runner).Resolve(
            TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName), ServiceName,
            Definition(path: "services/orders", prepare: catalogPrepare),
            DevConfig(path: elsewhere, preparePath: developerPrepare));

        Assert.Equal(["make", "dev"], Assert.Single(runner.Commands));
    }

    /// <summary>
    /// A grouped service's catalog prepare is the shared repository's step, written to run once at the
    /// root of the group's checkout — not in one member's directory, once per member. So a grouped
    /// <c>path</c> service inherits it not at all.
    /// </summary>
    [Fact]
    public void Resolve_GroupedServiceWithARepositoryPrepare_DoesNotRunItInTheServicesDirectory()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var runner = new CountingPrepareRunner();

        var repositories = new Dictionary<string, RepositoryDefinition>(StringComparer.Ordinal)
        {
            ["mono"] = new RepositoryDefinition
            {
                Url = "https://github.com/example/mono",
                // No mode written — which, for the repository source, means oncePerCommit; inherited
                // by a path service it would both run in the wrong directory and be refused.
                Prepare = new PrepareMetadata { Command = ["npm", "ci"] },
                CheckoutName = "mono",
            },
        };

        var definition = new ServiceMetadata
        {
            RepositoryRef = "mono",
            Path = "services/orders",
            Project = "Orders.csproj",
        }.ToDefinition("servicesources.yaml", ServiceName, repositories);

        var service = new PathSource(new GitCliClient(), runner).Resolve(
            TestHelpers.CreateBuilder(appHostDir), ServiceName, definition, DevConfig());

        Assert.IsType<ServiceResource>(service.Resource);
        Assert.Equal(0, runner.Runs);
    }

    [Fact]
    public void Resolve_NoPrepareBlockAtAll_NeverThrowsAboutPrepareModes()
    {
        // The overwhelming common case: a 'path' service that declares no 'prepare:' at all must not
        // be told to pick a mode for a step it never asked for.
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var builder = TestHelpers.CreateBuilder(appHostDir);

        var service = new PathSource(new GitCliClient(), new CountingPrepareRunner()).Resolve(
            builder, ServiceName, Definition(path: "services/orders"), DevConfig());

        Assert.IsType<ServiceResource>(service.Resource);
    }

    [Fact]
    public void Resolve_PrepareModeNever_NeverRunsEvenThoughACommandIsDeclared()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var builder = TestHelpers.CreateBuilder(appHostDir);
        var runner = new CountingPrepareRunner();
        var prepare = new PrepareMetadata { Command = ["./prepare.sh"], Mode = "never" };

        new PathSource(new GitCliClient(), runner).Resolve(
            builder, ServiceName, Definition(path: "services/orders", prepare: prepare), DevConfig());

        Assert.Equal(0, runner.Runs);
    }

    /// <summary>
    /// Unlike a developer's <c>repository.path</c> override — whose catalog <c>prepare:</c> block is
    /// ignored, not run (design finding 4) — a catalog-declared <c>path:</c>'s own step runs
    /// normally, since there is no "someone else's directory" here to protect.
    /// </summary>
    [Fact]
    public void Resolve_PrepareModeOnce_RunsAndRecordsAMarkerThatSurvivesToTheNextResolve()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var runner = new CountingPrepareRunner();
        var source = new PathSource(new GitCliClient(), runner);
        var prepare = new PrepareMetadata { Command = ["./prepare.sh"], Mode = "once" };
        var definition = Definition(path: "services/orders", prepare: prepare);

        // Two separate builders sharing one AppHost directory, the same shape two "aspire run"
        // processes over the same directory take — the marker lives on disk, not in the builder.
        source.Resolve(TestHelpers.CreateBuilder(appHostDir), ServiceName, definition, DevConfig());
        Assert.Equal(1, runner.Runs);

        source.Resolve(TestHelpers.CreateBuilder(appHostDir), ServiceName, definition, DevConfig());
        Assert.Equal(1, runner.Runs);
    }

    [Fact]
    public void Resolve_PrepareModeAlways_RunsOnEveryResolve()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var runner = new CountingPrepareRunner();
        var source = new PathSource(new GitCliClient(), runner);
        var prepare = new PrepareMetadata { Command = ["./prepare.sh"], Mode = "always" };
        var definition = Definition(path: "services/orders", prepare: prepare);

        source.Resolve(TestHelpers.CreateBuilder(appHostDir), ServiceName, definition, DevConfig());
        Assert.Equal(1, runner.Runs);

        source.Resolve(TestHelpers.CreateBuilder(appHostDir), ServiceName, definition, DevConfig());
        Assert.Equal(2, runner.Runs);
    }

    /// <summary>
    /// The developer's own <c>path.prepare</c> merges over the catalog's block per field, exactly as
    /// <c>repository.prepare</c> does for a managed checkout — here overriding just the mode, with
    /// the command still coming from the catalog.
    /// </summary>
    [Fact]
    public void Resolve_DeveloperPreparePathOverridesJustTheMode_MergesOverTheCatalogCommand()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var runner = new CountingPrepareRunner();
        var source = new PathSource(new GitCliClient(), runner);
        var prepare = new PrepareMetadata { Command = ["./prepare.sh"] };
        var definition = Definition(path: "services/orders", prepare: prepare);
        var developerPrepare = new PrepareDeveloperConfig { Mode = "always" };

        source.Resolve(
            TestHelpers.CreateBuilder(appHostDir), ServiceName, definition, DevConfig(preparePath: developerPrepare));

        Assert.Equal(1, runner.Runs);
    }

    [Fact]
    public async Task Resolve_PrepareRunning_HoldsTheCheckoutNameLockForTheResolvedPath()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out var serviceDir);
        var builder = TestHelpers.CreateBuilder(appHostDir);

        using var running = new SemaphoreSlim(0, 1);
        using var release = new SemaphoreSlim(0, 1);

        var runner = new DelegatePrepareRunner((_, _, _, _) =>
        {
            running.Release();
            release.Wait(TimeSpan.FromSeconds(10));
            return 0;
        });

        var prepare = new PrepareMetadata { Command = ["./prepare.sh"], Mode = "once" };
        var definition = Definition(path: "services/orders", prepare: prepare);
        var source = new PathSource(new GitCliClient(), runner);

        var resolveTask = Task.Run(() => source.Resolve(builder, ServiceName, definition, DevConfig()));

        Assert.True(running.Wait(TimeSpan.FromSeconds(10)), "the prepare step never started");

        // While the step is running, a second acquisition of the lock keyed on this same resolved
        // path must block — the discipline that keeps two services sharing one resolved path from
        // preparing it at once (design "prepare: once/always/never only").
        var lockKey = PrepareMarker.NormalizeCheckoutPath(serviceDir);
        var checkoutLock = CheckoutNameLock.For(builder);
        var secondAcquire = Task.Run(() => checkoutLock.Acquire(lockKey));

        await Task.WhenAny(secondAcquire, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.False(
            secondAcquire.IsCompleted,
            "a second acquire of the same resolved path's lock succeeded while its prepare step was still running");

        release.Release();
        await resolveTask;

        var secondHolder = await secondAcquire.WaitAsync(TimeSpan.FromSeconds(10));
        secondHolder.Dispose();
    }

    // === No UseDeferredCheckout() interaction ===

    [Fact]
    public void Resolve_UseDeferredCheckoutCalled_StillResolvesEagerly()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var builder = TestHelpers.CreateBuilder(appHostDir);
        builder.UseDeferredCheckout();

        var service = new PathSource(new GitCliClient()).Resolve(
            builder, ServiceName, Definition(path: "services/orders"), DevConfig());

        // A deferred registration would not yet be the real ProjectResource carrying its final
        // ProjectPath — deferral has nothing to buy a 'path' service (design finding 5), so this must
        // be the same eager resolution UseDeferredCheckout()'s absence would produce.
        Assert.IsAssignableFrom<ProjectResource>(Assert.Single(builder.Resources, r => r.Name == ServiceName));
        Assert.IsType<ServiceResource>(service.Resource);
    }
}
