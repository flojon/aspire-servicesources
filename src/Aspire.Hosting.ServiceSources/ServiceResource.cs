using System.Net.Sockets;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// A reference-only facade over the real resource that <c>AddService()</c> resolved (a local
/// project, a <c>kubectl port-forward</c> executable, etc.). Gives consumers an
/// <see cref="IResourceWithServiceDiscovery"/> handle for <c>WithReference(...)</c> /
/// <c>GetEndpoint(...)</c> without exposing the underlying resource type.
/// </summary>
/// <remarks>
/// Deliberately <b>never added to <c>builder.Resources</c></b> — the real resource is what
/// Aspire actually builds and runs; the facade just carries copies of its
/// <c>EndpointAnnotation</c>s. Because it's unregistered, further builder-extension calls on
/// it (<c>.WithEnvironment(...)</c>, <c>.WithHttpEndpoint(...)</c>, etc.) compile but
/// <b>silently no-op</b>. Configure the underlying resource directly, or via
/// <c>servicesources.yaml</c>/<c>servicesources.local.json</c>.
/// </remarks>
public sealed class ServiceResource : Resource, IResourceWithServiceDiscovery
{
    internal ServiceResource(string name) : base(name)
    {
    }

    internal static IResourceBuilder<IResourceWithServiceDiscovery> CreateEmptyFacade(
        IDistributedApplicationBuilder builder, string name) =>
        builder.CreateResourceBuilder(new ServiceResource(name));

    internal static IResourceBuilder<IResourceWithServiceDiscovery> CreateFacade<TResource>(
        IDistributedApplicationBuilder builder, string name, IResourceBuilder<TResource> realResource)
        where TResource : IResource
    {
        var facade = CreateEmptyFacade(builder, name);
        CopyEndpointAnnotations(facade, realResource);
        return facade;
    }

    internal static void CopyEndpointAnnotations<TResource>(
        IResourceBuilder<IResourceWithServiceDiscovery> facade, IResourceBuilder<TResource> realResource)
        where TResource : IResource
    {
        foreach (var endpoint in realResource.Resource.Annotations.OfType<EndpointAnnotation>())
        {
            facade.Resource.Annotations.Add(endpoint);
        }
    }

    /// <summary>
    /// Creates a facade whose single endpoint resolves to a fixed, already-known
    /// <paramref name="uri"/>. Used by the <c>"url"</c> source, which has no underlying
    /// resource for DCP to allocate an endpoint for, so the <see cref="AllocatedEndpoint"/> is
    /// set eagerly here instead.
    /// </summary>
    internal static IResourceBuilder<IResourceWithServiceDiscovery> CreateFacadeForUri(
        IDistributedApplicationBuilder builder, string name, Uri uri)
    {
        var facade = builder.CreateResourceBuilder(new ServiceResource(name));

        var endpoint = new EndpointAnnotation(
            ProtocolType.Tcp, uriScheme: uri.Scheme, name: uri.Scheme, transport: "http", port: uri.Port, targetPort: uri.Port)
        {
            TargetHost = uri.Host,
            IsProxied = false,
        };
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(
            endpoint, uri.Host, uri.Port, EndpointBindingMode.SingleAddress, targetPortExpression: null);

        facade.Resource.Annotations.Add(endpoint);

        return facade;
    }
}
