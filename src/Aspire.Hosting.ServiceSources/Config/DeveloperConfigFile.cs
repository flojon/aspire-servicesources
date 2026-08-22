namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class DeveloperConfigFile
{
    private Dictionary<string, ServiceDeveloperConfig> _services = new();

    /// <summary>
    /// Coerced away from null for the same reason as <see cref="ServiceCatalog.Services"/>: an
    /// explicit <c>"services": null</c> overrides the field initializer, which would fault
    /// <see cref="ServiceSourcesConfigCache"/> when it looks a service up.
    /// </summary>
    public Dictionary<string, ServiceDeveloperConfig> Services
    {
        get => _services;
        set => _services = value ?? [];
    }
}
