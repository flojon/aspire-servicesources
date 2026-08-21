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

    IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config);
}
