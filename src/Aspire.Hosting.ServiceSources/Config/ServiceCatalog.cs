namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceCatalog
{
    public Dictionary<string, ServiceMetadata> Services { get; set; } = new();
}
