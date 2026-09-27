using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Sources;
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
        new() { Source = "path", Local = new() { Ref = @ref }, Path = new() { Path = path, Prepare = preparePath } };

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
        // local.path today.
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
            $"Service '{ServiceName}': path 'services/orders' does not exist under '{appHostDir}'. A 'path' " +
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
    /// <c>local</c> is the <c>"repository"</c> source's block, and a block for a source that is not
    /// selected survives but nothing reads it — so a <c>local.ref</c> left in a lower configuration
    /// layer must not break a higher layer that switches the service to <c>"path"</c>.
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

    [Fact]
    public void Resolve_PrepareModeOncePerCommitExplicit_IsRejectedWithTheDraftedMessage()
    {
        var appHostDir = CreateAppHostDirectoryWithService(out _);
        var builder = TestHelpers.CreateBuilder(appHostDir);
        var prepare = new PrepareMetadata { Command = ["./prepare.sh"], Mode = "oncePerCommit" };

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new PathSource(new GitCliClient(), new CountingPrepareRunner()).Resolve(
                builder, ServiceName, Definition(path: "services/orders", prepare: prepare), DevConfig()));

        // Written in the catalog, which may be right for the 'repository' source the same entry
        // serves, so the developer's own override is offered alongside editing the catalog.
        Assert.Equal(
            $"Service '{ServiceName}': prepare.mode 'oncePerCommit' does not apply to a 'path' service — " +
            "there is no separate commit for this directory to move to on its own. Set path.prepare.mode to " +
            "'once' (re-run only when the command itself changes) or 'always' (an incremental script that " +
            "decides its own work) for this service in servicesources.local.json, or change the catalog's mode.",
            ex.Message);
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
    /// Unlike a developer's <c>local.path</c> override — whose catalog <c>prepare:</c> block is
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
    /// <c>local.prepare</c> does for a managed checkout — here overriding just the mode, with the
    /// command still coming from the catalog.
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
