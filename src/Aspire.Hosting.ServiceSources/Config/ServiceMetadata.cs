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

    /// <summary>
    /// A bootstrap command the <c>"local"</c> source runs inside the materialized checkout, before
    /// the kind is allowed to judge it. Absent for the services — most of them — whose checkout is
    /// runnable the moment it is cloned.
    /// </summary>
    public PrepareMetadata? Prepare { get; set; }

    public string Kind { get; set; } = LocalKinds.Dotnet;

    /// <summary>
    /// Populated by <see cref="ServiceCatalogLoader"/> from the raw yaml block whose key matches
    /// <see cref="Kind"/> — never bound from a <c>kindConfig:</c> key, hence <see cref="YamlIgnoreAttribute"/>.
    /// </summary>
    [YamlIgnore]
    public object? KindConfig { get; set; }
}
