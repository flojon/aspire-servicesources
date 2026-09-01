namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceDeveloperConfig
{
    public string Source { get; set; } = "";

    public string? Path { get; set; }

    public string? Ref { get; set; }

    public string? Context { get; set; }

    public string? Namespace { get; set; }

    public int? Port { get; set; }

    public string? Url { get; set; }

    public string? Tag { get; set; }

    public string? Scheme { get; set; }
}
