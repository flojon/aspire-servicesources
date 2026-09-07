namespace Aspire.Hosting.ServiceSources.Config.Catalog;

/// <summary>
/// Which catalog declared a <see cref="ServiceDefinition"/> — code, via
/// <c>AddServiceCatalog(…)</c>, or a specific <c>servicesources.yaml</c> file. Threaded into every
/// error message a code-declared service can reach, so none of them names a file that need not
/// exist (design finding 5).
/// </summary>
internal enum CatalogOriginKind
{
    Code,
    Yaml,
}

internal sealed record CatalogOrigin(CatalogOriginKind Kind, string? YamlPath = null)
{
    public static readonly CatalogOrigin Code = new(CatalogOriginKind.Code);

    public static CatalogOrigin FromYaml(string path) => new(CatalogOriginKind.Yaml, path);

    /// <summary>How this origin names itself inside an exception message.</summary>
    public string Describe() =>
        Kind == CatalogOriginKind.Code ? "code (AddServiceCatalog)" : $"'{YamlPath}'";
}
