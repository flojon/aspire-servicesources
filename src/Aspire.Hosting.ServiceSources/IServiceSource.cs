using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;

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
    /// <param name="repositoryConfig">
    /// This service's group-level developer-config entry (#291), or <see langword="null"/> for the
    /// common, ungrouped case. Only <see cref="Sources.LocalProjectSource"/> reads it — the other
    /// three sources have no managed checkout for a group to share — but every implementation takes
    /// it, the same way every one already takes the whole of <paramref name="config"/> whether or not
    /// its own source block is the one populated.
    /// </param>
    IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null);
}
