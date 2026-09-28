using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Messages;

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

    public IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null)
    {
        // Ahead of everything below, EnsureAvailable included, because it is the one check that can
        // say this service has nothing to clone at all — and a cold clone is what the answer saves.
        RequireRepositoryToCheckOut(serviceName, definition, config);

        // Before any network work: a machine without a usable git can't clone anything, and
        // finding that out once here beats finding it out as an identical clone failure on every
        // service the catalog holds. Cheap after the first call, which is why it sits on the hot
        // path rather than behind a one-shot flag of its own.
        gitClient.EnsureAvailable();

        var isDotnetKind = string.Equals(definition.Kind, LocalKinds.Dotnet, StringComparison.Ordinal);

        // Settled before paying for a checkout: looking the kind up is a dictionary probe against
        // registry state, needs no working tree, and running it after the clone would make a typo'd
        // kind — or a kind nobody registered — cost a cold clone of this repository before saying
        // so. Only the first "repository" AddService gets even that ahead of every clone: the prefetch
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
        var handler = isDotnetKind ? null : ResolveKindHandler(builder, serviceName, definition);

        // Whether this package owns the checkout directory, which decides whether the service
        // inherits the catalog's prepare block at all and where its completion marker goes.
        var managedCheckout = LocalGitCheckout.IsManagedCheckout(config);

        if (config.Local.IsDeclared)
        {
            // The deprecated 'local' spelling — already merged into config.Repository by
            // ServiceDeveloperConfig.ReconcileRepositoryAlias — earns its rename notice only once a
            // service actually resolves through "repository", the same rule the repository.path
            // notice below follows: a stray 'local' block on an entry nothing resolves this way
            // (a different source, or a service this AppHost never adds) is not this developer's
            // problem to hear about. Local.IsDeclared still answers correctly here because the
            // reconciliation merge never clears it.
            ServiceSourcesWarnings.For(builder).AddNotice(RepositoryAliasDeprecationNotice(serviceName));
        }

        if (!managedCheckout)
        {
            // 'repository.path' is deprecated in favor of the first-class 'path' source (design
            // "local.path is deprecated, not removed yet") — a soft deprecation, not a break: the
            // mechanism keeps working exactly as it does today, and only a one-time notice is owed.
            // AddNotice dedupes identical text, so a service resolved more than once in a run
            // reports this only once.
            ServiceSourcesWarnings.For(builder).AddNotice(LocalPathDeprecationNotice(serviceName, config));
        }

        // Whether this service is on its own repository or sharing one with others — the same test
        // Task 8's grouped-checkout handling reuses. A repository's own CheckoutName is the service's
        // own name for the common, ungrouped case (design finding 2, #291), so this only diverges for
        // a service declared through AddRepository/WithSharedRepository or yaml's repositoryRef.
        var label = definition.Repository.CheckoutName == serviceName
            ? PreparePlan.ServiceLabel(serviceName)
            : PreparePlan.RepositoryLabel(definition.Repository.CheckoutName);

        // The other configuration check that needs no working tree, and the last one standing in
        // front of the clone now that a kind's own Validate has moved below it. Being core's own it
        // also runs ahead of ShouldDefer, so it covers both paths — a typo'd mode, or a command
        // climbing out of the checkout, is reported at composition for a deferred service too,
        // before anything is registered against a directory that does not exist yet. Only what needs
        // the working tree waits for one, which is the division ValidateCheckout draws for a kind.
        var prepare = PreparePlan.For(
            serviceName, label, definition.Repository.Prepare, config.Repository.Prepare, managedCheckout,
            OperatingSystem.IsWindows());

        if (prepare.IgnoredCatalogNotice is { } ignored)
        {
            // A catalog block on a service resolved through 'repository.path' is ignored rather
            // than rejected: it is the team's, and applies correctly to every developer on a
            // managed checkout, so one developer's override must not turn a shared catalog field
            // into a failure. Buffered because there is no logger yet.
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
            ValidateProject(serviceName, definition.Project);
        }

        // Starts the checkouts an AddService call would have to block on — every "repository" service
        // whose first clone nothing else is going to run — at once, on background threads, and
        // returns without waiting for any of them. See LocalCheckoutPrefetch.
        var prefetch = LocalCheckoutPrefetch.For(builder, gitClient);

        var deferred = DeferredCheckout.For(builder);

        if (deferred.ShouldDefer(builder, serviceName, definition, config))
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
                    builder, serviceName, definition, config, repositoryConfig, prefetch, gitClient, prepare.Step,
                    _prepareRunner)
                : SupportsDeferredKind(serviceName, definition, handler!)
                    ? deferred.RegisterKind(
                        builder, serviceName, definition, config, repositoryConfig, prefetch, gitClient,
                        prepare.Step, _prepareRunner,
                        repoRoot => ResolveDeferredKind(builder, serviceName, definition, repoRoot, handler!))
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
        string repoRoot;

        // Held across this whole span: with grouping (#291), two services can share a CheckoutName,
        // and this span both reconciles the working tree onto its configured ref (inside
        // GetRepoRoot) and may run a bootstrap command against it — neither of which is safe to run
        // twice at once over the identical directory. See CheckoutNameLock.
        using (CheckoutNameLock.For(builder).Acquire(definition.Repository.CheckoutName))
        {
            repoRoot = prefetch.GetRepoRoot(
                serviceName, definition, config, repositoryConfig, builder.AppHostDirectory, gitClient);

            // The working tree is complete and reconciled onto its configured ref; the kind has not
            // yet been allowed to judge it. Both halves of that are load-bearing. After the
            // reconciliation, so the commit the marker keys on is the commit the step ran against.
            // Before the kind, because a kind's checkout checks — ResolveProjectFile below for
            // `dotnet`, Validate for every other — would otherwise reject a checkout for missing
            // precisely the files the step was about to produce. Neither kind knows this exists.
            if (prepare.Step is { } step)
            {
                // Run mode only (see RunOrReportSkip). Without that gate, deferral being refused in
                // publish mode means every "repository" service takes this path, so an `aspire
                // publish` over a cold checkout would pay the full bootstrap — for the motivating
                // case, hundreds of megabytes and a multi-minute graph build — to emit a manifest that
                // describes none of it.
                CheckoutPreparation.RunOrReportSkip(
                    builder, serviceName, label, definition.Repository.CheckoutName, step, repoRoot,
                    managedCheckout, gitClient, _prepareRunner);
            }
        }

        if (isDotnetKind)
        {
            var projectPath = ResolveProjectFile(serviceName, repoRoot, definition.Project);

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
            //
            // The options overload is used when the catalog sets a profile, because it sets the name and
            // the exclusion independently.
            return ResolvedService.Bridge(
                AddDotnetProject(builder, serviceName, projectPath, definition.Dotnet), serviceName, "repository");
        }

        // The handler's verdict on the service's configuration, now that there is a checkout to
        // judge it against — the same thing ResolveProjectFile just did for the dotnet kind, and
        // for the same reason: a kind's paths are relative to this directory, so a wrong one can
        // only be recognised here. Immediately before Resolve, and before this service has added
        // anything, so a handler reports it without a half-created resource behind it.
        ValidateWithKindHandler(serviceName, definition, repoRoot, handler!);

        return InvokeKindHandler(builder, serviceName, definition, repoRoot, handler!, "repository");
    }

    /// <summary>
    /// The one-time notice for a service resolved through the deprecated <c>local</c> block —
    /// <see cref="ServiceDeveloperConfig.ReconcileRepositoryAlias"/> already merged it into
    /// <see cref="ServiceDeveloperConfig.Repository"/> by the time this runs, so this only decides
    /// when the notice is owed: once a service actually resolves through <c>"repository"</c>, never
    /// merely because some configuration layer still writes <c>local</c> for an entry nothing reads
    /// that way.
    /// </summary>
    private static Raw RepositoryAliasDeprecationNotice(string serviceName) =>
        Raw.Compose(
            $"Service '{new Name(serviceName)}': the 'local' block is deprecated — renamed to 'repository' so "
            + $"it reads as the source's own name rather than colliding in spelling with "
            + $"'{Raw.Literal(DeveloperConfiguration.FileName)}'. Same fields ('path', 'ref', 'prepare'); it "
            + $"keeps working exactly as written, so this notice is only the nudge to rename it.");

    /// <summary>
    /// The design "<c>local.path</c> is deprecated, not removed yet" notice — <c>repository.path</c>
    /// keeps resolving exactly as it does today, but the first-class <c>"path"</c> source now does the
    /// same job (no clone, no ref), discoverable rather than hidden inside <c>"repository"</c>.
    /// </summary>
    /// <remarks>
    /// The remedy names <c>path.path</c> — <c>path</c> is a block, so a bare <c>"path": "..."</c> would
    /// be refused by the config validator. A developer's <c>path.path</c> behaves as
    /// <c>repository.path</c> does, catalog <c>prepare</c> step included (ignored, with its own
    /// notice); the one thing that does not carry over is a <c>repository.prepare</c> block, named
    /// only where there is one.
    /// <para>
    /// Named for the field, not the block spelling: <paramref name="config"/> has already been
    /// through <see cref="ServiceDeveloperConfig.ReconcileRepositoryAlias"/> by the time this runs, so
    /// <see cref="ServiceDeveloperConfig.Repository"/> holds the effective value whether the developer
    /// wrote it under <c>repository</c> or the deprecated <c>local</c> — and the remedy always names
    /// the current spelling, same as <see cref="DeveloperConfigShape.ThrowIfRetiredSource"/> does for
    /// the source rename.
    /// </para>
    /// </remarks>
    private static Raw LocalPathDeprecationNotice(string serviceName, ServiceDeveloperConfig config)
    {
        // Double-quoted and escaped through Raw.Escaped (Name's JSON-compatible escaping), because
        // this snippet is meant to be pasted into servicesources.local.json as it stands — a Windows
        // path's backslashes quoted by hand would be invalid JSON, or silently a different string.
        var notice = Raw.Compose($"Service '{new Name(serviceName)}': 'repository.path' is deprecated. Use the 'path' "
            + $"source instead: \"source\": \"path\", \"path\": {{ \"path\": \"{Raw.Escaped(config.Repository.Path)}\" }} "
            + $"— which resolves the directory the same way, with no clone and no ref.");

        if (config.Repository.Prepare?.IsDeclared == true)
        {
            notice = Raw.Compose($"{notice} Move this service's 'repository.prepare' block to 'path.prepare': it is "
                + $"not read under the 'path' source.");
        }

        return notice;
    }

    /// <summary>
    /// Refuses a <c>"repository"</c> service whose catalog entry declares no repository to clone.
    /// </summary>
    /// <remarks>
    /// A <c>repository.path</c> override is exempt: it points at a checkout the developer already
    /// has, so nothing is ever cloned and the absent <c>repository</c> costs that configuration
    /// nothing.
    /// <para>
    /// The message offers only remedies this guard itself can verify. Switching to <c>url</c>,
    /// <c>container</c> or <c>kubernetes</c> works only if that source's own preconditions hold —
    /// none of which is readable from here — so it is not offered; a declared block is not proof
    /// those preconditions hold either. The remedies offered are declaring a repository and the
    /// <c>"path"</c> source with a <c>path.path</c> override — the non-deprecated form of the
    /// <c>repository.path</c> exemption above — and the configuration key is named in every layer
    /// that can set it.
    /// </para>
    /// </remarks>
    private static void RequireRepositoryToCheckOut(
        string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config)
    {
        if (!LocalGitCheckout.IsManagedCheckout(config)
            || LocalGitCheckout.HasRepositoryToClone(definition))
        {
            return;
        }

        var serviceKey = Raw.Compose($"{Raw.Literal(DeveloperConfiguration.ServicesKey)}:{new Name(serviceName)}");

        throw ServiceSourcesConfigurationException.For(
            $"Service '{new Name(serviceName)}' source is 'repository' but {Raw.Origin(definition.Origin)} gives it no "
            + $"repository to clone. Either {DeclareRepositoryRemedy(serviceName, definition)}, or {PathSourceRemedy(serviceKey, definition)} "
            + $"{WhereTheSourceCanBeSet(serviceName)}");
    }

    /// <summary>
    /// The <c>"path"</c> source as the other remedy. Where the catalog already declares a
    /// <c>path:</c>, switching the source is all it takes; only without one does the developer also
    /// need a <c>path.path</c> naming a directory of their own.
    /// </summary>
    private static Raw PathSourceRemedy(Raw serviceKey, ServiceDefinition definition) =>
        string.IsNullOrWhiteSpace(definition.Path)
            ? Raw.Compose($"switch it to the 'path' source — set '{serviceKey}:source' to 'path' and "
                + $"'{serviceKey}:path:path' to a checkout you already have on disk, which needs no repository.")
            : Raw.Compose($"set '{serviceKey}:source' to 'path' — {Raw.Origin(definition.Origin)} already gives it a "
                + $"'path', which needs no repository.");

    /// <summary>
    /// The sentence naming every configuration layer a service's <c>source</c> can come from, so a
    /// reader whose source was set by the environment or a catalog default is not sent to edit a file
    /// that holds nothing. Shared by every message that tells a developer to change the source.
    /// </summary>
    internal static Raw WhereTheSourceCanBeSet(string serviceName)
    {
        var serviceKey = Raw.Compose($"{Raw.Literal(DeveloperConfiguration.ServicesKey)}:{new Name(serviceName)}");

        return Raw.Compose($"'{serviceKey}:source' can be set from any configuration layer: "
            + $"{Raw.Literal(DeveloperConfiguration.FileName)}, appsettings, user secrets, the environment variable "
            + $"{Raw.Escaped(DeveloperConfiguration.EnvironmentVariableFor(serviceName))}, or the command line.");
    }

    /// <summary>
    /// How to give this service a repository, in the terms of the catalog that declared it — yaml
    /// property names for a yaml entry, builder calls for a code-declared one.
    /// </summary>
    private static Raw DeclareRepositoryRemedy(string serviceName, ServiceDefinition definition)
    {
        var isCode = definition.Origin.Kind == CatalogOriginKind.Code;

        // A grouped service declares a repositoryRef and no repository of its own, so telling the
        // reader their entry declares neither is false and sends them to the wrong entry: what is
        // missing is the url on the repository they named.
        if (LocalGitCheckout.IsGrouped(definition, serviceName))
        {
            return isCode
                ? Raw.Compose($"give the shared repository '{new Name(definition.Repository.CheckoutName)}' a url where it is declared")
                : Raw.Compose($"give the 'repositories' entry '{new Name(definition.Repository.CheckoutName)}' a 'repository' url");
        }

        // "give ... a url" rather than "add a 'repository'": the predicate above is
        // IsNullOrWhiteSpace, so an entry that already carries a blank 'repository:' scalar reaches
        // here, and telling its author to add the key they can see would be advice they cannot take.
        // No repositoryRef is offered for the same reason: the loader refuses one on any entry
        // carrying a 'repository' key at all — blank included — which is not readable from here.
        return isCode
            ? Raw.Literal("declare it with WithRepository(...) or WithSharedRepository(...)")
            : Raw.Literal("give its catalog entry a 'repository' url");
    }

    /// <summary>
    /// Looks up the handler for a non-dotnet kind, or throws naming the kind. Deliberately free of
    /// filesystem and network work so it can run as a pre-flight, before the checkout.
    /// </summary>
    internal static ILocalResourceKind ResolveKindHandler(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition)
    {
        var registry = LocalKindRegistry.For(builder);

        if (!registry.TryGet(definition.Kind, out var handler))
        {
            // "java"/"javascript" always resolve via LocalKindRegistry's own built-in fallback, so
            // reaching here means an unregistered third-party kind — there is no Use*() call to
            // suggest for one core doesn't know about.
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': kind '{new Name(definition.Kind)}' is not registered. "
                + $"{registry.DescribeNearMatch(definition.Kind)}"
                + $"Register it with builder.AddLocalKind(name, handler) before the first AddService call.");
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
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition, string repoRoot,
        ILocalResourceKind handler)
    {
        DeferredLocalResource? registration;
        try
        {
            registration = handler.ResolveDeferred(builder, serviceName, repoRoot, definition.KindOptions);
        }
        catch (ServiceSourcesConfigurationException ex) when (IsCodeOriginIndentationAdvice(definition, ex))
        {
            throw LocalKindConfig.RewriteIndentationAdviceForCodeOrigin(ex);
        }
        catch (Exception ex) when (ex is not ServiceSourcesConfigurationException)
        {
            var message = GuestLanguagePackages.DescribeMissingPackage(ex, serviceName, definition.Kind) is { } missingPackage
                ? Raw.Escaped(missingPackage)
                : DeferredHandlerFailedMessage(serviceName, definition.Kind);

            throw ServiceSourcesConfigurationException.For($"{message}", ex);
        }

        if (registration is not null && registration.Service is null)
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': the handler for kind '{new Name(definition.Kind)}' returned a "
                + $"{Raw.Literal(nameof(DeferredLocalResource))} with no resource. Return null to decline deferral instead.");
        }

        if (registration is not null)
        {
            RequireDeclaredResourceType(serviceName, definition.Kind, handler, registration.Service.Resource);
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
        string serviceName, ServiceDefinition definition, ILocalResourceKind handler)
    {
        try
        {
            return handler.SupportsDeferredCheckout(definition.KindOptions);
        }
        catch (Exception ex) when (ex is not ServiceSourcesConfigurationException)
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': the handler for kind '{new Name(definition.Kind)}' failed while being asked whether "
                + $"it supports a deferred checkout. That call is documented as answering rather than throwing — a "
                + $"block it cannot judge should answer false and let the eager path report it.",
                ex);
        }
    }

    /// <remarks>
    /// Always answers: a developer's own <c>repository.path</c> makes <c>"repository"</c> selectable
    /// even for an entry with no repository.
    /// </remarks>
    public Type? DeclaredResourceType(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition) =>
        KindResourceType(builder, serviceName, definition);

    /// <summary>
    /// The type a directory-backed source resolves <paramref name="definition"/>'s kind to, shared
    /// with <see cref="PathSource"/>, which dispatches kinds the same way.
    /// </summary>
    internal static Type KindResourceType(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition)
    {
        if (string.Equals(definition.Kind, LocalKinds.Dotnet, StringComparison.Ordinal))
        {
            return typeof(ProjectResource);
        }

        if (!LocalKindRegistry.For(builder).TryGet(definition.Kind, out var handler))
        {
            return typeof(IResourceWithServiceDiscovery);
        }

        try
        {
            return ReadResourceType(serviceName, definition.Kind, handler);
        }
        catch (Exception ex) when (GuestLanguagePackages.DescribeMissingPackage(ex, serviceName, definition.Kind) is not null)
        {
            // The type becomes reachable once the package is installed, so claim nothing.
            return typeof(IResourceWithServiceDiscovery);
        }
    }

    /// <summary>
    /// Holds a kind to its <see cref="ILocalResourceKind.ResourceType"/>, since <c>Unwrap&lt;T&gt;</c>
    /// throws for other sources' developers on the strength of it.
    /// </summary>
    internal static void RequireDeclaredResourceType(
        string serviceName, string kind, ILocalResourceKind handler, IResource resource)
    {
        Type declared;
        try
        {
            declared = ReadResourceType(serviceName, kind, handler);
        }
        catch (Exception ex) when (GuestLanguagePackages.DescribeMissingPackage(ex, serviceName, kind) is { } missingPackage)
        {
            throw ServiceSourcesConfigurationException.For($"{Raw.Escaped(missingPackage)}", ex);
        }

        if (!declared.IsInstanceOfType(resource))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': the handler for kind '{new Name(kind)}' returned "
                + $"{Raw.Escaped(resource.GetType().Name)}, but its "
                + $"{Raw.Literal(nameof(ILocalResourceKind))}.{Raw.Literal(nameof(ILocalResourceKind.ResourceType))} "
                + $"is {Raw.Escaped(declared.Name)}. Declare a type every resource it returns is, or "
                + $"derives from — or leave the member unimplemented.");
        }
    }

    /// <summary>
    /// Reads <see cref="ILocalResourceKind.ResourceType"/>, naming the service and kind when the
    /// handler faults. A missing hosting package is left to the caller, which knows what it means.
    /// </summary>
    private static Type ReadResourceType(string serviceName, string kind, ILocalResourceKind handler)
    {
        Type? declared;
        try
        {
            declared = handler.ResourceType;
        }
        catch (Exception ex) when (ex is not ServiceSourcesConfigurationException
            && GuestLanguagePackages.DescribeMissingPackage(ex, serviceName, kind) is null)
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': the handler for kind '{new Name(kind)}' failed while reporting its "
                + $"{Raw.Literal(nameof(ILocalResourceKind))}.{Raw.Literal(nameof(ILocalResourceKind.ResourceType))}.",
                ex);
        }

        return declared ?? throw ServiceSourcesConfigurationException.For(
            $"Service '{new Name(serviceName)}': the handler for kind '{new Name(kind)}' returned null from "
            + $"{Raw.Literal(nameof(ILocalResourceKind))}.{Raw.Literal(nameof(ILocalResourceKind.ResourceType))}. "
            + $"Leave the member unimplemented to claim nothing.");
    }

    private static Raw HandlerFailedMessage(string serviceName, string kind) =>
        Raw.Compose($"Service '{new Name(serviceName)}': the handler for kind '{new Name(kind)}' failed while creating its "
            + $"resource. If this is a configuration problem, report it from "
            + $"{Raw.Literal(nameof(ILocalResourceKind))}.{Raw.Literal(nameof(ILocalResourceKind.Validate))} instead, which core calls "
            + $"immediately before this against the same checkout — including for a path that has to be in "
            + $"the repository — and before the service has added anything to the app model.");

    /// <summary>
    /// The deferred counterpart of <see cref="HandlerFailedMessage"/>. It cannot point at
    /// <see cref="ILocalResourceKind.Validate"/>: there is no checkout to validate against on this
    /// path, which is why core does not call it here.
    /// </summary>
    private static Raw DeferredHandlerFailedMessage(string serviceName, string kind) =>
        Raw.Compose($"Service '{new Name(serviceName)}': the handler for kind '{new Name(kind)}' failed while creating its resource for a "
            + $"checkout that has not landed yet. A check that needs the working tree belongs in "
            + $"{Raw.Literal(nameof(DeferredLocalResource))}.{Raw.Literal(nameof(DeferredLocalResource.ValidateCheckout))}, which core runs "
            + $"once the clone is there; anything settleable from the options block alone should be reported as a "
            + $"{Raw.Literal(nameof(ServiceSourcesConfigurationException))} naming the service.");

    /// <summary>
    /// Asks the handler to pass judgement on the service's configuration against its resolved
    /// checkout, before anything is built from it. Wrapped like every other call core makes into a
    /// handler: this one resolves the whole options block and makes every check the kind has against
    /// the working tree, so it reaches a language's hosting package exactly as
    /// <see cref="InvokeKindHandler"/> does — and the identical failure must not report as a bare
    /// load error just because it happened one call earlier.
    /// </summary>
    internal static void ValidateWithKindHandler(
        string serviceName, ServiceDefinition definition, string repoRoot, ILocalResourceKind handler)
    {
        try
        {
            handler.Validate(serviceName, repoRoot, definition.KindOptions);
        }
        catch (ServiceSourcesConfigurationException ex) when (IsCodeOriginIndentationAdvice(definition, ex))
        {
            throw LocalKindConfig.RewriteIndentationAdviceForCodeOrigin(ex);
        }
        catch (Exception ex) when (ex is not ServiceSourcesConfigurationException)
        {
            var message = GuestLanguagePackages.DescribeMissingPackage(ex, serviceName, definition.Kind) is { } missingPackage
                ? Raw.Escaped(missingPackage)
                : ValidateFailedMessage(serviceName, definition.Kind);

            throw ServiceSourcesConfigurationException.For($"{message}", ex);
        }
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is the shape-rejection message from
    /// <see cref="LocalKindConfig.Parse{T}"/> reaching a code-declared service — the one case where
    /// core, not <see cref="LocalKindConfig"/> itself, has to re-render the advice, since only core
    /// knows <see cref="ServiceDefinition.Origin"/>. Every other exception is left as thrown.
    /// </summary>
    private static bool IsCodeOriginIndentationAdvice(ServiceDefinition definition, ServiceSourcesConfigurationException ex) =>
        definition.Origin.Kind == CatalogOriginKind.Code
        && ex.Message.Contains(LocalKindConfig.IndentationAdvice, StringComparison.Ordinal);

    /// <summary>
    /// For a handler that faulted in <see cref="ILocalResourceKind.Validate"/> rather than reporting
    /// something. Unlike <see cref="HandlerFailedMessage"/> it has nowhere better to point the
    /// author at: this <em>is</em> the place a configuration problem belongs.
    /// </summary>
    private static Raw ValidateFailedMessage(string serviceName, string kind) =>
        Raw.Compose($"Service '{new Name(serviceName)}': the handler for kind '{new Name(kind)}' failed while checking the service's "
            + $"configuration against its checkout. A configuration problem should be reported from "
            + $"{Raw.Literal(nameof(ILocalResourceKind))}.{Raw.Literal(nameof(ILocalResourceKind.Validate))} as a "
            + $"{Raw.Literal(nameof(ServiceSourcesConfigurationException))} naming the service; anything else out of that "
            + $"call is a fault in the handler.");

    /// <param name="source">
    /// The catalog's own <c>source</c> name for the <see cref="ServiceSourceAnnotation"/> the
    /// resolved resource carries — <c>"repository"</c> here, or <c>"path"</c> for
    /// <see cref="PathSource"/>, which reuses this dispatch unchanged (design finding 1).
    /// </param>
    internal static IResourceBuilder<ServiceResource> InvokeKindHandler(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition, string repoRoot,
        ILocalResourceKind handler, string source)
    {
        IResourceBuilder<IResourceWithServiceDiscovery>? resourceBuilder;
        try
        {
            resourceBuilder = handler.Resolve(builder, serviceName, repoRoot, definition.KindOptions);
        }
        catch (ServiceSourcesConfigurationException ex) when (IsCodeOriginIndentationAdvice(definition, ex))
        {
            throw LocalKindConfig.RewriteIndentationAdviceForCodeOrigin(ex);
        }
        catch (Exception ex) when (ex is not ServiceSourcesConfigurationException)
        {
            var message = GuestLanguagePackages.DescribeMissingPackage(ex, serviceName, definition.Kind) is { } missingPackage
                ? Raw.Escaped(missingPackage)
                : HandlerFailedMessage(serviceName, definition.Kind);

            throw ServiceSourcesConfigurationException.For($"{message}", ex);
        }

        if (resourceBuilder is null)
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': the handler for kind '{new Name(definition.Kind)}' returned no resource. "
                + $"{Raw.Literal(nameof(ILocalResourceKind))}.{Raw.Literal(nameof(ILocalResourceKind.Resolve))} must return the resource it created.");
        }

        RequireDeclaredResourceType(serviceName, definition.Kind, handler, resourceBuilder.Resource);

        return ResolvedService.Bridge(resourceBuilder, serviceName, source);
    }

    internal static IResourceBuilder<ProjectResource> AddDotnetProject(
        IDistributedApplicationBuilder builder, string serviceName, string projectPath, DotnetMetadata? dotnet)
    {
        // Aspire silently starts with no profile when a named one is missing, so it is checked first.
        LaunchProfileCheck.Verify(serviceName, projectPath, DotnetMetadata.Resolve(dotnet).Name);

        var options = ToProjectResourceOptions(dotnet);
        if (options is null)
        {
            return builder.AddProject(serviceName, projectPath);
        }

#pragma warning disable ASPIREPROJECTS001 // ProjectResourceOptions is [Experimental]; the options overload sets name and exclude independently.
        return builder.AddProject(serviceName, projectPath, project =>
        {
            project.LaunchProfileName = options.LaunchProfileName;
            project.ExcludeLaunchProfile = options.ExcludeLaunchProfile;
        });
#pragma warning restore ASPIREPROJECTS001
    }

