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
    /// for the one source that cannot comply as #72). The exceptions are <see cref="Sources.UrlSource"/>
    /// and <see cref="Sources.DisabledSource"/>, which has no resource at all to register; see their
    /// remarks.
    /// </summary>
    /// <param name="repositoryConfig">
    /// This service's group-level developer-config entry (#291), or <see langword="null"/> for the
    /// common, ungrouped case. Only <see cref="Sources.LocalProjectSource"/> reads it — the other
    /// four sources have no managed checkout for a group to share — but every implementation takes
    /// it, the same way every one already takes the whole of <paramref name="config"/> whether or not
    /// its own source block is the one populated.
    /// </param>
    IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null);

    /// <summary>
    /// The type every resource <see cref="Resolve"/> would build for <paramref name="definition"/> is,
    /// or derives from — or <see langword="null"/> when the entry cannot be switched to this source,
    /// or nothing is reachable through it.
    /// </summary>
    /// <remarks>
    /// Read by <c>Unwrap&lt;T&gt;(configure)</c> to tell a call no source could satisfy from one a
    /// source switch skipped. Answering too broadly only turns that throw into a warning.
    /// </remarks>
    Type? DeclaredResourceType(IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition) => null;
}
