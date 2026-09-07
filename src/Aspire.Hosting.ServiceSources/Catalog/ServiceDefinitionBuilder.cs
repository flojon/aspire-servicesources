using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Catalog;

/// <summary>
/// The fluent <c>With*</c>/<c>As*</c> chain for one code-declared service. Accumulates onto a
/// mutable internal state object; <see cref="Build"/> (called only by
/// <see cref="ServiceCatalogBuilder.Freeze"/>) turns it into an immutable <see cref="ServiceDefinition"/>.
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class ServiceDefinitionBuilder
{
    private readonly string _serviceName;

    private string? _repository;
    private string? _project;
    private string? _defaultRef;
    private UrlMetadata? _url;
    private ContainerMetadata? _container;
    private KubernetesMetadata? _kubernetes;

    // Task 8 adds Kind/KindOptions/WithKind. Each field starts null and each With* throws the
    // additive-error below (via RequireUnset) if its field is already set.

    internal ServiceDefinitionBuilder(string serviceName)
    {
        _serviceName = serviceName;
    }

    internal string ServiceName => _serviceName;

    /// <summary>Declares this service's repository — the "local" source. See design "The authoring API".</summary>
    public ServiceDefinitionBuilder WithRepository(string url, string? project = null, string? defaultRef = null)
    {
        RequireUnset(_repository, nameof(WithRepository));
        _repository = url;
        _project = project;
        _defaultRef = defaultRef;
        return this;
    }

    /// <summary>Declares this service's plain URL — the "url" source. See design "The authoring API".</summary>
    public ServiceDefinitionBuilder WithUrl(string url)
    {
        RequireUnset(_url, nameof(WithUrl));
        _url = new UrlMetadata { Url = url };
        return this;
    }

    /// <summary>Declares this service's container image — the "container" source. See design "The authoring API".</summary>
    public ServiceDefinitionBuilder WithContainer(string image, int port, string? defaultTag = null)
    {
        RequireUnset(_container, nameof(WithContainer));
        _container = new ContainerMetadata { Image = image, Port = port, DefaultTag = defaultTag };
        return this;
    }

    /// <summary>
    /// Declares this service's Kubernetes forward target — the "kubernetes" source. See design "The
    /// authoring API". <see cref="KubernetesMetadata.Scheme"/> has no code-authoring surface in Stage
    /// 1; it stays null (default <c>http</c>), reachable only via the yaml <c>scheme</c> field.
    /// </summary>
    public ServiceDefinitionBuilder WithKubernetes(string service, int? port = null)
    {
        RequireUnset(_kubernetes, nameof(WithKubernetes));
        _kubernetes = new KubernetesMetadata { Service = service, Port = port };
        return this;
    }

    /// <summary>
    /// Guards every <c>With*</c> against a second call for the same block, naming the service and the
    /// block — design "Calls are additive… A second call to the same one is a configuration error".
    /// </summary>
    private void RequireUnset(object? current, string blockName)
    {
        if (current is not null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{_serviceName}': {blockName} was already called. Calls are additive across " +
                "different blocks, but a repeated call to the same one is not — remove one of the two.");
        }
    }

    /// <summary>Builds the immutable <see cref="ServiceDefinition"/> this chain describes.</summary>
    internal ServiceDefinition Build() => new()
    {
        // Repository/Project default to "" (ServiceMetadata's own defaults) when unset — a service
        // declared with no With* call at all is caught downstream by the same "no source configured"
        // path an empty yaml entry hits today, not rejected here.
        Repository = _repository ?? "",
        Project = _project ?? "",
        DefaultRef = _defaultRef,
        Url = _url,
        Container = _container,
        Kubernetes = _kubernetes,
        Kind = LocalKinds.Dotnet,
        Origin = CatalogOrigin.Code,
    };
}
