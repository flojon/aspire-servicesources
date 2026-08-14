namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ClusterMetadata
{
    public string Service { get; set; } = "";

    public int? Port { get; set; }
}
