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

    /// <summary>
    /// Whether <paramref name="definition"/> declares what this source needs, so the interactive
    /// picker can offer it. A source states its own precondition here, so a source added later needs
    /// no hard-coded list elsewhere.
    /// </summary>
    bool IsOfferedFor(ServiceDefinition definition) => true;

    /// <summary>
    /// Whether <paramref name="definition"/> with <paramref name="config"/> would resolve under this
    /// source, answered by the source's own validation and without resolving: no resource is built, no
    /// port allocated, nothing cloned. An error message uses it to offer a source switch only where
    /// following it would work.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>, so a source added later is simply not offered until it
    /// answers, rather than offered on a promise nothing checked; a test over the registered sources
    /// fails until a new one answers on purpose.
    /// <para>
    /// The stricter sibling of <see cref="IsOfferedFor"/>: that one reads the catalog entry alone and
    /// serves the picker, which can ask for values the entry lacks, so it may say yes where this says
    /// no. This one means "works as it stands", and a source answering yes here should also answer
    /// yes to <see cref="IsOfferedFor"/>. A test over the registered sources enforces that
    /// implication on sample entries and configs, so a source that drifts fails it.
    /// </para>
    /// </remarks>
    bool CanResolve(ServiceDefinition definition, ServiceDeveloperConfig config) => false;
}
