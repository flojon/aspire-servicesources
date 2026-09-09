using YamlDotNet.Serialization;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceMetadata
{
    public string Repository { get; set; } = "";

    public string Project { get; set; } = "";

    public string? DefaultRef { get; set; }

    /// <summary>
    /// The key of a <c>repositories:</c> entry this service joins, sharing its checkout with every
    /// other service naming the same one — the yaml counterpart of
    /// <see cref="Aspire.Hosting.ServiceSources.Catalog.ServiceDefinitionBuilder.WithSharedRepository"/>.
    /// Mutually exclusive with <see cref="Repository"/> and <see cref="DefaultRef"/> (the ref now
    /// belongs to the repository) and with <see cref="Prepare"/> (which moves there too) — validated
    /// in <see cref="ServiceCatalogLoader"/>, not here: this type does no cross-field validation of
    /// its own.
    /// </summary>
    public string? RepositoryRef { get; set; }

    public KubernetesMetadata? Kubernetes { get; set; }

    public UrlMetadata? Url { get; set; }

    public ContainerMetadata? Container { get; set; }

    /// <summary>
    /// A bootstrap command the <c>"local"</c> source runs inside the materialized checkout, before
    /// the kind is allowed to judge it. Absent for the services — most of them — whose checkout is
    /// runnable the moment it is cloned.
    /// </summary>
    public PrepareMetadata? Prepare { get; set; }

    public string Kind { get; set; } = LocalKinds.Dotnet;

    /// <summary>
    /// Populated by <see cref="ServiceCatalogLoader"/> from the raw yaml block whose key matches
    /// <see cref="Kind"/> — never bound from a <c>kindConfig:</c> key, hence <see cref="YamlIgnoreAttribute"/>.
    /// </summary>
    [YamlIgnore]
    public object? KindConfig { get; set; }

    /// <summary>
    /// Converts this yaml-bound entry into the source-agnostic <see cref="ServiceDefinition"/>
    /// every downstream consumer reads. <see cref="Kind"/> is already normalized to
    /// <see cref="LocalKinds.Dotnet"/> by <see cref="ServiceCatalogLoader"/> by the time this runs,
    /// so no further normalization happens here. Ungrouped, <paramref name="serviceName"/> mints this
    /// service's anonymous <see cref="RepositoryDefinition"/> — its
    /// <see cref="RepositoryDefinition.CheckoutName"/> is the service's own name, which is what keeps
    /// an existing checkout path byte-identical (repository-handle design finding 2, #291). Grouped
    /// (<see cref="RepositoryRef"/> set), the shared instance from <paramref name="repositories"/> is
    /// used instead — the same instance every other service naming that ref gets, which is the whole
    /// of the grouping mechanism.
    /// </summary>
    /// <param name="repositories">
    /// This yaml file's own <c>repositories:</c> entries, already resolved to
    /// <see cref="RepositoryDefinition"/> instances by <see cref="ServiceCatalogLoader"/> — one per
    /// entry, shared by reference across every service naming it. <see cref="RepositoryRef"/> is
    /// validated to exist in here before this method ever runs (composition-time, no filesystem
    /// access), so a lookup miss here would be this method's own bug, not a developer's — see
    /// <see cref="ServiceCatalogLoader.Load"/>.
    /// </param>
    public ServiceDefinition ToDefinition(
        string yamlPath, string serviceName, IReadOnlyDictionary<string, RepositoryDefinition> repositories) => new()
    {
        Repository = RepositoryRef is { } repositoryRef
            ? repositories[repositoryRef]
            : new RepositoryDefinition
            {
                Url = Repository,
                DefaultRef = DefaultRef,
                Prepare = Prepare,
                CheckoutName = serviceName,
            },
        Project = Project,
        Kubernetes = Kubernetes,
        Url = Url,
        Container = Container,
        Kind = Kind,
        KindOptions = KindConfig,
        Origin = CatalogOrigin.FromYaml(yamlPath),
    };
}
