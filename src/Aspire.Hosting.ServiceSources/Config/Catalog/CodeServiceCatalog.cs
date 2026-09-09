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

    /// <summary>
    /// Every repository declared across both catalogs — code-declared (<c>AddRepository</c>) and
    /// yaml-declared (<c>repositories:</c>) — keyed by <see cref="RepositoryDefinition.CheckoutName"/>.
    /// Populated by <c>LoadedConfig.Load</c> once both catalogs are read; the composition-time checks
    /// over this set (a name colliding with an ungrouped service, or declared in both catalogs) live
    /// there too. Repositories resolve per catalog, before the merge (design "Composition, and the
    /// checkout"), so this is a merged view for downstream consumers rather than something either
    /// producer builds on its own.
    /// </summary>
    public required IReadOnlyDictionary<string, RepositoryDefinition> Repositories { get; init; }
}
