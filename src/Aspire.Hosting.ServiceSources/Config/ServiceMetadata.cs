namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceMetadata
{
    public string Repository { get; set; } = "";

    public string Project { get; set; } = "";

    public string? DefaultRef { get; set; }

    public KubernetesMetadata? Kubernetes { get; set; }

    public UrlMetadata? Url { get; set; }

    public ContainerMetadata? Container { get; set; }

    public string Kind { get; set; } = "dotnet";

    public object? KindConfig { get; set; }
}
