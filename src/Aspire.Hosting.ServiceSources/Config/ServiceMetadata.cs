namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceMetadata
{
    public string Repository { get; set; } = "";

    public string Project { get; set; } = "";

    public string? DefaultRef { get; set; }

    public ClusterMetadata? Cluster { get; set; }

    public UrlMetadata? Url { get; set; }

    public ContainerMetadata? Container { get; set; }
}
