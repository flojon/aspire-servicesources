namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class DeveloperConfigFile
{
    public string? CacheDirectory { get; set; }

    public Dictionary<string, ServiceDeveloperConfig> Services { get; set; } = new();
}
