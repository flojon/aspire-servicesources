namespace Aspire.Hosting.ServiceSources.Config.Catalog;

/// <summary>
/// The composed, source-agnostic entry every downstream consumer reads — produced from a yaml
/// <see cref="ServiceMetadata"/> via <see cref="ServiceMetadata.ToDefinition"/>, or built
/// directly by <see cref="ServiceDefinitionBuilder"/> for a code-declared service. Two things a
/// yaml-bound <see cref="ServiceMetadata"/> deliberately does not carry: <see cref="Origin"/> (design
/// finding 5) and <see cref="KindOptions"/> as an already-typed value rather than a raw yaml block
/// (design finding 6).
/// </summary>
internal sealed class ServiceDefinition
{
    public required string Repository { get; init; }

    public required string Project { get; init; }

    public string? DefaultRef { get; init; }

    public KubernetesMetadata? Kubernetes { get; init; }

    public UrlMetadata? Url { get; init; }

    public ContainerMetadata? Container { get; init; }

    public PrepareMetadata? Prepare { get; init; }

    public required string Kind { get; init; }

    /// <summary>
    /// The raw yaml block (round-tripped through <see cref="LocalKindConfig.Parse{T}"/>) for a
    /// yaml-declared kind, or an already-typed options object for a code-declared one — see design
    /// finding 6's three-branch <see cref="LocalKindConfig.Parse{T}"/> change (Task 7).
    /// </summary>
    public object? KindOptions { get; init; }

    public required CatalogOrigin Origin { get; init; }
}
