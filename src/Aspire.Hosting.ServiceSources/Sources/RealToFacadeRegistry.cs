using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The reverse of <see cref="ServiceResourceBuilder.Real"/>: given the real, registered resource
/// behind a facade, finds the facade back. Populated by <see cref="ResolvedService.Bridge"/>, the
/// only place a facade wraps a real resource at all (<see cref="ResolvedService.BridgeUnregistered"/>
/// has no real to register).
/// </summary>
/// <remarks>
/// Exists because <see cref="UrlSource.DropWaitsOnUrlServices"/> and
/// <see cref="ServiceWaitRetargeting"/> both walk <c>DistributedApplicationModel.Resources</c> — the
/// registered resources, which is the real one for a bridged consumer, never its facade — while
/// <see cref="ServiceResourceBuilder.WithAnnotation"/> dual-writes the very same annotation instance
/// onto the facade's own collection too (#327). Without this lookup, either path can remove or replace
/// an annotation on the real resource and leave the identical, now-stale instance sitting on the
/// facade, invisible to a walk that only ever sees registered resources.
/// <para>
/// Keyed weakly, like every other per-resource/per-builder cache in this package
/// (<see cref="LocalKindRegistry"/>, <see cref="UrlSource"/>'s container-consumer check), so this
/// bookkeeping cannot keep a resource — or the app model it belongs to — alive past its own lifetime.
/// </para>
/// </remarks>
internal static class RealToFacadeRegistry
{
    private static readonly ConditionalWeakTable<IResource, ServiceResource> ByReal = new();

    public static void Register(IResource real, ServiceResource facade) => ByReal.AddOrUpdate(real, facade);

    public static bool TryGetFacade(IResource real, out ServiceResource? facade) => ByReal.TryGetValue(real, out facade);
}
