namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// One service's entry in the developer config. Each source's settings live in a block named for
/// that source, so only the block <see cref="Source"/> names is ever read.
/// </summary>
/// <remarks>
/// The nesting is what makes a source switchable from a higher configuration layer.
/// <see cref="IConfiguration"/> merges layers per key rather than per object, so with the settings
/// flat on this type a lower layer's <c>url</c> would survive a higher layer setting
/// <c>source: local</c> and land here alongside it. Under a block it still survives, but nothing
/// reads it.
///
/// The blocks are never null. An entry naming a source with no block of its own is the common case
/// — <c>{ "source": "local" }</c> is a complete entry — and an absent block and an empty one mean
/// the same thing, so consumers read through them without a null check.
/// </remarks>
internal sealed class ServiceDeveloperConfig
{
    public string Source { get; set; } = "";

    public LocalDeveloperConfig Local { get; set; } = new();

    public UrlDeveloperConfig Url { get; set; } = new();

    public KubernetesDeveloperConfig Kubernetes { get; set; } = new();

    public ContainerDeveloperConfig Container { get; set; } = new();
}
