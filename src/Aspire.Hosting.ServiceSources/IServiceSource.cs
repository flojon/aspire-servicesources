using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources;

internal interface IServiceSource
{
    /// <summary>
    /// The <see cref="ServiceDeveloperConfig"/> fields this source reads. Used by
    /// <see cref="ServiceDeveloperConfigValidator"/> to reject fields left over from
    /// switching sources or set by typo.
    /// </summary>
    IReadOnlySet<string> RelevantFields { get; }

    /// <summary>
    /// Adds the real resource Aspire will run for this service and returns a handle to it. The
    /// resource must be registered in <c>builder.Resources</c> — an unregistered one gets no DCP
    /// Service, which breaks any container consumer that references it (issue #58). The one
    /// exception is <see cref="Sources.UrlSource"/>; see its remarks.
    /// </summary>
    IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config);
}
