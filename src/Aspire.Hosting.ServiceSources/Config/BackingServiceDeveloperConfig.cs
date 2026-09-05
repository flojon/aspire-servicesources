namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// One backing service's entry in the developer config — the database, broker or cache a service
/// connects to, as opposed to the service itself. Each source's settings live in a block named for
/// that source, so only the block <see cref="Source"/> names is ever read.
/// </summary>
/// <remarks>
/// The same shape as <see cref="ServiceDeveloperConfig"/>, and for the same reason: nesting is what
/// makes a source switchable from a higher configuration layer, since
/// <see cref="Microsoft.Extensions.Configuration.IConfiguration"/> merges layers per key rather
/// than per object.
/// <para>
/// Two things differ from a service entry. There is no <c>local</c> block, because the
/// <c>"local"</c> source needs nothing configured — it runs the factory the AppHost passed to
/// <c>AddBackingService</c>, which is ordinary Aspire code and not configuration. And an absent
/// entry is not an error: it means <c>"local"</c>, which is the state of every backing service in
/// an AppHost nobody has pointed anywhere yet.
/// </para>
/// </remarks>
internal sealed class BackingServiceDeveloperConfig
{
    /// <summary>
    /// The source to resolve this backing service from. Empty means <c>"local"</c> — see
    /// <see cref="ServiceSourcesConfigCache.ResolveBackingService"/>.
    /// </summary>
    public string Source { get; set; } = "";

    public DirectDeveloperConfig Direct { get; set; } = new();

    public KubernetesBackingServiceDeveloperConfig Kubernetes { get; set; } = new();
}
