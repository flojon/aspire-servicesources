namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class KubernetesMetadata
{
    public string Service { get; set; } = "";

    public int? Port { get; set; }
}
