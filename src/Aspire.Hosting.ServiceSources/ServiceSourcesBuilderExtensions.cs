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
        ["local"] = new LocalProjectSource(new LibGit2SharpGitClient()),
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
