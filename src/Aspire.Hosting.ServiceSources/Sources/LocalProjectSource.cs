using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <param name="prepareRunner">
/// How a <c>prepare</c> command is launched. Defaulted to the process-backed runner, which is what
/// production wants and what keeps every existing construction site of this type unchanged; a test
/// substitutes it, so nothing here spawns a process — the same shape
/// <paramref name="gitClient"/> gives cloning.
/// </param>
internal sealed class LocalProjectSource(IGitClient gitClient, IPrepareCommandRunner? prepareRunner = null)
    : IServiceSource
{
    private readonly IPrepareCommandRunner _prepareRunner = prepareRunner ?? ProcessPrepareCommandRunner.Instance;

    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        // Before any network work: a machine without a usable git can't clone anything, and
        // finding that out once here beats finding it out as an identical clone failure on every
        // service the catalog holds. Cheap after the first call, which is why it sits on the hot
        // path rather than behind a one-shot flag of its own.
        gitClient.EnsureAvailable();

        var isDotnetKind = string.Equals(metadata.Kind, LocalKinds.Dotnet, StringComparison.Ordinal);

        // Settled before paying for a checkout: looking the kind up is a dictionary probe against
        // registry state, needs no working tree, and running it after the clone would make a typo'd
        // kind — or a kind nobody registered — cost a cold clone of this repository before saying
        // so. Only the first "local" AddService gets even that ahead of every clone: the prefetch
        // below starts the speculative ones at once, so once any service has been resolved they are
        // already in flight and this check no longer runs in front of them.
        //
        // The handler's own Validate used to sit here and cannot any more: it is handed the
        // resolved checkout to judge the service's paths against, so it runs below, after
        // GetRepoRoot and immediately before Resolve. That costs every service and not only the
        // first — an options block used to be rejected without waiting on any clone at all, and now
        // each service blocks on its own checkout before its block is so much as parsed. A service
        // that is both misconfigured and pointed at a repository that cannot be reached reports the
        // clone failure rather than the configuration error, and on a cold clone the typo is
        // reported only once the clone has finished. Paid deliberately: a kind's paths are relative
        // to the checkout, so without one in hand the check cannot be made at all.
        var handler = isDotnetKind ? null : ResolveKindHandler(builder, serviceName, metadata);

        // Whether this package owns the checkout directory, which decides whether the service
        // inherits the catalog's prepare block at all and where its completion marker goes.
        var managedCheckout = LocalGitCheckout.IsManagedCheckout(config);

        // The other configuration check that needs no working tree, and the last one standing in
        // front of the clone now that a kind's own Validate has moved below it. Being core's own it
        // also runs ahead of ShouldDefer, so it covers both paths — a typo'd mode, or a command
        // climbing out of the checkout, is reported at composition for a deferred service too,
        // before anything is registered against a directory that does not exist yet. Only what needs
        // the working tree waits for one, which is the division ValidateCheckout draws for a kind.
        var prepare = PreparePlan.For(
            serviceName, metadata.Prepare, config.Local.Prepare, managedCheckout, OperatingSystem.IsWindows());

        if (prepare.IgnoredCatalogNotice is { } ignored)
        {
            // A catalog block on a service resolved through 'local.path' is ignored rather than
            // rejected: it is the team's, and applies correctly to every developer on a managed
            // checkout, so one developer's override must not turn a shared catalog field into a
            // failure. Buffered because there is no logger yet.
            ServiceSourcesWarnings.For(builder).AddNotice(ignored);
        }

        // The dotnet kind's equivalent of the check above, and here for the same reason: confining
        // 'project' to the checkout is lexical, so it needs no working tree and belongs in front of
        // the clone rather than after it. Both paths below combine the value with a repo root — the
        // eager one only once GetRepoRoot has materialized the checkout — and without this the
        // commonest configuration, deferral being off by default, would pay for a cold clone before
        // being told the value was wrong before any of it started.
        if (isDotnetKind)
        {
            ValidateProject(serviceName, metadata.Project);
        }

        // Starts the checkouts an AddService call would have to block on — every "local" service
        // whose first clone nothing else is going to run — at once, on background threads, and
        // returns without waiting for any of them. See LocalCheckoutPrefetch.
        var prefetch = LocalCheckoutPrefetch.For(builder, gitClient);

        var deferred = DeferredCheckout.For(builder);

        if (deferred.ShouldDefer(builder, serviceName, config))
        {
            // Nothing is on disk for this service yet, so registering the resource against the path
            // its checkout will have — and starting it once the clone lands — costs the AppHost
            // nothing it would otherwise have had, and buys it a dashboard while the clone runs.
            //
            // A non-dotnet kind gets the same treatment when it can build its resource without
            // reading the repository, which java always can and javascript can for most of its app
            // types: their endpoints come from the committed catalog rather than from anything in
            // the checkout. SupportsDeferredCheckout is asked first because it is the form of the
            // question that can be asked without registering anything — see ILocalResourceKind —
            // and ResolveDeferred returning null is still honoured for a kind that can only decide
            // once it has looked at everything.
            var registered = isDotnetKind
                ? deferred.Register(
                    builder, serviceName, metadata, config, prefetch, gitClient, prepare.Step, _prepareRunner)
                : SupportsDeferredKind(serviceName, metadata, handler!)
                    ? deferred.RegisterKind(
                        builder, serviceName, metadata, config, prefetch, gitClient, prepare.Step, _prepareRunner,
                        repoRoot => ResolveDeferredKind(builder, serviceName, metadata, repoRoot, handler!))
                    : null;

            if (registered is not null)
            {
                return registered;
            }
        }

        // Blocks on this service's checkout. Usually that checkout was started on the first
        // AddService call together with the other speculative ones, so the wait is for the slowest
        // one overall rather than for this one in turn.
        //
        // One case waits alone: a service the prefetch left out because it would have been deferred
        // (#76), whose kind then declined deferral by returning null from ResolveDeferred after
        // SupportsDeferredCheckout had said yes. There is no prefetched task for it, so GetRepoRoot
        // clones it inline, here, on this thread.
        //
        // That kind is not doing anything wrong: ILocalResourceKind documents deciding late as a
        // legitimate choice, for a kind that can only tell once it has looked at everything, and
        // this path is what keeps it working. What changed is its price. Deciding late used to cost
        // only the eager path, because the prefetch had already started every cold clone regardless
        // of the answer; now the prefetch acts on the early answer, so a late decline is also a
        // clone that runs in turn instead of with the others. Correct, and slower — which is why
        // the interface now says so where a handler author reads it.
        var repoRoot = prefetch.GetRepoRoot(serviceName, metadata, config, builder.AppHostDirectory, gitClient);

        // The working tree is complete and reconciled onto its configured ref; the kind has not yet
        // been allowed to judge it. Both halves of that are load-bearing. After the reconciliation,
        // so the commit the marker keys on is the commit the step ran against. Before the kind,
        // because a kind's checkout checks — ResolveProjectFile below for `dotnet`, Validate for
        // every other — would otherwise reject a checkout for missing precisely the files the step
        // was about to produce. Neither kind knows this exists.
        if (prepare.Step is { } step)
        {
            // Run mode only, which is the same gate DeferredCheckout.ShouldDefer applies and for a
            // related reason: publish mode composes the model, writes the manifest and exits, and a
            // bootstrap produces what a service needs in order to run. Without this, deferral being
            // refused in publish mode means every "local" service takes this path, so an
            // `aspire publish` over a cold checkout pays the full download and import — for the
            // motivating case, hundreds of megabytes and a multi-minute graph build — to emit a
            // manifest that describes none of it.
            if (builder.ExecutionContext.IsRunMode)
            {
                CheckoutPreparation.Run(
                    serviceName, step, repoRoot, builder.AppHostDirectory, managedCheckout, gitClient,
                    _prepareRunner, ConsolePrepareOutputSink.Instance);
            }
            else if (CheckoutPreparation.WouldRun(
                serviceName, step, repoRoot, builder.AppHostDirectory, managedCheckout, gitClient))
            {
                // Only where the step would actually have run. A warm checkout whose marker already
                // satisfies it was not going to run one anyway, and naming it there would report a
                // skip that costs nothing while advising a developer to materialize a checkout they
                // already have.
                ConsolePrepareOutputSink.Instance.Report(
                    CheckoutPreparation.SkippedOutsideRunModeNotice(serviceName, step));
            }
        }

        if (isDotnetKind)
        {
            var projectPath = ResolveProjectFile(serviceName, repoRoot, metadata.Project);

            // Aspire's own AddProject, with a path that exists — so the project picks up every
            // default it normally would (launch-profile endpoints, OTLP exporter, certificate
            // trust, debugging support).
            //
            // This path waits for a real path rather than registering the resource early and
            // filling the path in later, for two independent reasons.
            //
            // AddProject reads the launch profile during composition: WithProjectDefaults calls
            // GetEffectiveLaunchProfile(throwIfNotFound: true), which throws
            // DistributedApplicationException unless the .csproj is on disk. That one is
            // avoidable — either by passing launchProfileName: null, which sets
            // ExcludeLaunchProfile and skips the lookup, or by supplying an IProjectMetadata that
            // answers LaunchSettings itself — but it costs the endpoints Aspire synthesises from
            // the profile's applicationUrl, because those are created here during composition and
            // there is nothing to read them from afterwards. A service registered that way has to
            // declare its endpoints instead. DeferredCheckout takes exactly that trade for a
            // checkout that does not exist yet, where there is no launch profile to lose.
            //
            // The path itself is frozen regardless: DCP bakes it into the executable's working
            // directory and its "--project" argument while preparing the model, which happens
            // before the dashboard is up. Mutating ProjectPath afterwards changes nothing, so the
            // absolute path has to be settled before Build() whatever the launch profile does.
            return ResolvedService.Tag(builder.AddProject(serviceName, projectPath), serviceName, "local");
        }

        // The handler's verdict on the service's configuration, now that there is a checkout to
        // judge it against — the same thing ResolveProjectFile just did for the dotnet kind, and
        // for the same reason: a kind's paths are relative to this directory, so a wrong one can
        // only be recognised here. Immediately before Resolve, and before this service has added
        // anything, so a handler reports it without a half-created resource behind it.
        ValidateWithKindHandler(serviceName, metadata, repoRoot, handler!);

        return InvokeKindHandler(builder, serviceName, metadata, repoRoot, handler!);
    }

    /// <summary>
    /// Looks up the handler for a non-dotnet kind, or throws naming the kind. Deliberately free of
    /// filesystem and network work so it can run as a pre-flight, before the checkout.
    /// </summary>
    private static ILocalResourceKind ResolveKindHandler(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata)
    {
        var registry = LocalKindRegistry.For(builder);

        if (!registry.TryGet(metadata.Kind, out var handler) || handler is null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': kind '{metadata.Kind}' is not registered. " +
                registry.DescribeNearMatch(metadata.Kind) +
                "Call the kind's registration method before the first AddService call " +
                "(builder.UseJavaScript() or builder.UseJava() for the built-in kinds), or " +
                "register your own with builder.AddLocalKind(name, handler).");
        }

        return handler;
    }

    /// <summary>
    /// The deferred counterpart of <see cref="InvokeKindHandler"/>: asks the handler to build its
    /// resource against a <paramref name="repoRoot"/> that does not exist yet. A
    /// <see langword="null"/> result is the handler declining deferral, not a failure, so — unlike
    /// the eager path — it is passed through rather than reported.
    /// </summary>
    private static DeferredLocalResource? ResolveDeferredKind(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, string repoRoot,
        ILocalResourceKind handler)
    {
        DeferredLocalResource? registration;
        try
        {
            registration = handler.ResolveDeferred(builder, serviceName, repoRoot, metadata.KindConfig);
        }
        catch (Exception ex) when (ex is not ServiceSourcesConfigurationException)
        {
            throw new ServiceSourcesConfigurationException(
                GuestLanguagePackages.DescribeMissingPackage(ex, serviceName, metadata.Kind)
                    ?? DeferredHandlerFailedMessage(serviceName, metadata.Kind),
                ex);
        }

        if (registration is not null && registration.Service is null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the handler for kind '{metadata.Kind}' returned a " +
                $"{nameof(DeferredLocalResource)} with no resource. Return null to decline deferral instead.");
        }

        return registration;
    }

    /// <summary>
    /// Asks the handler whether it can defer this service at all. Documented as never throwing, but
    /// nothing enforces that — and this runs for a service the developer did not ask a question
    /// about, so a handler dereferencing something on an odd config block would otherwise take the
    /// AppHost down with a bare exception naming neither the service nor the kind.
    /// </summary>
    private static bool SupportsDeferredKind(
        string serviceName, ServiceMetadata metadata, ILocalResourceKind handler)
    {
        try
        {
            return handler.SupportsDeferredCheckout(metadata.KindConfig);
        }
        catch (Exception ex) when (ex is not ServiceSourcesConfigurationException)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the handler for kind '{metadata.Kind}' failed while being asked whether " +
                "it supports a deferred checkout. That call is documented as answering rather than throwing — a " +
                "block it cannot judge should answer false and let the eager path report it.", ex);
        }
    }

    private static string HandlerFailedMessage(string serviceName, string kind) =>
        $"Service '{serviceName}': the handler for kind '{kind}' failed while creating its " +
        $"resource. If this is a configuration problem, report it from " +
        $"{nameof(ILocalResourceKind)}.{nameof(ILocalResourceKind.Validate)} instead, which core calls " +
        "immediately before this against the same checkout — including for a path that has to be in " +
        "the repository — and before the service has added anything to the app model.";

    /// <summary>
    /// The deferred counterpart of <see cref="HandlerFailedMessage"/>. It cannot point at
    /// <see cref="ILocalResourceKind.Validate"/>: there is no checkout to validate against on this
    /// path, which is why core does not call it here.
    /// </summary>
    private static string DeferredHandlerFailedMessage(string serviceName, string kind) =>
        $"Service '{serviceName}': the handler for kind '{kind}' failed while creating its resource for a " +
        $"checkout that has not landed yet. A check that needs the working tree belongs in " +
        $"{nameof(DeferredLocalResource)}.{nameof(DeferredLocalResource.ValidateCheckout)}, which core runs " +
        "once the clone is there; anything settleable from the options block alone should be reported as a " +
        $"{nameof(ServiceSourcesConfigurationException)} naming the service.";

    /// <summary>
    /// Asks the handler to pass judgement on the service's configuration against its resolved
    /// checkout, before anything is built from it. Wrapped like every other call core makes into a
    /// handler: this one resolves the whole options block and makes every check the kind has against
    /// the working tree, so it reaches a language's hosting package exactly as
    /// <see cref="InvokeKindHandler"/> does — and the identical failure must not report as a bare
    /// load error just because it happened one call earlier.
    /// </summary>
    private static void ValidateWithKindHandler(
        string serviceName, ServiceMetadata metadata, string repoRoot, ILocalResourceKind handler)
    {
        try
        {
            handler.Validate(serviceName, repoRoot, metadata.KindConfig);
        }
        catch (Exception ex) when (ex is not ServiceSourcesConfigurationException)
        {
            throw new ServiceSourcesConfigurationException(
                GuestLanguagePackages.DescribeMissingPackage(ex, serviceName, metadata.Kind)
                    ?? ValidateFailedMessage(serviceName, metadata.Kind),
                ex);
        }
    }

    /// <summary>
    /// For a handler that faulted in <see cref="ILocalResourceKind.Validate"/> rather than reporting
    /// something. Unlike <see cref="HandlerFailedMessage"/> it has nowhere better to point the
    /// author at: this <em>is</em> the place a configuration problem belongs.
    /// </summary>
    private static string ValidateFailedMessage(string serviceName, string kind) =>
        $"Service '{serviceName}': the handler for kind '{kind}' failed while checking the service's " +
        $"configuration against its checkout. A configuration problem should be reported from " +
        $"{nameof(ILocalResourceKind)}.{nameof(ILocalResourceKind.Validate)} as a " +
        $"{nameof(ServiceSourcesConfigurationException)} naming the service; anything else out of that " +
        "call is a fault in the handler.";

    private static IResourceBuilder<IResourceWithServiceDiscovery> InvokeKindHandler(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, string repoRoot,
        ILocalResourceKind handler)
    {
        IResourceBuilder<IResourceWithServiceDiscovery>? resourceBuilder;
        try
        {
            resourceBuilder = handler.Resolve(builder, serviceName, repoRoot, metadata.KindConfig);
        }
        catch (Exception ex) when (ex is not ServiceSourcesConfigurationException)
        {
            throw new ServiceSourcesConfigurationException(
                GuestLanguagePackages.DescribeMissingPackage(ex, serviceName, metadata.Kind)
                    ?? HandlerFailedMessage(serviceName, metadata.Kind),
                ex);
        }

        if (resourceBuilder is null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the handler for kind '{metadata.Kind}' returned no resource. " +
                $"{nameof(ILocalResourceKind)}.{nameof(ILocalResourceKind.Resolve)} must return the resource it created.");
        }

        return ResolvedService.Tag(resourceBuilder, serviceName, "local");
    }

    /// <summary>
    /// Resolves and validates the project file path for a "dotnet"-kind service whose repo root has
    /// already been resolved.
    /// </summary>
    internal static string ResolveProjectFile(string serviceName, string repoRoot, string? project)
    {
        var projectPath = ConfineProject(serviceName, repoRoot, project);

        if (!File.Exists(projectPath))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': project file '{project}' was not found under '{repoRoot}'.");
        }

        return projectPath;
    }

    /// <summary>
    /// Combines a service's <c>project</c> with its checkout, having confined it to that checkout.
    /// The one place the value is turned into a path, because the eager path and
    /// <see cref="DeferredCheckout"/> both resolve it and must not disagree about what it means.
    /// </summary>
    /// <remarks>
    /// Confined for the reason <c>java.jarPath</c> and a <c>prepare</c> command are:
    /// <c>servicesources.yaml</c> is shared team configuration a developer clones rather than writes,
    /// so an absolute or climbing <c>project</c> would have the AppHost build — and MSBuild evaluate,
    /// imports and inline tasks included — something from outside the checkout the catalog describes.
    /// <see cref="Path.Combine(string, string)"/> gives no confinement of its own: it discards
    /// <paramref name="repoRoot"/> outright for a rooted value and does nothing about <c>..</c>.
    /// <para>
    /// Lexical, so the verdict is the same on both paths — the deferred one judges the value in front
    /// of a checkout that has not landed yet — and so an absolute path is reported as the absolute
    /// path it is rather than as a file missing from a checkout it was never looked for in.
    /// </para>
    /// </remarks>
    internal static string ConfineProject(string serviceName, string repoRoot, string? project)
    {
        ValidateProject(serviceName, project);

        return Path.Combine(repoRoot, CheckoutRelativePath.NormalizeSeparators(project));
    }

    /// <summary>
    /// The confinement check on its own, for the callers that have a <c>project</c> to judge before
    /// they have a checkout to combine it with. Lexical, so it is the same verdict
    /// <see cref="ConfineProject"/> reaches later — running it twice costs nothing and keeps the
    /// value judged in front of the clone as well as at the point it becomes a path.
    /// </summary>
    internal static void ValidateProject(string serviceName, [NotNull] string? project)
    {
        // Required, and reported as that rather than as a file that is not there: the "dotnet" kind
        // resolves the whole service from this one value, so a service without it names nothing to
        // run. Null and not "" when the key is written with nothing after it — YamlDotNet parses an
        // empty scalar as null, overriding the default, which is what ServiceCatalogLoader
        // normalizes 'kind' for and does not normalize this — and whitespace survives quoting, so
        // all three spellings are caught here rather than one of them being combined with the
        // checkout root and reported after a clone as a .csproj that never appeared.
        if (string.IsNullOrWhiteSpace(project))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': 'project' is required for a 'local' service of kind 'dotnet'. It names "
                + "the project file to run, relative to the service's checkout — for example "
                + "'src/Orders.Api/Orders.Api.csproj'.");
        }

        if (CheckoutRelativePath.IsAbsolute(project))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': project '{project}' is an absolute path. 'project' has to be a path "
                + "relative to the service's checkout — it names a project the repository commits, not one "
                + "sitting elsewhere on a developer's machine.");
        }

        if (CheckoutRelativePath.UnusableSegment(project) is { } unusable)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': project '{project}' has a path segment '{unusable}' — "
                + CheckoutRelativePath.OnlyDotsAndSpacesRuleAndRemedy);
        }

        if (CheckoutRelativePath.EscapesRoot(project))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': project '{project}' points outside the service's checkout. It must "
                + "stay within the repository.");
        }
    }
}
