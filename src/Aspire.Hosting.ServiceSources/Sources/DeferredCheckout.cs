using System.Diagnostics;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Git;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Registers a <c>"local"</c> <c>dotnet</c> service whose managed checkout does not exist yet as a
/// project resource that is held back at startup, and starts it once the clone has landed.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the first <c>AddService()</c> call blocks composition until every <c>"local"</c>
/// service's checkout has resolved. On a cold clone the AppHost never reaches the dashboard —
/// there is nothing to look at while several repositories clone — and one checkout failure throws
/// out of composition, taking the whole AppHost with it including the services that were fine.
/// </para>
/// <para>
/// Deferring turns that into ordinary resource lifecycle. The project is registered at the path its
/// checkout <em>will</em> have, marked <c>WithExplicitStart()</c>, and started from a background
/// task once the clone finishes. DCP withholds an explicit-start
/// executable entirely rather than creating it stopped
/// (<c>ExecutableCreator.IsReadyToCreate</c>), so working directory, certificates and execution
/// configuration are all resolved on demand in <c>CreateObjectAsync</c> — after the checkout —
/// and <c>dotnet run --project</c> builds the project then too, rather than at startup.
/// </para>
/// <para>
/// The clone itself is not made any later. It is started from this registration —
/// <see cref="LocalCheckoutPrefetch.StartCheckout"/>, on a background thread — rather than by the
/// speculative prefetch, because a deferred service is the one case where the prefetch does not have
/// to guess: nothing between here and <c>BeforeStartEvent</c> waits on the clone, so it overlaps
/// with the rest of composition wherever it was started from, and the prefetch is free to leave it
/// out and stop cloning services this AppHost never adds (#76). All that changes for this service is
/// who waits for it — a background task after the host is up, instead of composition.
/// </para>
/// <para>
/// What still has to be final before <c>Build()</c> is the <em>path</em>. DCP freezes it into the
/// executable spec (working directory, <c>--project</c>) in
/// <c>ExecutableCreator.PrepareProjectExecutablesAsync</c>, which runs for every project resource
/// at startup regardless of whether the executable is withheld. That is affordable because the
/// managed checkout root is a pure function of the service name
/// (<see cref="LocalGitCheckout.ManagedRepoRoot"/>), so the path is computable from the committed
/// catalog with no network access.
/// </para>
/// <para>
/// Endpoints are the one thing deferral cannot preserve: they are synthesised from the launch
/// profile's <c>applicationUrl</c> while composing, and nothing re-runs that step later. Rather
/// than demand a declaration up front and refuse to run without one — which a run-to-completion
/// worker, whose profile has no <c>applicationUrl</c> on either path, cannot satisfy honestly —
/// the divergence is checked against the repository's real launch profile once the checkout has
/// landed, and reported then. See <see cref="LaunchProfileEndpointWarning"/>.
/// </para>
/// </remarks>
internal sealed class DeferredCheckout
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, DeferredCheckout> Cache = new();

    /// <summary>
    /// The variable <c>dotnet run</c> and <c>dotnet watch</c> report the active profile in, which
    /// <c>WithProjectDefaults</c> sets on the warm path and the restore has to set here.
    /// </summary>
    private const string LaunchProfileNameVariable = "DOTNET_LAUNCH_PROFILE";

    /// <summary>
    /// What the State column says while a service is waiting for its checkout and has no progress of
    /// its own to show — before the clone reports anything, and again once it is done and the
    /// checkout is being reconciled onto its configured ref.
    /// </summary>
    private const string CheckingOutState = "Checking out";

    /// <summary>
    /// What the State column says while the service's <c>prepare</c> step is running — the phase
    /// after the clone and before anything of this service starts.
    /// </summary>
    /// <remarks>
    /// Its own phase rather than more "Checking out", because it is a different wait with a
    /// different cause: the clone is over, and what is taking minutes now is the repository's own
    /// bootstrap. The step's output goes to the resource log alongside it, so the State column says
    /// which phase and the log says how far into it.
    /// </remarks>
    private const string PreparingState = "Preparing";

    /// <summary>
    /// The shortest gap between two published progress updates within one phase. See
    /// <see cref="ReportCloneProgressAsync"/>.
    /// </summary>
    private static readonly TimeSpan ProgressPublishInterval = TimeSpan.FromSeconds(1);

    // Plain object rather than System.Threading.Lock: this package still targets net8.0.
    private readonly object _gate = new();

    private readonly List<Deferred> _deferred = [];

    private bool _enabled;

    private bool _subscribed;

    /// <summary>
    /// One held-back service. <c>Resource</c> is what <c>AddService()</c> handed the AppHost;
    /// <c>HeldBack</c> is everything else registering it added to the app model — empty for
    /// <c>dotnet</c>, the <c>npm install</c> installer for <c>javascript</c> — which has to be
    /// withheld and started alongside it, in the order it was added. <c>OnCheckoutLanded</c> is the
    /// kind's own post-clone work, run once the repository is on disk and before anything starts.
    /// </summary>
    private sealed record Deferred(
        string ServiceName,
        IResource Resource,
        IReadOnlyList<IResource> HeldBack,
        string RepoRoot,
        ServiceMetadata Metadata,
        ServiceDeveloperConfig Config,
        string AppHostDirectory,
        LocalCheckoutPrefetch Prefetch,
        IGitClient GitClient,
        PrepareStep? PrepareStep,
        IPrepareCommandRunner PrepareRunner,
        Action<IResource, string, ILogger> OnCheckoutLanded)
    {
        /// <summary>Everything withheld for this service, in the order it must be started.</summary>
        public IEnumerable<IResource> AllResources => HeldBack.Append(Resource);
    }

    public static DeferredCheckout For(IDistributedApplicationBuilder builder)
    {
        // The factory stays free of side effects, for the reason spelled out in
        // LocalCheckoutPrefetch.For: ConditionalWeakTable.GetValue may run it concurrently for the
        // same key and keep only one result. Subscribing happens on the instance that won, in
        // EnsureSubscribed.
        return Cache.GetValue(builder, static _ => new DeferredCheckout());
    }

    /// <summary>
    /// Turns deferral on for this builder. Off by default: the behaviour is user-visible — a
    /// service that used to be running by the time <c>Build()</c> returned is now started
    /// afterwards — and the package already has consumers.
    /// </summary>
    public void Enable()
    {
        lock (_gate)
        {
            _enabled = true;
        }
    }

    /// <summary>
    /// Whether <paramref name="serviceName"/> should be registered deferred rather than resolved
    /// eagerly. Scoped tightly on purpose: a warm checkout keeps today's path exactly, with full
    /// launch-profile fidelity, so the blast radius is first-run-only.
    /// </summary>
    /// <remarks>
    /// Two decisions layered, in this order: the policy this type owns — opted in, run mode — and
    /// then <see cref="LocalGitCheckout.IsColdManagedCheckout"/>, which is where "is there
    /// anything to clone here" is answered for every caller that needs it. The speculative
    /// prefetch is the other one, and it drops a candidate from its clone set on the strength of
    /// that predicate before leaving the real decision to this method, so the filesystem half has
    /// to be the same rule in both places rather than two that happen to agree.
    /// </remarks>
    public bool ShouldDefer(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDeveloperConfig config)
    {
        lock (_gate)
        {
            if (!_enabled)
            {
                return false;
            }
        }

        // Run mode only. Deferral buys a dashboard to look at while the clone runs, and publish mode
        // has no dashboard, no DCP and no resource lifecycle — it composes the model, writes the
        // manifest and exits. Deferring there would trade the whole point of the manifest for
        // nothing: the resource would be described from a .csproj that is not on disk, so it would
        // carry no launch-profile endpoints and no profile environment, where the eager path clones
        // first and describes the real project. The start task would strand too, waiting on a
        // NotStarted that only DCP publishes.
        //
        // BeforeStartEvent does not distinguish the two — DistributedApplication publishes it in
        // every mode but 'inspect' — so the mode has to be checked here, before anything is
        // registered deferred.
        if (!builder.ExecutionContext.IsRunMode)
        {
            return false;
        }

        // Deferral claims exactly the case where a clone still has to happen, and nothing else.
        //
        // A 'path' override points at a checkout the developer manages themselves: there is nothing
        // to clone, so there is nothing to wait for — and nothing this package is entitled to
        // create at that path if it turns out to be missing. Anything already on disk at the
        // managed root — a complete checkout, or debris from an interrupted clone — goes down the
        // eager path, which is the one that knows how to tell those apart and what to do about
        // each.
        return LocalGitCheckout.IsColdManagedCheckout(builder.AppHostDirectory, serviceName, config);
    }

    /// <summary>
    /// Registers the project resource for a service whose checkout has not happened yet, and
    /// arranges for the checkout — and the start — to run after the host is up.
    /// </summary>
    public IResourceBuilder<IResourceWithServiceDiscovery> Register(
        IDistributedApplicationBuilder builder,
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        LocalCheckoutPrefetch prefetch,
        IGitClient gitClient,
        PrepareStep? prepareStep,
        IPrepareCommandRunner prepareRunner)
    {
        var repoRoot = LocalGitCheckout.ManagedRepoRoot(builder.AppHostDirectory, serviceName);
        var projectPath = Path.Combine(repoRoot, metadata.Project);

        var resource = new ProjectResource(serviceName);

        // Aspire's own AddProject, taken apart into the two steps it is made of, because the path
        // this one is given does not exist yet: AddProject would attach its internal ProjectMetadata,
        // whose launch-settings read throws for a missing .csproj. Everything else is identical —
        // WithProjectDefaults is what supplies OTLP export, console logs, certificate trust and
        // debugging support, and it is public from 13.5.0 precisely so it can be reached like this.
#pragma warning disable ASPIREPROJECTS001 // WithProjectDefaults is [Experimental]; it is the only public route to the project defaults an out-of-tree project resource needs.
        var resourceBuilder = builder.AddResource(resource)
            .WithAnnotation<IProjectMetadata>(new DeferredProjectMetadata(projectPath))
            .WithProjectDefaults(new ProjectResourceOptions())
            .WithExplicitStart();
#pragma warning restore ASPIREPROJECTS001

        Add(
            builder, serviceName, resource, [], repoRoot, metadata, config, prefetch, gitClient, prepareStep,
            prepareRunner,
            (deferredResource, checkoutRoot, logger) =>
                RestoreLaunchProfile(deferredResource, metadata.Project, checkoutRoot, logger));

        return ResolvedService.Tag(resourceBuilder, serviceName, "local");
    }

    /// <summary>
    /// The same for a non-dotnet kind, whose resource only the kind's own
    /// <see cref="ILocalResourceKind"/> knows how to build. <paramref name="resolveDeferred"/> is
    /// handed the path the checkout will have and returns <see langword="null"/> if the kind does
    /// not support deferral, in which case so does this — nothing has been registered and the caller
    /// falls back to the eager path.
    /// </summary>
    /// <remarks>
    /// Withholding is applied here rather than by the handler, and to every resource the handler
    /// added rather than only the one it returned. A kind is entitled to add
    /// helpers of its own next to the service — <c>Aspire.Hosting.JavaScript</c> adds the
    /// <c>npm install</c> installer the app waits on — and every one of them would otherwise be
    /// started by DCP against a directory that does not exist yet. Making that core's job also keeps
    /// it off the list of things a handler author can get wrong.
    /// </remarks>
    public IResourceBuilder<IResourceWithServiceDiscovery>? RegisterKind(
        IDistributedApplicationBuilder builder,
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        LocalCheckoutPrefetch prefetch,
        IGitClient gitClient,
        PrepareStep? prepareStep,
        IPrepareCommandRunner prepareRunner,
        Func<string, DeferredLocalResource?> resolveDeferred)
    {
        var repoRoot = LocalGitCheckout.ManagedRepoRoot(builder.AppHostDirectory, serviceName);

        // Everything the handler adds from here on, identified by reference rather than by index:
        // IResourceCollection supports removal, and a handler that removed one would shift every
        // index after it, so a positional read could withhold and start-command an unrelated
        // resource that was already in the model. Reference identity also keeps a handler free to
        // add a resource named like an existing one, which is what the index was chosen for.
        var before = new HashSet<object>(builder.Resources, ReferenceEqualityComparer.Instance);

        var registration = resolveDeferred(repoRoot);
        if (registration is null)
        {
            // Declining has to be free, and it is only free before anything is registered: nothing
            // can take a resource back out of the app model, so whatever was added would be left
            // orphaned and then collide with the eager retry, which adds the same service again
            // under the same name. The handler is the only one that can fix that, so it is named.
            if (builder.Resources.Any(added => !before.Contains(added)))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': the handler for kind '{metadata.Kind}' added resources to the app " +
                    "model and then declined deferral by returning null from ResolveDeferred. Decide before " +
                    "adding anything — SupportsDeferredCheckout is the side-effect-free place to decline — " +
                    "because resources cannot be removed once added, and the eager path registers this service " +
                    "again.");
            }

            return null;
        }

        var resource = registration.Service.Resource;

        // Only what DCP actually creates. A handler is equally entitled to add a parameter or a
        // connection string beside its service, and DCP never creates one of those — so it never
        // publishes the NotStarted this class waits for, and one in this list would leave the start
        // task waiting for a state that is not coming. The service would then silently never start,
        // on a task nobody awaits, which is the same failure the WaitFor check below exists to
        // prevent. They also have nothing to withhold, having nothing to start.
        var heldBack = builder.Resources
            .Where(added => !before.Contains(added))
            .Where(added => !ReferenceEquals(added, resource) && DcpCreates(added))
            .ToArray();

        // Helpers are started before the service, which is what Aspire.Hosting.JavaScript's installer
        // needs — the app carries a WaitFor on it, so the reverse order would leave the app waiting
        // on a resource still withheld. A helper that waits on the service instead inverts that, and
        // the start loop would sit on it forever: it is awaited in turn, and the thing it waits for
        // has not been released yet. There is no order that satisfies both, so it is refused here.
        // Named rather than left to hang, because a deadlocked background task nobody awaits shows
        // up as a service that simply never starts.
        foreach (var helper in heldBack)
        {
            if (helper.Annotations.OfType<WaitAnnotation>().Any(wait => ReferenceEquals(wait.Resource, resource)))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': the handler for kind '{metadata.Kind}' added resource " +
                    $"'{helper.Name}' alongside the service and gave it a WaitFor on the service itself. A " +
                    "deferred checkout starts the resources a handler added before the service they belong to, " +
                    "so that wait could never be satisfied. Have the service wait on the helper rather than the " +
                    "other way round, or return null from ResolveDeferred to opt this kind out of deferral.");
            }
        }

        foreach (var withheld in heldBack.Append(resource))
        {
            // WithExplicitStart() needs an IResourceBuilder<T>, and the resources the handler added
            // alongside its own are only reachable as IResource. The annotation is all that call
            // adds, so it is added directly — and guarded, because the handler is free to have
            // marked its own resource already.
            if (!withheld.Annotations.OfType<ExplicitStartupAnnotation>().Any())
            {
                withheld.Annotations.Add(new ExplicitStartupAnnotation());
            }
        }

        Add(
            builder, serviceName, resource, heldBack, repoRoot, metadata, config, prefetch, gitClient, prepareStep,
            prepareRunner,
            (_, checkoutRoot, logger) =>
                RunCheckoutValidation(registration, serviceName, metadata.Kind, checkoutRoot, logger));

        return ResolvedService.Tag(registration.Service, serviceName, "local");
    }

    /// <summary>
    /// Whether DCP turns <paramref name="resource"/> into something it starts, and therefore whether
    /// withholding it means anything.
    /// </summary>
    /// <remarks>
    /// Keyed on the resource kinds DCP materialises rather than on
    /// <see cref="IResourceWithoutLifetime"/>: that marker is not on <c>ParameterResource</c> in the
    /// Aspire versions this package supports, so testing for it would let a parameter through. Both
    /// resources the shipped handlers add — a <c>JavaAppExecutableResource</c>, and the
    /// <c>JavaScriptInstallerResource</c> beside a <c>ViteAppResource</c> — are
    /// <see cref="ExecutableResource"/>s. Projects are named separately because
    /// <c>ProjectResource</c> is not one; DCP builds its executable while preparing the model.
    /// </remarks>
    private static bool DcpCreates(IResource resource) =>
        resource is ExecutableResource or ContainerResource
        || resource.Annotations.OfType<IProjectMetadata>().Any();

    /// <summary>
    /// Runs the kind's post-clone checks: the ones core would have taken from
    /// <see cref="ILocalResourceKind.Validate"/> against a warm checkout, which
    /// <see cref="ILocalResourceKind.ResolveDeferred"/> had no checkout to make.
    /// </summary>
    /// <remarks>
    /// A handler that reports a problem any other way than
    /// <see cref="ServiceSourcesConfigurationException"/> is wrapped in one, for the same reason
    /// <c>LocalProjectSource.ValidateWithKindHandler</c> wraps these same checks on the eager path:
    /// the exception is about to become this service's failure state, and "the handler for kind
    /// 'java' failed" is a more useful thing to read there than a bare <c>IOException</c>.
    /// </remarks>
    private static void RunCheckoutValidation(
        DeferredLocalResource registration, string serviceName, string kind, string repoRoot, ILogger logger)
    {
        if (registration.ValidateCheckout is not { } validate)
        {
            return;
        }

        logger.LogInformation(
            "Checking the landed checkout at {RepoRoot} against the service's configuration.", repoRoot);

        try
        {
            validate();
        }
        catch (Exception ex) when (ex is not ServiceSourcesConfigurationException)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the handler for kind '{kind}' failed while checking the checkout that " +
                "had just landed.", ex);
        }
    }

    /// <summary>The dotnet kind's post-clone work: everything the missing launch profile cost it.</summary>
    private static void RestoreLaunchProfile(
        IResource resource, string relativeProject, string repoRoot, ILogger logger)
    {
        // The repository is on disk now, so everything Aspire read from the launch profile while
        // composing — and got nothing for — is finally readable. ResolveProjectFile is the same
        // check the eager path makes; it is what reports a 'project' that names nothing.
        var projectFile = LocalProjectSource.ResolveProjectFile(resource.Name, repoRoot, relativeProject);

        var profile = LandedLaunchProfile.Read(projectFile, resource);

        RestoreLaunchProfileEnvironment(resource, profile, logger);

        // Endpoints are the one part that cannot be put back, so the shortfall is reported
        // rather than enforced: refusing to start a service that would have run fine is worse
        // than telling the developer their port is not Aspire's to manage.
        if (LaunchProfileEndpointWarning(resource.Name, profile, resource) is { } warning)
        {
            logger.LogWarning("{Warning}", warning);
        }
    }

    private void Add(
        IDistributedApplicationBuilder builder,
        string serviceName,
        IResource resource,
        IReadOnlyList<IResource> heldBack,
        string repoRoot,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        LocalCheckoutPrefetch prefetch,
        IGitClient gitClient,
        PrepareStep? prepareStep,
        IPrepareCommandRunner prepareRunner,
        Action<IResource, string, ILogger> onCheckoutLanded)
    {
        // The clone starts here, not in the speculative phase: the prefetch leaves a service that
        // would be deferred out of its set precisely because this call will claim it (#76). Starting
        // it now rather than when the start task gets to it keeps it overlapping with the rest of
        // composition — nothing between here and BeforeStartEvent waits on it.
        //
        // It also marks the service requested, which it must: the prefetch decides what to report as
        // speculative work at BeforeStartEvent, before a deferred service has waited on anything.
        prefetch.StartCheckout(serviceName, metadata, config, builder.AppHostDirectory, gitClient);

        lock (_gate)
        {
            _deferred.Add(new Deferred(
                serviceName, resource, heldBack, repoRoot, metadata, config, builder.AppHostDirectory, prefetch,
                gitClient, prepareStep, prepareRunner, onCheckoutLanded));
        }

        EnsureSubscribed(builder);
    }

    private void EnsureSubscribed(IDistributedApplicationBuilder builder)
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
        }

        // BeforeStartEvent, not AfterResourcesCreatedEvent. "After resources created" sounds like
        // the right moment and is not: a resource with an unsatisfied WaitFor annotation is not
        // created until that wait resolves, so the event sits behind the whole wait graph — and
        // anything that waits on a deferred service is itself part of that graph. Subscribing there
        // deadlocks the common case, because the event that would start the service cannot fire
        // until the service that is waiting to be started has started. Each task waits for its own
        // resource instead, in StartDeferredAsync, which needs nothing from the rest of the graph.
        builder.Eventing.Subscribe<BeforeStartEvent>((@event, cancellationToken) =>
        {
            StartAll(@event.Services, cancellationToken);
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The warning for a deferred service whose landed launch profile declares an
    /// <c>applicationUrl</c> that the AppHost did not mirror as an endpoint, or
    /// <see langword="null"/> when there is nothing to say. Exposed for tests.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the whole of what deferral costs, stated at the only moment it can be stated
    /// accurately. Endpoints are synthesised from <c>applicationUrl</c> while composing, when the
    /// repository is not on disk; nothing re-runs that step, so a deferred resource carries only
    /// the endpoints the AppHost declared. If the project turns out to declare none either, that
    /// agrees with the warm path exactly and there is nothing wrong — which is the case a
    /// run-to-completion worker is in, and the case the earlier pre-flight check could not tell
    /// apart from a mistake.
    /// </para>
    /// <para>
    /// When the profile <em>does</em> declare one, the consequence is worth a sentence: the project
    /// still binds that URL itself, because <c>dotnet run</c> applies its own launch profile, but
    /// Aspire allocated no endpoint for it — so the port is not reassigned away from a collision,
    /// no proxy fronts it, service discovery cannot resolve it and the dashboard does not link it.
    /// The message can quote the real URL, which is why it is worth waiting for the checkout to
    /// produce it rather than guessing at composition time.
    /// </para>
    /// </remarks>
    public static string? LaunchProfileEndpointWarning(
        string serviceName, LandedLaunchProfile profile, IResource resource)
    {
        if (resource.Annotations.OfType<EndpointAnnotation>().Any())
        {
            return null;
        }

        var declared = profile.ApplicationUrls;
        if (declared.Count == 0)
        {
            return null;
        }

        return $"Service '{serviceName}' was started from a checkout cloned during this run, and its launch " +
               $"profile declares applicationUrl '{string.Join(", ", declared)}' — but a project's endpoints are " +
               "read while the AppHost composes, before the repository was on disk, so Aspire allocated none. " +
               "The project will bind that URL itself and run, but the port is outside Aspire's management: it " +
               "is not moved off a collision, nothing proxies it, service discovery cannot resolve this service " +
               $"and the dashboard will not link it. Declare it in the AppHost — builder.AddService(\"{serviceName}\")" +
               ".WithHttpEndpoint() — which also takes effect on every later run, where the checkout is warm and " +
               "the endpoint comes from the profile as usual. This is reported after the clone rather than " +
               "refused before it, so a service that has no endpoints on either path costs you nothing.";
    }

    /// <summary>
    /// Puts the landed launch profile's <c>environmentVariables</c> back onto the resource, which
    /// Aspire would have applied during composition had the repository been on disk.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is not a nicety. <c>Host.CreateDefaultBuilder</c> takes the environment name from
    /// <c>DOTNET_ENVIRONMENT</c>, which for most repositories is set by the launch profile and
    /// nowhere else — so without this a deferred service runs as <c>Production</c> while every warm
    /// run of the same service runs as <c>Development</c>. That is the kind of divergence that
    /// either crashes immediately on a missing <c>appsettings.Development.json</c> or, worse, does
    /// not.
    /// </para>
    /// <para>
    /// Added last and only where the key is absent, so anything the AppHost set — <c>WithEnvironment</c>,
    /// <c>WithReference</c>, a resolved connection string — wins over the profile, which is the
    /// precedence Aspire itself applies. Command-line arguments from <c>commandLineArgs</c> are not
    /// restored here: unlike environment, they are positional, so appending them after whatever the
    /// AppHost added can change their meaning — and they need no restoring anyway, because Aspire
    /// reads the profile again when it builds the executable, after the clone.
    /// </para>
    /// <para>
    /// Values are expanded and <c>DOTNET_LAUNCH_PROFILE</c> is set, both of which
    /// <c>WithProjectDefaults</c> does on the warm path. Skipping either would leave a difference
    /// that only shows up on the first run: a profile value such as <c>%USERPROFILE%\certs</c> would
    /// reach the process literally now and expanded on every run after.
    /// </para>
    /// </remarks>
    private static void RestoreLaunchProfileEnvironment(
        IResource resource, LandedLaunchProfile profile, ILogger logger)
    {
        // Keyed off the profile rather than off its variable count: a profile with no
        // environmentVariables still names itself in DOTNET_LAUNCH_PROFILE on the warm path.
        if (profile.Name is not { } profileName)
        {
            return;
        }

        resource.Annotations.Add(new EnvironmentCallbackAnnotation(context =>
        {
            // Written before the profile's own variables, which is the order Aspire writes them
            // in: the selected profile's name wins over a DOTNET_LAUNCH_PROFILE the profile
            // happens to declare for itself.
            if (!context.EnvironmentVariables.ContainsKey(LaunchProfileNameVariable))
            {
                context.EnvironmentVariables[LaunchProfileNameVariable] = profileName;
            }

            foreach (var variable in profile.EnvironmentVariables)
            {
                if (!context.EnvironmentVariables.ContainsKey(variable.Key))
                {
                    context.EnvironmentVariables[variable.Key] = Environment.ExpandEnvironmentVariables(variable.Value);
                }
            }
        }));

        // Counted from what the callback above sets, DOTNET_LAUNCH_PROFILE included — a profile
        // with no environmentVariables of its own still restores that one, and a log saying
        // nothing was applied would send a developer looking for a bug that isn't there. A profile
        // that declares the name variable itself is not counted twice, because the callback drops
        // its value for the profile name.
        var applied = new List<string>(profile.EnvironmentVariables.Count + 1) { LaunchProfileNameVariable };
        applied.AddRange(profile.EnvironmentVariables.Keys.Where(
            key => !string.Equals(key, LaunchProfileNameVariable, StringComparison.Ordinal)));

        logger.LogInformation(
            "Applied {Count} environment variable(s) from the checkout's launch profile '{Profile}' ({Names}), "
            + "which Aspire could not read while composing the AppHost.",
            applied.Count,
            profileName,
            string.Join(", ", applied));
    }

    /// <summary>
    /// The per-service checkout-then-start tasks <see cref="StartAll"/> launched. Exposed for tests
    /// — in a real run nothing awaits these, which is the whole point of them.
    /// </summary>
    public IReadOnlyList<Task> StartTasks
    {
        get
        {
            lock (_gate)
            {
                return [.. _startTasks];
            }
        }
    }

    private readonly List<Task> _startTasks = [];

    private void StartAll(IServiceProvider services, CancellationToken cancellationToken)
    {
        Deferred[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _deferred];
        }

        foreach (var deferred in snapshot)
        {
            // Deliberately not awaited: BeforeStartEvent is awaited by host startup, and waiting
            // for the checkout here would put the block back exactly where this class exists to
            // take it out of. StartDeferredAsync never throws, so nothing is left unobserved.
            //
            // Task.Run rather than a bare call because the first thing past the await is
            // GetRepoRoot, which blocks the calling thread on the clone.
            var task = Task.Run(
                () => StartDeferredAsync(deferred, services, cancellationToken), CancellationToken.None);

            lock (_gate)
            {
                _startTasks.Add(task);
            }
        }
    }

    /// <summary>
    /// Waits for one service's checkout and then starts its resource, reporting failure as resource
    /// state and resource logs rather than as an exception.
    /// </summary>
    /// <remarks>
    /// Nothing is left for this to throw at. It runs on a task nobody awaits, long after the call
    /// that would have propagated a configuration error has returned — which is the point: a
    /// checkout that fails now costs one service, not the whole AppHost. The developer's route to
    /// it is the dashboard, so that is where it is put.
    /// </remarks>
    private static async Task StartDeferredAsync(
        Deferred deferred, IServiceProvider services, CancellationToken cancellationToken)
    {
        // Declared out here so the failure report can tell what this failure is not about.
        var started = new List<IResource>();

        try
        {
            var notifications = services.GetRequiredService<ResourceNotificationService>();
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(deferred.Resource);

            // BeforeStartEvent carries whatever token was handed to RunAsync, and the token an
            // AppHost actually supplies is none: the template ends in Run(), which is
            // RunAsync().Wait() with the default. Ctrl-C would leave this task waiting on a
            // NotStarted that is no longer coming, and if the clone landed mid-teardown it would go
            // on to start a resource on a stopping host and report the resulting failure as the
            // service's own. ApplicationStopping is the signal that does fire; the event's token is
            // kept alongside it because a host that was given a real one means it.
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);

            var stoppingToken = shutdown.Token;

            // This runs from BeforeStartEvent, so the resources are not in the notification service
            // yet — a state published now would be overwritten by the NotStarted that DCP publishes
            // when it withholds each explicit-start executable, and the start commands below would
            // have nothing to act on. Waiting for that NotStarted is the whole synchronisation this
            // needs, and it depends on nothing outside this service's own resources.
            foreach (var withheld in deferred.AllResources)
            {
                await notifications.WaitForResourceAsync(
                    withheld.Name, KnownResourceStates.NotStarted, stoppingToken)
                    .ConfigureAwait(false);
            }

            await PublishCheckingOutAsync(notifications, deferred.Resource).ConfigureAwait(false);

            logger.LogInformation(
                "Resolving checkout of {Repository} into {RepoRoot} before starting.",
                GitUrl.Redact(deferred.Metadata.Repository),
                deferred.RepoRoot);

            // Claimed here, on this thread, rather than inside the reporting task: the checkout
            // below may be the thing that starts the clone — a service the prefetch never enumerated
            // is resolved on this call — and it can only report to a stream that already exists by
            // the time it runs.
            var progress = deferred.Prefetch.WatchCheckout(deferred.ServiceName);

            // git's own account of the clone, mirrored onto this resource while it runs. On a task
            // of its own because the call below blocks this one for as long as the clone takes,
            // which is exactly the stretch there is something to report.
            var reporting = Task.Run(
                () => ReportCloneProgressAsync(deferred, notifications, logger, progress, stoppingToken),
                CancellationToken.None);

            string repoRoot;
            try
            {
                repoRoot = deferred.Prefetch.GetRepoRoot(
                    deferred.ServiceName,
                    deferred.Metadata,
                    deferred.Config,
                    deferred.AppHostDirectory,
                    deferred.GitClient);
            }
            finally
            {
                // Awaited on the failure path too, and before the failure is reported: a progress
                // line still in flight would otherwise be published over the state that says this
                // service failed. It never throws, so it cannot displace the exception it runs under.
                await reporting.ConfigureAwait(false);
            }

            // The absolute .csproj path was frozen into the DCP executable spec before this ran, so
            // a checkout that landed anywhere else cannot be started — the resource would run with
            // the wrong working directory and --project argument. It should not be reachable:
            // ManagedRepoRoot is the same pure function both sides call, and deferral is refused
            // for a 'path' override. Checked anyway, because being wrong about it is silent.
            if (!string.Equals(repoRoot, deferred.RepoRoot, StringComparison.Ordinal))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{deferred.ServiceName}': the checkout resolved to '{repoRoot}', but the resource " +
                    $"was registered against '{deferred.RepoRoot}' before the AppHost started and that path " +
                    "cannot be changed afterwards.");
            }

            // The checkout is complete and nothing has judged it yet, which is where a prepare step
            // belongs on this path exactly as it does on the eager one. Before OnCheckoutLanded,
            // which is where a kind's ValidateCheckout runs and where the dotnet kind reads its
            // landed launch profile — and therefore before the held-back helpers start too: an
            // installer resource core starts ahead of the app reads a package.json a prepare step is
            // entitled to have generated.
            //
            // This is where the step lands better than the console could. The task already holds the
            // service's logger and publishes its resource state, so a country-sized import reads as
            // an initialization phase in the dashboard rather than as an apparent hang, and a
            // failure becomes this one service's state instead of an exception out of composition.
            if (deferred.PrepareStep is { } step)
            {
                await PublishStateAsync(notifications, deferred.Resource, PreparingState).ConfigureAwait(false);

                CheckoutPreparation.Run(
                    deferred.ServiceName,
                    step,
                    repoRoot,
                    deferred.AppHostDirectory,
                    // A 'path' override never defers (DeferredCheckout.ShouldDefer refuses one), so
                    // a checkout reaching this point is always one this package manages.
                    managedCheckout: true,
                    deferred.GitClient,
                    deferred.PrepareRunner,
                    new LoggerPrepareOutputSink(logger));
            }

            // Whatever this kind could only settle against a real working tree: the dotnet kind's
            // launch profile, a kind's DeferredLocalResource.ValidateCheckout.
            deferred.OnCheckoutLanded(deferred.Resource, repoRoot, logger);

            logger.LogInformation("Checkout ready at {RepoRoot}. Starting.", repoRoot);

            var commands = services.GetRequiredService<ResourceCommandService>();

            // Held-back helpers first, then the service. They are not merely ordered but dependent:
            // Aspire.Hosting.JavaScript's app already carries a WaitFor on its installer, so starting
            // the app while the installer is still withheld would leave it waiting on a resource
            // nothing was ever going to create. Starting the installer first makes that wait resolve
            // the way it does on a warm run; DCP does the sequencing from there.
            foreach (var withheld in deferred.AllResources)
            {
                var result = await commands
                    .ExecuteCommandAsync(withheld, KnownResourceCommands.StartCommand, stoppingToken)
                    .ConfigureAwait(false);

                if (!result.Success && !result.Canceled)
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{deferred.ServiceName}': the checkout completed but starting resource " +
                        $"'{withheld.Name}' failed. " + (result.Message ?? "No further detail was reported."));
                }

                started.Add(withheld);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down. This is reached from the wait for NotStarted or from the
            // start command, never from the clone in between: LocalCheckoutPrefetch takes no
            // cancellation token, so a shutdown that arrives mid-clone is not noticed until that
            // clone has finished on its own. Either way there is nothing left to start and nobody
            // to tell.
        }
        catch (Exception ex)
        {
            await ReportFailureAsync(deferred, services, ex, started).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Mirrors the running clone onto the resource: every line git writes goes to the resource's
    /// logs as it arrives, and the phase it names goes to the State column. Returns when the clone's
    /// progress stream ends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A clone the developer cannot see is the state deferral leaves them in: the AppHost reaches
    /// the dashboard, and the service sits in "Checking out" for however long the repository takes.
    /// git already reports what it is doing in enough detail to answer "is this moving?" — phase,
    /// percentage, bytes and a transfer rate — so this carries that through rather than inventing a
    /// progress model on top of it.
    /// </para>
    /// <para>
    /// Silence is not a stall. git suppresses progress for work that finishes inside its own delay
    /// threshold, and a clone from a local path reports nothing at all, so a small repository can go
    /// from "Checking out" to started without a single line — which is why nothing here times out or
    /// reports an absence. The same goes for a checkout that was already on disk: there was no clone
    /// to report, and the stream ends saying so.
    /// </para>
    /// <para>
    /// Ends when the stream does, which <see cref="LocalCheckoutPrefetch.GetRepoRoot"/> guarantees
    /// happens — so this cannot outlive the checkout it is reporting on, whichever path resolved it.
    /// </para>
    /// </remarks>
    private static async Task ReportCloneProgressAsync(
        Deferred deferred,
        ResourceNotificationService notifications,
        ILogger logger,
        CheckoutProgress progress,
        CancellationToken cancellationToken)
    {
        var published = false;
        var publishedAt = 0L;
        string? publishedPhase = null;

        try
        {
            await foreach (var line in progress.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                // Verbatim, progress lines included: the logs are where the developer goes for what
                // actually happened, and git's own stream is a better record of it than any summary
                // of ours. The State column is where the summarising happens.
                logger.LogInformation("{GitProgress}", line);

                if (!GitProgressLine.TryParse(line, out var parsed))
                {
                    continue;
                }

                // Coalesced, because "Receiving objects" emits a line per percentage point and every
                // publish is a round trip to the dashboard. A new phase is always published — it is
                // the part a developer reads — and within one phase a second apart is enough to look
                // alive.
                var now = Stopwatch.GetTimestamp();
                if (published
                    && string.Equals(parsed.Phase, publishedPhase, StringComparison.Ordinal)
                    && Stopwatch.GetElapsedTime(publishedAt, now) < ProgressPublishInterval)
                {
                    continue;
                }

                published = true;
                publishedAt = now;
                publishedPhase = parsed.Phase;

                await notifications.PublishUpdateAsync(deferred.Resource, snapshot => snapshot with
                {
                    State = new ResourceStateSnapshot(parsed.StateText, KnownResourceStateStyles.Info),
                }).ConfigureAwait(false);
            }

            if (published)
            {
                // The transfer is over; the checkout is not. What follows is the reconciliation onto
                // the configured ref, which reports nothing, and leaving "Updating files 100%" up for
                // it would read as a clone that had stopped moving.
                await PublishCheckingOutAsync(notifications, deferred.Resource).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The host is shutting down. The clone goes on — nothing takes a cancellation token as
            // far as git — but there is no longer anywhere to report it to.
        }
        catch (Exception ex)
        {
            // Reporting progress must never be the thing that fails a checkout. The caller awaits
            // this from a finally, so an exception escaping here would replace the exception the
            // checkout itself failed with — and the checkout is the part that matters. A
            // notification service being torn down under us costs the progress display and nothing
            // else.
            try
            {
                logger.LogDebug(ex, "Reporting checkout progress stopped early: {Message}", ex.Message);
            }
            catch (Exception)
            {
                // The logger is one of the things that can be torn down under us, so noting the
                // problem must not become the problem.
            }
        }
    }

    private static Task PublishCheckingOutAsync(ResourceNotificationService notifications, IResource resource) =>
        PublishStateAsync(notifications, resource, CheckingOutState);

    private static Task PublishStateAsync(
        ResourceNotificationService notifications, IResource resource, string state) =>
        notifications.PublishUpdateAsync(resource, snapshot => snapshot with
        {
            State = new ResourceStateSnapshot(state, KnownResourceStateStyles.Info),
        });

    /// <summary>
    /// The deferred path's <see cref="IPrepareOutputSink"/>: every line a <c>prepare</c> step
    /// reports reaches the service's own resource log as it arrives, and so the dashboard.
    /// </summary>
    private sealed class LoggerPrepareOutputSink(ILogger logger) : IPrepareOutputSink
    {
        public void Report(string line) => logger.LogInformation("{PrepareOutput}", line);
    }

    /// <param name="started">
    /// The resources whose start command already succeeded, which this failure is therefore not
    /// about.
    /// </param>
    private static async Task ReportFailureAsync(
        Deferred deferred, IServiceProvider services, Exception exception, IReadOnlyList<IResource> started)
    {
        try
        {
            var notifications = services.GetRequiredService<ResourceNotificationService>();
            var logger = services.GetRequiredService<ResourceLoggerService>().GetLogger(deferred.Resource);

            logger.LogError(
                exception,
                "Service '{ServiceName}': its checkout was deferred past startup and did not complete, so the " +
                "service was never started. {Message}",
                deferred.ServiceName,
                exception.Message);

            // Every resource withheld for this service, not just the service's own: a held-back
            // helper left sitting in NotStarted reads as "still waiting" rather than as the casualty
            // of a clone that is never going to land.
            //
            // Except the ones that did start. Helpers are started ahead of the service, so a failure
            // between the two — the service's own start command reporting failure — is reached with
            // the installer already running or Finished. Painting that red would report a successful
            // npm install as the thing that failed, and republishing over a terminal state churns it
            // under anything still watching. Keyed on what was actually started rather than on the
            // state text, because the service is in this package's own "Checking out" by then and
            // has to be painted.
            foreach (var withheld in deferred.AllResources.Except(started))
            {
                await notifications.PublishUpdateAsync(withheld, snapshot => snapshot with
                {
                    State = new ResourceStateSnapshot(
                        KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error),
                }).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Reporting the failure must not become a second, worse failure on a task nobody awaits.
            // The host may already be tearing its logging and notification services down — which is
            // one of the ways the checkout got interrupted in the first place.
        }
    }
}
