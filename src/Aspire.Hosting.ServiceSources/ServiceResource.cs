using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// A reference-only facade over the real project resource that <c>AddService()</c> resolved
/// (a locally checked-out or managed-clone project added via Aspire's own
/// <c>AddProject(name, path)</c>). This type exists so consumers get an
/// <see cref="IResourceWithServiceDiscovery"/> handle to pass to
/// <c>WithReference(...)</c> and to call <c>GetEndpoint(...)</c> on, without this package
/// needing to expose the underlying <c>ProjectResource</c> type directly.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="IResourceBuilder{T}"/> returned by <c>AddService()</c> wraps this facade.
/// It is deliberately <b>never added to <c>builder.Resources</c></b> — the real project
/// resource added via <c>AddProject</c> is what actually participates in Aspire's resource
/// model, gets built, and runs. The facade only carries copies of the real resource's
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

    internal static IResourceBuilder<IResourceWithServiceDiscovery> CreateFacade(
        IDistributedApplicationBuilder builder, string name, IResourceBuilder<ProjectResource> realResource)
    {
        var facade = builder.CreateResourceBuilder(new ServiceResource(name));

        foreach (var endpoint in realResource.Resource.Annotations.OfType<EndpointAnnotation>())
        {
            facade.Resource.Annotations.Add(endpoint);
        }

        return facade;
    }
}
