using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.PortAllocation;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources;

public static class ServiceSourcesBuilderExtensions
{
    private static readonly Dictionary<string, IServiceSource> Sources = new()
    {
        ["local"] = new LocalProjectSource(new GitCliClient()),
        ["kubernetes"] = new KubernetesSource(new SocketPortAllocator()),
        ["url"] = new UrlSource(),
        ["container"] = new ContainerSource(),
    };

    /// <summary>
    /// Resolves service <paramref name="name"/> to its real resource and adds it to
    /// <paramref name="builder"/>, according to the service's configured source: a local
    /// project — either a developer-managed checkout (<c>path</c> in
    /// <c>servicesources.local.json</c>) or a package-managed git clone under
    /// <c>.servicesources/checkouts/&lt;serviceName&gt;</c> beneath the AppHost directory —
    /// added via Aspire's own <c>AddProject(name, path)</c> without ever
    /// touching this AppHost's own <c>.csproj</c>/<c>.sln</c> (the <c>"local"</c> source); or a
    /// <c>kubectl port-forward</c> process against an already-running service in a Kubernetes
    /// dev cluster, added via Aspire's own <c>AddExecutable(...)</c> (the <c>"kubernetes"</c>
    /// source); or a fixed, already-known URL — e.g. a Kubernetes ingress or any other reachable
    /// HTTP endpoint — with no underlying resource for Aspire to run (the <c>"url"</c> source);
    /// or a published container image run locally via Aspire's own <c>AddContainer(...)</c>,
    /// with image pull and lifecycle managed entirely by Aspire's own container-runtime
    /// integration (the <c>"container"</c> source).
    /// </summary>
    /// <returns>
    /// An <see cref="IResourceBuilder{T}"/> over the <b>real</b> resource Aspire runs. Pass it to a
    /// consumer's <c>WithReference(...)</c>, name its endpoint with
    /// <see cref="ServiceEndpointExtensions.GetServiceEndpoint"/> (or <c>GetEndpoint(...)</c>, which
    /// ties the consumer to one source's endpoint naming), or apply this AppHost's own configuration
    /// with <see cref="ServiceConfigurationExtensions.Configure{T}"/> and
    /// <see cref="ServiceConfigurationExtensions.As{T}"/>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The resource is registered in Aspire's model, so configuration applied through the returned
    /// builder reaches the process that actually runs, and a container consumer's
    /// <c>WithReference(...)</c> resolves. Which configuration applies depends on the resolved
    /// source: the <c>"url"</c> and <c>"kubernetes"</c> sources run out of band — one is a fixed
    /// remote URL, the other a <c>kubectl port-forward</c> in front of something already running —
    /// so <see cref="ServiceConfigurationExtensions.Configure{T}"/> skips with a warning rather than
    /// applying it. Wait ordering survives for <c>"kubernetes"</c>, whose port-forward is a real
    /// local process to order against; <c>"url"</c> registers no resource at all, so nothing
    /// applies to it.
    /// </para>
    /// <para>
    /// The bare <c>IResourceBuilder&lt;IResourceWithServiceDiscovery&gt;</c> return type is load
    /// bearing — Aspire's TypeScript code generator emits nothing for an exported method returning a
    /// custom interface, so narrowing it would drop <c>addService</c> from the generated SDK
    /// entirely and break the TypeScript AppHost.
    /// </para>
    /// </remarks>
    [AspireExport]
    public static IResourceBuilder<IResourceWithServiceDiscovery> AddService(
        this IDistributedApplicationBuilder builder, [ResourceName] string name)
    {
        var (metadata, developerConfig) = ServiceSourcesConfigCache.ResolveService(builder, name);

        if (!Sources.TryGetValue(developerConfig.Source, out var source))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{name}' has source '{developerConfig.Source}', which is not implemented yet.");
        }

        ServiceDeveloperConfigValidator.Validate(name, developerConfig.Source, source.RelevantFields, developerConfig);

