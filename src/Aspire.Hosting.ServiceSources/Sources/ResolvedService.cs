using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Sources;

internal static class ResolvedService
{
    /// <summary>
    /// Tags the real resource a source created with the service name and source that produced it,
    /// and widens it to the single type <c>AddService()</c> returns.
    /// </summary>
    /// <remarks>
    /// The return type stays <c>IResourceBuilder&lt;IResourceWithServiceDiscovery&gt;</c> rather
    /// than a package-defined interface for a concrete reason: Aspire's TypeScript code generator
    /// emits nothing at all for an exported method returning a custom interface, so a narrower type
    /// would silently drop <c>addService</c> from the generated SDK and break the TypeScript AppHost
    /// (#51). Verified against both released 13.5.1 and the 13.6.0 build carrying the
    /// microsoft/aspire#19577 codegen fix.
    /// </remarks>
    public static IResourceBuilder<IResourceWithServiceDiscovery> Tag<TResource>(
        IResourceBuilder<TResource> resource, string serviceName, string source)
        // `class` is what lets IResourceBuilder<T>'s covariance widen the result below.
        where TResource : class, IResourceWithServiceDiscovery
    {
        resource.WithAnnotation(new ServiceSourceAnnotation(serviceName, source));
        return resource;
    }
}
