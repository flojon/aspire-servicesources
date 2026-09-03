using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class LocalProjectSource(IGitClient gitClient) : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        // Before any network work: a machine without a usable git can't clone anything, and
        // finding that out once here beats finding it out as an identical clone failure on every
        // service the catalog holds. Cheap after the first call, which is why it sits on the hot
        // path rather than behind a one-shot flag of its own.
        gitClient.EnsureAvailable();

        var isDotnetKind = string.Equals(metadata.Kind, LocalKinds.Dotnet, StringComparison.Ordinal);

        // Settle everything configuration alone can settle before paying for a checkout. Looking
        // the kind up is a dictionary probe against registry state; the handler's own Validate
        // only reads the kind config. Neither needs a working tree, and running them after the
        // clone would make a typo'd kind — or a kind nobody registered — cost a cold
        // clone of this repository before saying so.
        //
        // Only the first "local" AddService gets that for free across the board: the prefetch below
        // starts the speculative clones at once, so once any service has been resolved those clones
        // are already in flight and this check no longer runs ahead of them.
        var handler = isDotnetKind ? null : ResolveKindHandler(builder, serviceName, metadata);
        handler?.Validate(serviceName, metadata.KindConfig);

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
                ? deferred.Register(builder, serviceName, metadata, config, prefetch, gitClient)
                : SupportsDeferredKind(serviceName, metadata, handler!)
                    ? deferred.RegisterKind(
                        builder, serviceName, metadata, config, prefetch, gitClient,
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
                    ?? HandlerFailedMessage(serviceName, metadata.Kind),
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
        $"{nameof(ILocalResourceKind)}.{nameof(ILocalResourceKind.Validate)} instead, which runs first.";

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
    internal static string ResolveProjectFile(string serviceName, string repoRoot, string project)
    {
        var projectPath = Path.Combine(repoRoot, project);

        if (!File.Exists(projectPath))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': project file '{project}' was not found under '{repoRoot}'.");
        }

        return projectPath;
    }
}
