namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class DeveloperConfigFile
{
    public Dictionary<string, ServiceDeveloperConfig> Services { get; set; } = new();
}
