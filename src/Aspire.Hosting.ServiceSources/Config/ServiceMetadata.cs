using YamlDotNet.Serialization;

namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceMetadata
{
    public string Repository { get; set; } = "";

    public string Project { get; set; } = "";

    public string? DefaultRef { get; set; }

    public KubernetesMetadata? Kubernetes { get; set; }

    public UrlMetadata? Url { get; set; }

    public ContainerMetadata? Container { get; set; }

    public string Kind { get; set; } = LocalKinds.Dotnet;

    /// <summary>
    /// Populated by <see cref="ServiceCatalogLoader"/> from the raw yaml block whose key matches
    /// <see cref="Kind"/> — never bound from a <c>kindConfig:</c> key, hence <see cref="YamlIgnoreAttribute"/>.
    /// </summary>
    [YamlIgnore]
    public object? KindConfig { get; set; }
}
