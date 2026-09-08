namespace Aspire.Hosting.ServiceSources.Config.Catalog;

/// <summary>
/// The composed catalog every downstream reader sees — code-declared and yaml-declared entries
/// merged into one map, keyed and compared exactly as <see cref="ServiceCatalog.Services"/> was:
/// <c>Ordinal</c>. Design finding 3 — <c>ServiceCatalog.Services</c> cannot be retyped because
/// <see cref="ServiceCatalogLoader"/> binds <see cref="ServiceCatalog"/> with YamlDotNet directly.
/// </summary>
internal sealed class CodeServiceCatalog
{
    public required IReadOnlyDictionary<string, ServiceDefinition> Services { get; init; }
}
