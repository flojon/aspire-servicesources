using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources;

internal interface IServiceSource
{
    /// <summary>
    /// Adds the real resource Aspire will run for this service and returns a handle to it. The
    /// resource must be registered in <c>builder.Resources</c> — an unregistered one gets no DCP
    /// Service, which breaks any container consumer that references it. The exceptions are
    /// <see cref="Sources.UrlSource"/>, whose container consumers are rewritten to a literal instead,
    /// and <see cref="Sources.DisabledSource"/>, which has no resource at all to register; see their
    /// remarks.
    /// </summary>
    /// <param name="repositoryConfig">
    /// This service's group-level developer-config entry (#291), or <see langword="null"/> for the
    /// common, ungrouped case. Only <see cref="Sources.LocalProjectSource"/> reads it — the other
    /// five sources have no managed checkout for a group to share — but every implementation takes
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

    /// <summary>
    /// What this source needs before it resolves <paramref name="input"/>: the settings the picker must
    /// still ask for, and the first reason <see cref="Resolve"/> would refuse the entry as it stands.
    /// Runs the same check <see cref="Resolve"/> runs, with no side effect.
    /// </summary>
    SourceRequirements Requirements(SourceInput input);

    /// <summary>
    /// Whether an error message about another source may suggest switching to this one.
    /// </summary>
    bool IsSwitchRemedy { get; }
}
