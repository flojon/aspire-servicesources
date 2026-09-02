using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources;

internal interface IServiceSource
{
    /// <summary>
    /// Adds the real resource Aspire will run for this service and returns a handle to it. The
    /// resource must be registered in <c>builder.Resources</c> — an unregistered one gets no DCP
    /// Service, which breaks any container consumer that references it (reported as #58, still open
    /// for the one source that cannot comply as #72). The one
    /// exception is <see cref="Sources.UrlSource"/>; see its remarks.
    /// </summary>
    IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config);
}
