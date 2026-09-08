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
    private PrepareMetadata? _prepare;
    private string? _kind;
    private object? _kindOptions;

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
    /// Declares a bootstrap command the <c>"local"</c> source runs inside the materialized checkout
    /// before the kind is allowed to judge it — the code-authoring equivalent of yaml's
    /// <c>prepare:</c> block. See the "prepare" step design
    /// (<c>docs/superpowers/specs/2026-08-28-servicesources-prepare-step-design.md</c>) for what it
    /// runs and when.
    /// </summary>
    /// <param name="command">
    /// The command, as argv rather than a shell string — no quoting or word-splitting rules to get
    /// wrong. A first element that looks like a path is resolved against the checkout and confined
    /// to it; a bare name goes through <c>PATH</c>.
    /// </param>
    /// <param name="windowsCommand">
    /// Replaces <paramref name="command"/> on Windows. Left unset, <paramref name="command"/> runs
    /// there too — correct for a program that is a real executable on every platform, wrong for one
    /// that is a <c>.cmd</c>/<c>.bat</c> shim there (<c>npm</c> is the case to know: there is no
    /// <c>npm.exe</c>).
    /// </param>
    /// <param name="mode">
    /// How often the step runs: <c>"oncePerCommit"</c> (the default when left unset), <c>"once"</c>,
    /// <c>"always"</c>, or <c>"never"</c>.
    /// </param>
    public ServiceDefinitionBuilder WithPrepare(string[] command, string[]? windowsCommand = null, string? mode = null)
    {
        RequireUnset(_prepare, nameof(WithPrepare));
        _prepare = new PrepareMetadata { Command = command, WindowsCommand = windowsCommand, Mode = mode };
        return this;
    }

    /// <summary>Declares this service's kind (language runtime) and optional kind-specific configuration.</summary>
    public ServiceDefinitionBuilder WithKind(string kind, object? options = null)
    {
        RequireUnset(_kind, nameof(WithKind));
        _kind = kind;
        _kindOptions = options;
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
        Prepare = _prepare,
        Kind = _kind ?? LocalKinds.Dotnet,
        KindOptions = _kindOptions,
        Origin = CatalogOrigin.Code,
    };
}
