namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ContainerMetadata
{
    public string Image { get; set; } = "";

    public int? Port { get; set; }

    public string? DefaultTag { get; set; }
}