        return source.Resolve(builder, name, metadata, developerConfig);
    }

    /// <summary>
    /// Opts this AppHost into deferring a <c>"local"</c> service's <em>first</em> checkout past
    /// startup: a service whose package-managed clone does not exist yet is registered stopped,
    /// cloned while the AppHost runs, and started when its checkout lands — so the dashboard comes
    /// up immediately, checkout progress and failure show as resource state, and one failed clone
    /// costs one service rather than the whole AppHost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must be called before the first <see cref="AddService"/>, which is where the decision is
    /// made. Nothing else about the run changes: the clones start at exactly the same moment they
    /// always did, and a service whose checkout already exists — every service on every run after
    /// the first — resolves eagerly, with full launch-profile fidelity, exactly as it does without
    /// this call. Services with a <c>path</c> override are never deferred either; that directory is
    /// the developer's own and there is nothing to clone. Neither is anything outside run mode:
    /// <c>aspire publish</c> and manifest generation clone first as they always have, because a
    /// manifest written from a repository that is not on disk would describe a project without its
    /// endpoints or its profile environment.
    /// </para>
    /// <para>
    /// Applies to the <c>"local"</c> kinds that own a managed checkout — <c>dotnet</c>, <c>java</c>
    /// and <c>javascript</c>. The satellite kinds pay none of the cost below: neither has a launch
    /// profile, and both take their endpoints from the committed catalog, so a deferred one is
    /// identical to a warm one and only their post-clone checks move. <c>url</c>, <c>kubernetes</c>
    /// and <c>container</c> clone nothing, so there is nothing to defer.
    /// </para>
    /// <para>
    /// A deferred <c>dotnet</c> service's launch profile environment is put back once the clone
    /// lands, and only where the AppHost has not already set the same key — expanded, and alongside
    /// <c>DOTNET_LAUNCH_PROFILE</c>, exactly as a warm run applies it.
    /// </para>
    /// <para>
    /// A deferred <c>dotnet</c> service should declare its own endpoints in the AppHost, because a
    /// project's endpoints come from its launch profile and Aspire reads that while composing —
    /// before the repository is on disk:
    /// </para>
    /// <code lang="csharp">
    /// builder.UseDeferredCheckout();
    ///
    /// var orders = builder.AddService("orders").WithHttpEndpoint();
    /// </code>
    /// <para>
    /// That line is correct on a warm checkout too — <c>WithHttpEndpoint</c> updates an endpoint of
    /// the same name using its non-null arguments only, and it has none — so there is one call, not
    /// one per path. A service that declares none still runs: once the checkout has landed its real
    /// launch profile is read, and only a profile that declares an <c>applicationUrl</c> the AppHost
    /// did not mirror produces a warning naming the service and the URL. A service with no
    /// <c>applicationUrl</c> on either path — a run-to-completion worker — costs nothing and is
    /// never reported. See <c>DeferredCheckout.LaunchProfileEndpointWarning</c>.
    /// </para>
    /// <para>
    /// Off by default: a service that used to be running by the time <c>Build()</c> returned is
    /// started after it instead, which is visible to anything in the AppHost that assumed otherwise.
    /// </para>
    /// </remarks>
    [AspireExportIgnore]
    public static IDistributedApplicationBuilder UseDeferredCheckout(this IDistributedApplicationBuilder builder)
    {
        DeferredCheckout.For(builder).Enable();
        return builder;
    }

    /// <summary>
    /// Registers <paramref name="handler"/> as the resolver for local-sourced services whose
    /// <c>servicesources.yaml</c> entry declares <c>kind: &lt;paramref name="kind"/&gt;</c>.
    /// Called by a satellite package's own registration method (e.g. a hypothetical
    /// <c>UseJavaScript()</c>), not typically called directly by an AppHost author.
    /// </summary>
    [AspireExportIgnore]
    public static IDistributedApplicationBuilder AddLocalKind(
        this IDistributedApplicationBuilder builder, string kind, ILocalResourceKind handler)
    {
        LocalKindRegistry.For(builder).Register(kind, handler);
        return builder;
    }
}
