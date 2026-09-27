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
        RejectRefOnPathService(serviceName, config);

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
    /// No <c>ref</c> concept exists for a <c>"path"</c> service (design "<c>ref</c> is not offered" —
    /// finding 3): a directory that is not a separate checkout has no second commit for a ref to
    /// name. The only field a developer could still have set that would mean one is
    /// <c>local.ref</c>, most plausibly left over from an earlier <c>"repository"</c> configuration —
    /// <see cref="ServiceDeveloperConfig.Local"/> is always bound regardless of the effective source
    /// (see its own remarks), so nothing stops a developer from writing it. This is the same shape of
    /// mistake <see cref="LocalGitCheckout.PrepareRepoRoot"/> refuses for a grouped service's
    /// <c>local.ref</c>, applied here for a different reason.
    /// </summary>
    private static void RejectRefOnPathService(string serviceName, ServiceDeveloperConfig config)
    {
        if (config.Local.Ref is null)
        {
            return;
        }

        throw ServiceSourcesConfigurationException.For(
            $"Service '{new Name(serviceName)}': 'local.ref' cannot be set — this service's source is 'path', which "
            + $"names a directory that is already checked out beside the AppHost, with no separate commit for a "
            + $"ref to move it onto. Remove 'local.ref', or give this developer a 'repository' source instead if "
            + $"they need to select a ref.");
    }

    /// <summary>
    /// The directory this service resolves to: a developer's own override if they set one — unconfined,
    /// exactly like <c>local.path</c> today — or the catalog's own <c>path:</c>, confined to inside the
    /// AppHost directory.
    /// </summary>
    private static string ResolveRepoRoot(
        string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config, string appHostDirectory)
    {
        if (config.Path.Path is { } overridePath)
        {
            // Anchored to the AppHost directory, not the process's current working directory —
            // matching local.path's own behavior. Path.GetFullPath is a no-op when overridePath is
            // already absolute.
            var overridden = Path.GetFullPath(overridePath, appHostDirectory);

            if (!Directory.Exists(overridden))
            {
                throw ServiceSourcesConfigurationException.For(
                    $"Service '{new Name(serviceName)}': the 'path.path' override points at '{Raw.Escaped(overridden)}', "
                    + $"which does not exist. 'path.path' must name an existing local directory.");
            }

            return overridden;
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
        if (CheckoutRelativePath.IsAbsolute(path))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': path '{Raw.Escaped(path)}' is an absolute path. A catalog-declared "
                + $"'path' has to be relative to the AppHost directory — it names a directory the repository "
                + $"commits, not one sitting elsewhere on a developer's machine. To point at a directory outside "
                + $"the repository, use a developer override instead: 'path.path' in servicesources.local.json.");
        }

        if (CheckoutRelativePath.UnusableSegment(path) is { } unusable)
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': path '{Raw.Escaped(path)}' has a path segment '{new Name(unusable)}' — "
                + $"{CheckoutRelativePath.OnlyDotsAndSpacesRuleAndRemedy}");
        }

        if (CheckoutRelativePath.EscapesRoot(path))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': path '{Raw.Escaped(path)}' points outside the AppHost directory. "
                + $"It must stay within the repository.");
        }
    }

    /// <summary>
    /// Runs this service's <c>prepare</c> step if one is due — the catalog's own block, merged with
    /// the developer's <c>path.prepare</c> per field, rejecting <c>oncePerCommit</c> (design "prepare:
    /// once/always/never only"). Two services sharing one resolved <paramref name="repoRoot"/>
    /// serialize under <see cref="CheckoutNameLock"/>, keyed on the normalized absolute path, the same
    /// discipline a managed checkout gets from <see cref="LocalProjectSource"/>.
    /// </summary>
    private void RunPrepareIfDue(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, string repoRoot)
    {
        var plan = PreparePlan.ForCatalogPath(
            serviceName, definition.Repository.Prepare, config.Path.Prepare, OperatingSystem.IsWindows());

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
