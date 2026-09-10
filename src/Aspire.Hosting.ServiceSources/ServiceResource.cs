using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// What <c>AddService()</c> returns for every source. Never the object DCP registers and starts —
/// see <c>Sources.ResolvedService.Bridge</c> — so its own declared shape stays the same across
/// <c>"container"</c>, <c>"kubernetes"</c>, <c>"local"</c> and <c>"url"</c>, while each dual-writes
/// configuration to the real, source-specific resource behind it.
/// </summary>
/// <remarks>
/// Deliberately does not implement:
/// <list type="bullet">
///   <item><description>
///   <see cref="IResourceWithoutLifetime"/> — giving every instance this marker unconditionally
///   would also suppress <c>WaitFor</c> on <c>container</c>/<c>kubernetes</c>/<c>local</c>-sourced
///   services, which must keep working. The <c>"url"</c> source's "no lifetime" behaviour is
///   preserved a different way — <c>Sources.UrlSource.DropWaitsOnUrlServices</c> strips a consumer's
///   <see cref="WaitAnnotation"/> at <c>BeforeStartEvent</c>, before Aspire's own wait machinery ever
///   evaluates one.
///   </description></item>
///   <item><description>
///   Any container-only vocabulary (<c>WithImage</c>, <c>WithBindMount</c>, <c>WithVolume</c>,
///   <c>WithDockerfile</c>, <c>WithLifetime</c>, …), <c>IComputeResource</c> and
///   <c>IResourceWithProbes</c> — DCP-execution-model concerns tied to being the literal
///   registered resource, which this type never is.
///   </description></item>
/// </list>
/// See docs/superpowers/specs/2026-09-10-313-service-resource-dualwrite-bridge-design.md §1.
/// </remarks>
public sealed class ServiceResource(string name) : Resource(name),
    IResourceWithServiceDiscovery,
    IResourceWithEnvironment,
    IResourceWithArgs,
    IResourceWithEndpoints,
    IResourceWithWaitSupport;
