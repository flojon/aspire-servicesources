namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ContainerMetadata
{
    public string Image { get; set; } = "";

    public int? Port { get; set; }

    public string? DefaultTag { get; set; }

    /// <summary>
    /// The scheme the image serves on <see cref="Port"/> — <c>"http"</c> (the default) or
    /// <c>"https"</c>. Names the endpoint consumers reference; see
    /// <see cref="Sources.EndpointScheme"/>.
    /// </summary>
    public string? Scheme { get; set; }
}
