namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class KubernetesMetadata
{
    public string Service { get; set; } = "";

    public int? Port { get; set; }

    /// <summary>
    /// The scheme the forwarded port speaks — <c>"http"</c> (the default) or <c>"https"</c>. Names
    /// the endpoint consumers reference; see <see cref="Sources.EndpointScheme"/>.
    /// </summary>
    public string? Scheme { get; set; }
}
