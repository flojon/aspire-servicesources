using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.Prepare;

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
/// A catalog-declared <c>path:</c> is confined to inside the AppHost directory, the same rule
/// <c>project:</c>/<c>java.jarPath</c> already follow; a developer's own <c>path.path</c> override is
/// unconfined, exactly like <c>local.path</c> today — it is the developer's own machine and directory
/// (design "Confinement differs by who wrote the value").
/// </para>
/// </remarks>
internal sealed class PathSource(IGitClient? gitClient = null, IPrepareCommandRunner? prepareRunner = null)
    : IServiceSource
{
    private readonly IGitClient _gitClient = gitClient ?? new GitCliClient();
    private readonly IPrepareCommandRunner _prepareRunner = prepareRunner ?? ProcessPrepareCommandRunner.Instance;

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

        var repoRoot = ResolveRepoRoot(serviceName, definition, config, builder.AppHostDirectory);

        RunPrepareIfDue(builder, serviceName, definition, config, repoRoot);

        if (isDotnetKind)
        {
            var projectPath = LocalProjectSource.ResolveProjectFile(serviceName, repoRoot, definition.Project, Source);

            return ResolvedService.Bridge(builder.AddProject(serviceName, projectPath), serviceName, Source);
        }

        LocalProjectSource.ValidateWithKindHandler(serviceName, definition, repoRoot, handler!);

        return LocalProjectSource.InvokeKindHandler(builder, serviceName, definition, repoRoot, handler!, Source);
    }

    /// <summary>
    /// The directory this service resolves to: a developer's own override if they set one — unconfined,
    /// exactly like <c>local.path</c> today — or the catalog's own <c>path:</c>, confined to inside the
    /// AppHost directory.
    /// </summary>
    /// <remarks>
    /// There is no <c>ref</c> for a <c>"path"</c> service (design finding 3), and a leftover
    /// <c>local.ref</c> is not read at all: <c>local</c> is the <c>"repository"</c> source's block, and a
    /// block for a source that is not selected survives but nothing reads it — a lower layer's
    /// <c>local.ref</c> must not break a higher layer that switches this service to <c>"path"</c>.
    /// </remarks>
    private static string ResolveRepoRoot(
        string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config, string appHostDirectory)
    {
        if (config.Path.Path is { } overridePath)
        {
            return LocalGitCheckout.ResolveDeveloperDirectory(serviceName, "path.path", overridePath, appHostDirectory);
        }

        RequireCatalogPath(serviceName, definition);

        ValidateCatalogPath(serviceName, definition.Path!);

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
    /// <c>path.path</c> override is exempt, the same way a <c>local.path</c> override exempts a
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
            + $"'{serviceKey}:path:path' to a directory you already have on disk. The key is '{serviceKey}:source', "
            + $"which any configuration layer can set: {Raw.Literal(DeveloperConfiguration.FileName)}, appsettings, "
            + $"user secrets, the environment variable {Raw.Escaped(DeveloperConfiguration.EnvironmentVariableFor(serviceName))}, "
            + $"or the command line.");
    }

    /// <summary>
    /// The confinement a catalog-declared <c>path:</c> gets — the same lexical rule
    /// <see cref="LocalProjectSource.ValidateProject"/> applies to <c>project:</c>: no absolute path,
    /// no climbing out with <c>..</c>. A developer's own <c>path.path</c> override never reaches this
    /// — see <see cref="ResolveRepoRoot"/>.
    /// </summary>
    private static void ValidateCatalogPath(string serviceName, string path)
    {
        switch (CheckoutRelativePath.FirstBreach(path))
        {
            case { Kind: ConfinementBreachKind.Absolute }:
                throw ServiceSourcesConfigurationException.For(
                    $"Service '{new Name(serviceName)}': path '{Raw.Escaped(path)}' is an absolute path. A catalog-declared "
                    + $"'path' has to be relative to the AppHost directory — it names a directory the repository "
                    + $"commits, not one sitting elsewhere on a developer's machine. To point at a directory outside "
                    + $"the repository, use a developer override instead: 'path.path' in servicesources.local.json.");

            case { Kind: ConfinementBreachKind.UnusableSegment, Segment: var unusable }:
                throw ServiceSourcesConfigurationException.For(
                    $"Service '{new Name(serviceName)}': path '{Raw.Escaped(path)}' has a path segment '{new Name(unusable)}' — "
                    + $"{CheckoutRelativePath.OnlyDotsAndSpacesRuleAndRemedy}");

            case { Kind: ConfinementBreachKind.EscapesRoot }:
                throw ServiceSourcesConfigurationException.For(
                    $"Service '{new Name(serviceName)}': path '{Raw.Escaped(path)}' points outside the AppHost directory. "
                    + $"It must stay within the repository.");
        }
    }

    /// <summary>
    /// Runs this service's <c>prepare</c> step if one is due — the catalog's own block, merged with
    /// the developer's <c>path.prepare</c> per field (design "prepare: once/always/never only"). Two
    /// services sharing one resolved <paramref name="repoRoot"/> serialize under
    /// <see cref="CheckoutNameLock"/>, keyed on the normalized absolute path, the same discipline a
    /// managed checkout gets from <see cref="LocalProjectSource"/>.
    /// </summary>
    /// <remarks>
    /// Only an ungrouped service's own block is inherited. A grouped service's
    /// <c>definition.Repository.Prepare</c> is the shared repository's step, written to run once at
    /// the root of the group's shared checkout — not in one member's directory, once per member, which
    /// is all a <c>"path"</c> service could offer it. So a grouped <c>"path"</c> service inherits
    /// nothing, and runs only a <c>path.prepare</c> step its developer declares.
    /// </remarks>
    private void RunPrepareIfDue(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, string repoRoot)
    {
        var catalogPrepare = LocalGitCheckout.IsGrouped(definition, serviceName) ? null : definition.Repository.Prepare;

        var plan = PreparePlan.ForCatalogPath(
            serviceName, catalogPrepare, config.Path.Prepare, OperatingSystem.IsWindows());

        if (plan.Step is not { } step)
        {
            return;
        }

        var lockKey = PrepareMarker.NormalizeCheckoutPath(repoRoot);

        using (CheckoutNameLock.For(builder).Acquire(lockKey))
        {
            // Run mode only — same gate LocalProjectSource applies, and for the same reason: publish
            // mode composes the model and exits, and a bootstrap produces what a service needs in
            // order to run rather than anything a manifest depends on.
            if (builder.ExecutionContext.IsRunMode)
            {
                CheckoutPreparation.Run(
                    serviceName, PreparePlan.ServiceLabel(serviceName), serviceName, step, repoRoot,
                    builder.AppHostDirectory, managedCheckout: false, _gitClient, _prepareRunner,
                    BufferingPrepareOutputSink.Wrap(builder, serviceName, ConsolePrepareOutputSink.Instance));
            }
            else if (CheckoutPreparation.WouldRun(
                serviceName, step, repoRoot, builder.AppHostDirectory, managedCheckout: false, _gitClient))
            {
                ConsolePrepareOutputSink.Instance.Report(
                    CheckoutPreparation.SkippedOutsideRunModeNotice(serviceName, step));
            }
        }
    }
}
