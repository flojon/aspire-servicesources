namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// Untyped mirror of <see cref="ServiceCatalog"/>, used only to fish the raw yaml mapping for a
/// service's kind-specific block (e.g. the <c>javascript:</c> block) out of the document — core
/// doesn't know the shape of that block, so it can't be captured by <see cref="ServiceMetadata"/>
/// itself. YamlDotNet deserializes each service's remaining unknown keys as
/// <c>Dictionary&lt;object, object&gt;</c> values here because the declared value type is
/// <c>object</c>.
/// </summary>
internal sealed class RawServiceCatalog
{
    private Dictionary<string, Dictionary<string, object>> _services = new();

    /// <summary>
    /// Coerced away from null for the same reason as <see cref="ServiceCatalog.Services"/>: a bare
    /// <c>services:</c> key deserializes the map itself to null, overriding the field initializer.
    /// </summary>
    public Dictionary<string, Dictionary<string, object>> Services
    {
        get => _services;
        set => _services = value ?? [];
    }
}
