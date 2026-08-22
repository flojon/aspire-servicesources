namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceCatalog
{
    private Dictionary<string, ServiceMetadata> _services = new();

    /// <summary>
    /// YamlDotNet assigns null for a bare <c>services:</c> key with nothing under it, overriding the
    /// field initializer — coerce it back to empty so that document behaves like an omitted key or an
    /// explicit <c>services: {}</c>. Without this, <see cref="ServiceCatalogLoader"/> would fault with
    /// a NullReferenceException while enumerating the map, instead of letting
    /// <see cref="ServiceSourcesConfigCache"/> report the referenced service by name.
    /// </summary>
    public Dictionary<string, ServiceMetadata> Services
    {
        get => _services;
        set => _services = value ?? [];
    }
}
