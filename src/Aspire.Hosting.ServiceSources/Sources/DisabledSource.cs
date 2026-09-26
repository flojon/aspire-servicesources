using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Resolves a service a developer has deliberately turned off. <c>AddService</c> still hands back a
/// valid <see cref="ServiceResource"/> facade — so the AppHost's own code needs no change — but
/// nothing runs and nothing is reachable: no endpoint, no process, no container.
/// </summary>
/// <remarks>
/// <para>
/// Named <c>"disabled"</c> rather than a blank/absent <c>source</c>, which already means something
/// else — "not configured at all" (see <see cref="Config.ServiceSourcesConfigCache.ResolveService"/>,
/// whose <c>NotConfiguredError</c> that condition still reaches unchanged). <c>"disabled"</c> is a
/// distinct, deliberate, non-blank value: a developer who writes it has said something, rather than
/// left an entry unfinished.
/// </para>
/// <para>
/// Shares <see cref="UrlSource"/>'s shape: there is no real resource for Aspire to run, so the facade
/// is left unregistered via <see cref="ResolvedService.BridgeUnregistered"/>, exactly as
/// <c>"url"</c> is — and <see cref="UnregisteredServiceStartupGuard"/> drops a consumer's
/// <c>WaitFor</c>/<c>WaitForCompletion</c> on it the same way, so a service switched off does not
/// leave another resource waiting forever for it (#170's shape, generalized — see that guard's
/// remarks). Unlike <c>"url"</c>, no <see cref="EndpointAnnotation"/> is ever registered: a disabled
/// service has nothing to point anywhere, deliberately, so there is no host or port to hand a
/// consumer even for its own facade's sake. That also means the container-reference failure
/// <see cref="UrlSource"/>'s remarks describe (#58/#72) does not apply here — a container that
/// <c>WithReference</c>s a disabled service finds no endpoints to wire up and simply gets none.
/// </para>
/// <para>
/// Every <c>Configure</c> call — <c>WithEnvironment</c>, <c>WithReference</c>, <c>WithArgs</c>,
/// endpoints, commands — is skipped with a warning through the same
/// <see cref="Reachability.OutOfBandSources"/> gate <c>"url"</c> and <c>"kubernetes"</c> already use,
/// with no <see cref="WaitAnnotation"/> exception: unlike <c>"kubernetes"</c>'s <c>kubectl
/// port-forward</c>, there is no local process here worth ordering against.
/// </para>
/// <para>
/// Needs nothing from the catalog or the developer config beyond <c>"source": "disabled"</c> itself
/// — no block of its own, unlike every other source — since there is nothing to resolve: turning a
/// service off is the whole of what this does.
/// </para>
/// </remarks>
internal sealed class DisabledSource : IServiceSource
{
    public IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null)
    {
        var facade = new ServiceResource(serviceName);

        UnregisteredServiceStartupGuard.EnsureRegistered(builder);

        return ResolvedService.BridgeUnregistered(builder, facade, serviceName, "disabled");
    }
}
