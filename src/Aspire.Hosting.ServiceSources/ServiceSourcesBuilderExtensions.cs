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
    /// An <see cref="IResourceBuilder{T}"/> wrapping a <see cref="ServiceResource"/> facade —
    /// see its remarks for the important caveat that this builder is reference-only.
    /// </returns>
    /// <remarks>
    /// The returned builder is intended to be passed to a consumer's <c>WithReference(...)</c>
    /// call, or used to call <c>GetEndpoint(...)</c> directly. It is <b>not</b> intended for
    /// applying further resource configuration: calls such as <c>.WithEnvironment(...)</c> or
    /// <c>.WithHttpEndpoint(...)</c> on the returned builder compile but silently have no
    /// effect, because the facade resource is deliberately never registered in Aspire's
    /// resource model (the real, underlying project resource is what runs). See
    /// <see cref="ServiceResource"/> for the full explanation.
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
}
