using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Resolves a service a developer has deliberately turned off. <c>AddService</c> still hands back a
/// valid <see cref="ServiceResource"/> facade, but nothing runs and nothing is reachable: no
/// endpoint, no process, no container.
/// </summary>
/// <remarks>
/// A distinct, deliberate value — not the same as a blank/absent <c>source</c>, which still means
/// "not configured at all" (see <see cref="Config.ServiceSourcesConfigCache.ResolveServiceOrNotConfigured"/>).
/// <para>
/// Shares <see cref="UrlSource"/>'s unregistered-facade shape (<see cref="ResolvedService.BridgeUnregistered"/>),
/// including the <see cref="UnregisteredServiceStartupGuard"/> wait-drop (#170) — but registers no
/// <see cref="EndpointAnnotation"/> at all, so the container-reference failure a
/// <c>"url"</c> service needs a rewrite for doesn't apply here: nothing for DCP to trip over.
/// </para>
/// <para>
/// Every <c>Configure</c> call is skipped with a warning via <see cref="Reachability.OutOfBandSources"/>,
/// with no <see cref="WaitAnnotation"/> exception (unlike <c>"kubernetes"</c>, there's no local
/// process to order against). Needs no catalog block or developer-config block of its own.
/// </para>
/// </remarks>
internal sealed class DisabledSource : ServiceSource<NoValues>
{
    public override bool IsSwitchRemedy => false;

    protected override IReadOnlyList<FollowUpField> FieldsFor(ServiceDefinition definition) => [];

    protected override NoValues Check(SourceInput input) => default;

    protected override IResourceBuilder<ServiceResource> Build(
        IDistributedApplicationBuilder builder, SourceInput input, NoValues checkedValues)
    {
        var facade = new ServiceResource(input.ServiceName);

        UnregisteredServiceStartupGuard.EnsureRegistered(builder);

        return ResolvedService.BridgeUnregistered(builder, facade, input.ServiceName, "disabled");
    }
}
