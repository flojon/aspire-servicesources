namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceDeveloperConfig
{
    public string Source { get; set; } = "";

    public string? Path { get; set; }

    public string? Ref { get; set; }
}
