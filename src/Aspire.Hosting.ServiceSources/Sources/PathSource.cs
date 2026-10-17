using System.Reflection;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.Prepare;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Resolves the <c>"path"</c> source: a directory that is already checked out beside the AppHost, or
/// — for a developer's own override — anywhere else on their machine. See design
/// "docs/superpowers/specs/2026-09-26-servicesources-workspace-source-design.md", "New source:
/// <c>path</c>".
/// </summary>
/// <remarks>
/// <para>
/// No clone, no ref, no reconciliation, and no deferral (design findings 3 and 5): there is nothing
/// here <see cref="LocalGitCheckout"/> or <see cref="DeferredCheckout"/> need do, so neither is ever
/// called. What this shares with <see cref="LocalProjectSource"/> is exactly what design finding 1
/// says is checkout-shape-agnostic: the built-in <c>dotnet</c> kind's project resolution
/// (<see cref="LocalProjectSource.ResolveProjectFile"/>/<see cref="LocalProjectSource.ConfineProject"/>)
/// and the non-dotnet kind dispatch (<see cref="ILocalResourceKind"/>/<see cref="LocalKindRegistry"/>),
/// reused unchanged through <see cref="LocalProjectSource"/>'s own internal helpers.
/// </para>
/// <para>
/// A catalog-declared <c>path:</c> is relative to the AppHost directory and confined to the repository
/// that holds it — the nearest ancestor with a <c>.git</c> entry, else a configured
/// <c>ServiceSources:RepositoryRoot</c>, else the AppHost directory itself — so the usual layout, an AppHost project beside its services
/// (<c>../Orders.Api</c>), can be declared. A developer's own <c>path.path</c> override is unconfined,
/// exactly like <c>repository.path</c> today — it is the developer's own machine and directory (design
/// "Confinement differs by who wrote the value").
/// </para>
/// </remarks>
internal sealed class PathSource(
    IGitClient? gitClient = null, IPrepareCommandRunner? prepareRunner = null, IBuildRunner? buildRunner = null)
    : IServiceSource
{
    private readonly IGitClient _gitClient = gitClient ?? new GitCliClient();
    private readonly IPrepareCommandRunner _prepareRunner = prepareRunner ?? ProcessPrepareCommandRunner.Instance;
    private readonly IBuildRunner _buildRunner = buildRunner ?? new ProcessBuildRunner();

    private const string Source = "path";

    public IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null)
    {
        var isDotnetKind = string.Equals(definition.Kind, LocalKinds.Dotnet, StringComparison.Ordinal);

        // Settled ahead of any filesystem work, exactly as LocalProjectSource settles it ahead of a
        // clone: a typo'd kind, or a missing 'project', is a mistake in the catalog whichever
        // directory it would have been judged against.
        var handler = isDotnetKind ? null : LocalProjectSource.ResolveKindHandler(builder, serviceName, definition);

        if (isDotnetKind)
        {
            LocalProjectSource.ValidateProject(serviceName, definition.Project, Source);
        }

        var groupRedirect = GroupPathSource.Redirects(serviceName, definition, config, repositoryConfig);

        if (groupRedirect && config.Repository.Prepare?.IsDeclared == true)
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': 'repository.prepare' is not read while the repository '{new Name(definition.Repository.CheckoutName)}' is on the 'path' source. Move it to 'path.prepare'.");
        }

        var repoRoot = groupRedirect
            ? GroupPathSource.ResolveDirectory(definition, repositoryConfig!, builder.AppHostDirectory)
            : ResolveRepoRoot(builder, serviceName, definition, config, builder.AppHostDirectory);

        RunPrepareIfDue(builder, serviceName, definition, config, repoRoot, developerDirectory: groupRedirect || config.Path.Path is not null);

        if (isDotnetKind)
        {
            var projectPath = LocalProjectSource.ResolveProjectFile(serviceName, repoRoot, definition.Project, Source);

            var project = LocalProjectSource.AddDotnetProject(builder, serviceName, projectPath, definition.Dotnet);

            if (builder.ExecutionContext.IsRunMode)
            {
                SerializeBuild(builder, project, serviceName, repoRoot, projectPath, definition);
            }

            return ResolvedService.Bridge(project, serviceName, Source);
        }

        LocalProjectSource.ValidateWithKindHandler(serviceName, definition, repoRoot, handler!);

        return LocalProjectSource.InvokeKindHandler(builder, serviceName, definition, repoRoot, handler!, Source);
    }

    /// <summary>
    /// Registers this project in its build group and, once Aspire is about to start it, builds it under the
    /// group's gate so services sharing a <c>ProjectReference</c> do not race on <c>bin/</c> and <c>obj/</c>.
    /// Membership is only complete after every service is added, so the handler asks at start time.
    /// </summary>
    private void SerializeBuild(
        IDistributedApplicationBuilder builder, IResourceBuilder<ProjectResource> project, string serviceName,
        string serviceDirectory, string projectPath, ServiceDefinition definition)
    {
        var key = BuildGroupKey.For(serviceDirectory, definition.BuildGroup, () => ConfiguredRepositoryRoot(builder));
        var gate = PathBuildGate.For(builder);
        var runner = _buildRunner;

        gate.Register(serviceName, key);

        project.OnBeforeResourceStarted((resource, @event, cancellationToken) =>
        {
            var logger = @event.Services.GetRequiredService<ResourceLoggerService>().GetLogger(resource);

            return gate.RunGatedBuildAsync(
                runner, serviceName, key, Path.GetFullPath(projectPath), ConfigurationOf(Assembly.GetEntryAssembly()), logger, cancellationToken);
        });
    }

    /// <summary>
    /// The configuration the AppHost was built in — what Aspire passes to <c>dotnet run</c> — so the gated
    /// build lands in the same <c>bin/</c> folder that run will use.
    /// </summary>
    internal static string? ConfigurationOf(Assembly? assembly) =>
        assembly?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration is { Length: > 0 } configuration
            ? configuration
            : null;

    /// <remarks>
    /// Always answers: a developer's own <c>path.path</c> makes <c>"path"</c> selectable even for an
    /// entry with no catalog <c>path:</c>.
    /// </remarks>
    public Type? DeclaredResourceType(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition) =>
        LocalProjectSource.KindResourceType(builder, serviceName, definition);

    /// <summary>
    /// The directory this service resolves to: a developer's own override if they set one — unconfined,
    /// exactly like <c>repository.path</c> today — or the catalog's own <c>path:</c>, relative to the AppHost
    /// directory and confined to the repository around it.
    /// </summary>
    /// <remarks>
    /// There is no <c>ref</c> for a <c>"path"</c> service (design finding 3), and a leftover
    /// <c>repository.ref</c> is not read at all: <c>repository</c> (or its deprecated alias,
    /// <c>local</c>) is the <c>"repository"</c> source's block, and a block for a source that is not
    /// selected survives but nothing reads it — a lower layer's <c>repository.ref</c> must not break
    /// a higher layer that switches this service to <c>"path"</c>.
    /// </remarks>
    private static string ResolveRepoRoot(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, string appHostDirectory)
    {
        if (config.Path.Path is { } overridePath)
        {
            return LocalGitCheckout.ResolveDeveloperDirectory(PreparePlan.ServiceLabel(serviceName), "path.path", overridePath, appHostDirectory);
        }

        RequireCatalogPath(serviceName, definition);

        ValidateCatalogPath(builder, serviceName, definition.Path!, appHostDirectory);

        var confined = Path.Combine(appHostDirectory, CheckoutRelativePath.NormalizeSeparators(definition.Path!));

        if (!Directory.Exists(confined))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': path '{Raw.Escaped(definition.Path!)}' does not exist under "
                + $"'{Raw.Escaped(appHostDirectory)}'. A 'path' service names a directory that should already be "
                + $"checked out beside the AppHost — there is nothing here to clone. If this service actually "
                + $"lives in a separate repository, give it a 'repository:' instead (or add one alongside "
                + $"'path:' so each developer can pick).");
        }

        return confined;
    }

    /// <summary>
    /// Refuses a <c>"path"</c> service whose catalog entry declares no directory to use — the
    /// counterpart of <see cref="LocalProjectSource.RequireRepositoryToCheckOut"/> for this source. A
    /// <c>path.path</c> override is exempt, the same way a <c>repository.path</c> override exempts a
    /// <c>"repository"</c> service from needing a catalog <c>repository:</c> — this method only runs
    /// once the caller has already established there is no override.
    /// </summary>
    private static void RequireCatalogPath(string serviceName, ServiceDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(definition.Path))
        {
            return;
        }

        var serviceKey = Raw.Compose($"{Raw.Literal(DeveloperConfiguration.ServicesKey)}:{new Name(serviceName)}");

        throw ServiceSourcesConfigurationException.For(
            $"Service '{new Name(serviceName)}' source is 'path' but {Raw.Origin(definition.Origin)} gives it no "
            + $"directory to use. Either give its catalog entry a 'path' (or WithPath(...) in code), or set "
            + $"'{serviceKey}:path:path' to a directory you already have on disk. "
            + $"{LocalProjectSource.WhereTheSourceCanBeSet(serviceName)}");
    }

    /// <summary>
    /// The confinement a catalog-declared <c>path:</c> gets: never absolute, no segment made only of
    /// dots and spaces, and — read from the AppHost directory — never climbing out of the repository
    /// that holds it (<see cref="ConfinementRootOf"/>). A developer's own <c>path.path</c> override
    /// never reaches this — see <see cref="ResolveRepoRoot"/>.
    /// </summary>
    /// <remarks>
    /// Still lexical, like every other confinement here: the value is judged by joining it to the
    /// AppHost directory's own position inside the repository, so <c>../Orders.Api</c> from
    /// <c>src/MyApp.AppHost</c> is <c>src/Orders.Api</c> — inside — while a climb past the repository
    /// root is refused. The repository is only located, never resolved against.
    /// </remarks>
    private static void ValidateCatalogPath(
        IDistributedApplicationBuilder builder, string serviceName, string path, string appHostDirectory)
    {
        var appHost = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appHostDirectory));
        var root = ConfinementRootOf(appHost, () => ConfiguredRepositoryRoot(builder));
        var appHostWithinRoot = Path.GetRelativePath(root.Directory, appHost);

        // Absolute and unusable-segment are facts about the value as written; only climbing out needs
        // the AppHost's own depth inside the repository to judge.
        var breach = CheckoutRelativePath.FirstBreach(path) is { Kind: not ConfinementBreachKind.EscapesRoot } asWritten
            ? asWritten
            : CheckoutRelativePath.FirstBreach(Path.Join(appHostWithinRoot, path));

        var (boundary, escapeRemedy) = root.Kind switch
        {
            ConfinementRootKind.Git => (Raw.Compose($"the repository at '{Raw.Escaped(root.Directory)}'"), (Raw?)null),
            ConfinementRootKind.Configured => (
                Raw.Compose($"the repository root '{Raw.Escaped(root.Directory)}' set by '{Raw.Literal(DeveloperConfiguration.RepositoryRootKey)}'"),
                null),
            _ => (Raw.Compose($"the AppHost directory '{Raw.Escaped(root.Directory)}'"), NoRepositoryFoundRemedy()),
        };

        LocalProjectSource.ThrowIfBreached(
            serviceName, "path", path, breach,
            outside: boundary,
            inside: boundary,
            absoluteReason: Raw.Literal("A catalog-declared 'path' has to be relative to the AppHost directory — it names "
                + "a directory the repository commits, not one sitting elsewhere on a developer's machine. To point at a "
                + "directory outside the repository, use a developer override instead: 'path.path' in "
                + "servicesources.local.json."),
            escapeRemedy: escapeRemedy);
    }

    /// <summary>
    /// Why the boundary is the AppHost directory, said where it bites: nothing was found to say where
    /// the repository begins, so a <c>../</c> the repository would allow is refused.
    /// </summary>
    private static Raw NoRepositoryFoundRemedy() =>
        Raw.Compose($"No '.git' was found in the AppHost directory or above it, so there is no repository to "
            + $"confine to and the AppHost directory is the boundary. If this is a copy of the repository without "
            + $"its '.git' — a source archive, or a container build context that leaves '.git' out — set "
            + $"'{Raw.Literal(DeveloperConfiguration.RepositoryRootKey)}' to the repository's root directory: the "
            + $"environment variable {Raw.Literal(RepositoryRootEnvironmentVariable)}, \"{Raw.Literal(DeveloperConfigFileSource.FileRepositoryRootKey)}\" "
            + $"in {Raw.Literal(DeveloperConfiguration.FileName)}, appsettings, user secrets or the command line.");

    private const string RepositoryRootEnvironmentVariable = "ServiceSources__RepositoryRoot";

    /// <summary>What a catalog <c>path:</c> is confined to, and how that was decided.</summary>
    internal enum ConfinementRootKind
    {
        /// <summary>The nearest ancestor of the AppHost, itself included, holding a <c>.git</c> entry.</summary>
        Git,

        /// <summary><see cref="DeveloperConfiguration.RepositoryRootKey"/>, where no <c>.git</c> was found.</summary>
        Configured,

        /// <summary>Neither: the AppHost directory itself.</summary>
        AppHost,
    }

    internal readonly record struct ConfinementRoot(string Directory, ConfinementRootKind Kind);

    /// <summary>
    /// The repository <paramref name="appHostDirectory"/> sits in: the nearest ancestor (itself
    /// included) holding a <c>.git</c> entry — a directory, or the file a worktree or submodule has;
    /// failing that, <paramref name="configuredRoot"/>; failing that, the AppHost directory itself,
    /// which keeps an AppHost outside any repository confined to its own directory.
    /// </summary>
    /// <remarks>
    /// <c>.git</c> comes first, so the setting can only draw a boundary where git draws none — it can
    /// never widen one a repository already has. It is not even read while a <c>.git</c> is found.
    /// </remarks>
    /// <param name="configuredRoot">
    /// Reads <see cref="DeveloperConfiguration.RepositoryRootKey"/>, resolved to an ancestor of the
    /// AppHost directory by <see cref="ConfiguredRepositoryRoot"/>, or <see langword="null"/> when unset.
    /// </param>
    internal static ConfinementRoot ConfinementRootOf(string appHostDirectory, Func<string?> configuredRoot)
    {
        var start = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appHostDirectory));

        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            var gitEntry = Path.Combine(directory.FullName, ".git");

            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
            {
                return new(Path.TrimEndingDirectorySeparator(directory.FullName), ConfinementRootKind.Git);
            }
        }

        return configuredRoot() is { } configured
            ? new(configured, ConfinementRootKind.Configured)
            : new(start, ConfinementRootKind.AppHost);
    }

    /// <summary>Whether a <see cref="Path.GetRelativePath"/> result climbs out of, or sits on another root than, its base.</summary>
    internal static bool IsOutside(string relativePath)
        => Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// <see cref="DeveloperConfiguration.RepositoryRootKey"/> as a full path — relative to the AppHost
    /// directory, like every other path a developer writes — or <see langword="null"/> when unset.
    /// </summary>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// It names a directory that does not exist, or one that does not contain the AppHost directory:
    /// it is the root of the repository the AppHost is in, so anything else is a mistake to report
    /// rather than a boundary to apply.
    /// </exception>
    private static string? ConfiguredRepositoryRoot(IDistributedApplicationBuilder builder)
    {
        DeveloperConfigFileSource.EnsureRegistered(builder);

        var value = builder.Configuration[DeveloperConfiguration.RepositoryRootKey];

        return string.IsNullOrWhiteSpace(value) ? null : ResolveConfiguredRepositoryRoot(value, builder.AppHostDirectory);
    }

    internal static string ResolveConfiguredRepositoryRoot(string value, string appHostDirectory)
    {
        var appHost = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appHostDirectory));
        var resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value, appHost));
        var key = Raw.Literal(DeveloperConfiguration.RepositoryRootKey);

        if (!Directory.Exists(resolved))
        {
            throw ServiceSourcesConfigurationException.For(
                $"'{key}' is '{Raw.Escaped(value)}', which resolves to '{Raw.Escaped(resolved)}' — that directory does "
                + $"not exist. It names the root of the repository the AppHost is in.");
        }

        var appHostWithinRoot = Path.GetRelativePath(resolved, appHost);

        if (IsOutside(appHostWithinRoot))
        {
            throw ServiceSourcesConfigurationException.For(
                $"'{key}' is '{Raw.Escaped(value)}', which resolves to '{Raw.Escaped(resolved)}' — that does not contain "
                + $"the AppHost directory '{Raw.Escaped(appHost)}'. It names the root of the repository the AppHost is "
                + $"in, so it has to be the AppHost directory or one of the directories above it.");
        }

        return resolved;
    }

    /// <summary>
    /// Runs this service's <c>prepare</c> step if one is due — the catalog's own block, merged with
    /// the developer's <c>path.prepare</c> per field (design "prepare: once/always/never only"). Two
    /// services sharing one resolved <paramref name="repoRoot"/> serialize under
    /// <see cref="CheckoutNameLock"/>, keyed on the normalized absolute path, the same discipline a
    /// managed checkout gets from <see cref="LocalProjectSource"/>.
    /// </summary>
    /// <remarks>
    /// Only a catalog-declared <c>path:</c> inherits the catalog's block; a <c>path.path</c> override
    /// does not (see the body). And only an ungrouped service's own block is inherited. A grouped service's
    /// <c>definition.Repository.Prepare</c> is the shared repository's step, written to run once at
    /// the root of the group's shared checkout — not in one member's directory, once per member, which
    /// is all a <c>"path"</c> service could offer it. So a grouped <c>"path"</c> service inherits
    /// nothing, and runs only a <c>path.prepare</c> step its developer declares.
    /// </remarks>
    private void RunPrepareIfDue(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, string repoRoot, bool developerDirectory)
    {
        var catalogPrepare = LocalGitCheckout.IsGrouped(definition, serviceName) ? null : definition.Repository.Prepare;

        // A developer-named directory (path.path, or a group's) is a working tree nothing establishes is a
        // checkout of the repository the catalog names — so, as for repository.path, the catalog's step
        // is not run there; only one the developer declares is.
        var plan = developerDirectory
            ? PreparePlan.ForPathOverride(serviceName, catalogPrepare, config.Path.Prepare, OperatingSystem.IsWindows())
            : PreparePlan.ForCatalogPath(serviceName, catalogPrepare, config.Path.Prepare, OperatingSystem.IsWindows());

        if (plan.IgnoredCatalogNotice is { } ignored)
        {
            ServiceSourcesWarnings.For(builder).AddNotice(ignored);
        }

        if (plan.Step is not { } step)
        {
            return;
        }

        using (CheckoutNameLock.For(builder).Acquire(PrepareMarker.NormalizeCheckoutPath(repoRoot)))
        {
            CheckoutPreparation.RunOrReportSkip(
                builder, serviceName, PreparePlan.ServiceLabel(serviceName), serviceName, step, repoRoot,
                managedCheckout: false, _gitClient, _prepareRunner);
        }
    }
}
