namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// One entry under a <c>servicesources.yaml</c> document's <c>repositories:</c> root key — the yaml
/// counterpart of <see cref="Aspire.Hosting.ServiceSources.Catalog.RepositoryBuilder"/>. A service
/// joins the group this names by setting <see cref="ServiceMetadata.RepositoryRef"/> to this entry's
/// key rather than declaring its own <c>repository:</c>/<c>defaultRef:</c>/<c>prepare:</c>.
/// </summary>
/// <remarks>
/// Declared as a class in this namespace, like <see cref="PrepareMetadata"/>, which is what makes
/// <see cref="ServiceCatalogLoader"/>'s unknown-key checks pick it up: field named the same as
/// <see cref="ServiceMetadata.Repository"/> — <c>Repository</c>, spelled <c>repository:</c> in yaml —
/// so the two blocks read alike even though one lives at the document root and the other inside a
/// service.
/// </remarks>
internal sealed class RepositoryMetadata
{
    /// <summary>This repository's URL — the <c>repository:</c> field, matching a service's own field of the same name.</summary>
    public string Repository { get; set; } = "";

    public string? DefaultRef { get; set; }

    /// <summary>
    /// The bootstrap command that runs once for this repository's shared checkout — moved here from
    /// the service (design "prepare moves to the repository"). A service carrying
    /// <see cref="ServiceMetadata.RepositoryRef"/> may not declare its own <c>prepare:</c>; see
    /// <see cref="ServiceCatalogLoader"/>.
    /// </summary>
    public PrepareMetadata? Prepare { get; set; }
}
