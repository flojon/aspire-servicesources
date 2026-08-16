using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// A reference-only facade over the real resource that <c>AddService()</c> resolved — for
/// example, a local project added via Aspire's own <c>AddProject(name, path)</c> (the
/// <c>"local"</c> source), or a <c>kubectl port-forward</c> executable added via
/// <c>AddExecutable(...)</c> (the <c>"cluster"</c> source). This type exists so consumers get
/// an <see cref="IResourceWithServiceDiscovery"/> handle to pass to
/// <c>WithReference(...)</c> and to call <c>GetEndpoint(...)</c> on, without this package
/// needing to expose the underlying resource type directly.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="IResourceBuilder{T}"/> returned by <c>AddService()</c> wraps this facade.
/// It is deliberately <b>never added to <c>builder.Resources</c></b> — the real resource that
/// <c>AddService()</c> resolved is what actually participates in Aspire's resource model, gets
/// built, and runs. The facade only carries copies of the real resource's
/// <c>EndpointAnnotation</c>s so that <c>GetEndpoint(...)</c>/<c>WithReference(...)</c>
/// resolve identically to the real resource.
/// </para>
/// <para>
/// Because the facade is never registered, calling further Aspire builder-extension methods
/// on the returned builder — e.g. <c>.WithEnvironment(...)</c>, <c>.WithHttpEndpoint(...)</c>,
/// <c>.WithArgs(...)</c>, or any other resource-configuration extension — compiles
/// successfully but <b>silently has no effect</b>: the annotation is added to a resource
/// object that Aspire's DCP/dashboard machinery never sees. Configure the underlying project
/// (endpoints, environment variables, command-line args, etc.) in the AppHost that owns it, or
/// via the <c>servicesources.yaml</c>/<c>servicesources.local.json</c> configuration — not by
/// chaining calls onto the value returned from <c>AddService()</c>.
/// </para>
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
}