#pragma warning disable ASPIREPROJECTS001 // ProjectResourceOptions is [Experimental]; the options overload sets name and exclude independently.
    internal static ProjectResourceOptions? ToProjectResourceOptions(DotnetMetadata? dotnet)
    {
        var (name, exclude) = DotnetMetadata.Resolve(dotnet);
        return name is null && !exclude
            ? null
            : new ProjectResourceOptions { LaunchProfileName = name, ExcludeLaunchProfile = exclude };
    }
#pragma warning restore ASPIREPROJECTS001

    /// <summary>
    /// Resolves and validates the project file path for a "dotnet"-kind service whose repo root has
    /// already been resolved.
    /// </summary>
    internal static string ResolveProjectFile(
        string serviceName, string repoRoot, string? project, string sourceName = "repository")
    {
        var projectPath = ConfineProject(serviceName, repoRoot, project, sourceName);

        if (!File.Exists(projectPath))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': project file '{Raw.Escaped(project)}' was not found under '{Raw.Escaped(repoRoot)}'.");
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
    internal static string ConfineProject(
        string serviceName, string repoRoot, string? project, string sourceName = "repository")
    {
        ValidateProject(serviceName, project, sourceName);

        return Path.Combine(repoRoot, CheckoutRelativePath.NormalizeSeparators(project));
    }

    /// <summary>
    /// The confinement check on its own, for the callers that have a <c>project</c> to judge before
    /// they have a checkout to combine it with. Lexical, so it is the same verdict
    /// <see cref="ConfineProject"/> reaches later — running it twice costs nothing and keeps the
    /// value judged in front of the clone as well as at the point it becomes a path.
    /// </summary>
    internal static void ValidateProject(
        string serviceName, [NotNull] string? project, string sourceName = "repository")
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
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': 'project' is required for a '{Raw.Escaped(sourceName)}' service of kind 'dotnet'. It names "
                + $"the project file to run, relative to the service's checkout — for example "
                + $"'src/Orders.Api/Orders.Api.csproj'. It belongs on the service's 'servicesources.yaml' entry; "
                + $"'servicesources.local.json' chooses the source and carries no 'project'.");
        }

        ThrowIfBreached(
            serviceName, "project", project, CheckoutRelativePath.FirstBreach(project),
            outside: Raw.Literal("the service's checkout"),
            inside: Raw.Literal("the repository"),
            absoluteReason: Raw.Literal("'project' has to be a path relative to the service's checkout — it names a "
                + "project the repository commits, not one sitting elsewhere on a developer's machine."));
    }

    /// <summary>
    /// The refusal for a catalog-written path that broke a confinement rule, phrased in the caller's
    /// terms. One place for the three messages, so two fields confined the same way cannot drift in
    /// how they report it. Returns when <paramref name="breach"/> is <see langword="null"/>.
    /// </summary>
    /// <param name="field">The catalog field, as the message names it — <c>project</c>, <c>path</c>.</param>
    /// <param name="outside">What the path climbed out of.</param>
    /// <param name="inside">Where it has to stay.</param>
    /// <param name="absoluteReason">Why an absolute value is refused, and what to do instead.</param>
    /// <param name="escapeRemedy">
    /// A sentence to add to the climbing-out refusal, when the caller has one — why the boundary sits
    /// where it does and how to move it.
    /// </param>
    internal static void ThrowIfBreached(
        string serviceName, string field, string value, ConfinementBreach? breach, Raw outside, Raw inside,
        Raw absoluteReason, Raw? escapeRemedy = null)
    {
        switch (breach)
        {
            case { Kind: ConfinementBreachKind.Absolute }:
                throw ServiceSourcesConfigurationException.For(
                    $"Service '{new Name(serviceName)}': {Raw.Escaped(field)} '{Raw.Escaped(value)}' is an absolute path. {absoluteReason}");

            case { Kind: ConfinementBreachKind.UnusableSegment, Segment: var unusable }:
                throw ServiceSourcesConfigurationException.For(
                    $"Service '{new Name(serviceName)}': {Raw.Escaped(field)} '{Raw.Escaped(value)}' has a path segment '{new Name(unusable)}' — "
                    + $"{CheckoutRelativePath.OnlyDotsAndSpacesRuleAndRemedy}");

            case { Kind: ConfinementBreachKind.EscapesRoot }:
                throw ServiceSourcesConfigurationException.For(
                    $"Service '{new Name(serviceName)}': {Raw.Escaped(field)} '{Raw.Escaped(value)}' points outside {outside}. It must "
                    + $"stay within {inside}.{(escapeRemedy is { } remedy ? Raw.Compose($" {remedy}") : Raw.Literal(""))}");
        }
    }
}
