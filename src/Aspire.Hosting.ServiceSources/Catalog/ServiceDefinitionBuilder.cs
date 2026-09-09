using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Prepare;
using Aspire.Hosting.ServiceSources.Sources;

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

    // The ContainerMetadata or KubernetesMetadata most recently created by WithContainer/
    // WithKubernetes — where WithHttpEndpoint/WithHttpsEndpoint stamp a scheme, matching Aspire's
    // own "call it right after the thing it names a scheme for" idiom.
    private object? _schemeOwner;

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
        _schemeOwner = _container;
        return this;
    }

    /// <summary>
    /// Declares this service's Kubernetes forward target — the "kubernetes" source. See design "The
    /// authoring API".
    /// </summary>
    public ServiceDefinitionBuilder WithKubernetes(string service, int? port = null)
    {
        RequireUnset(_kubernetes, nameof(WithKubernetes));
        _kubernetes = new KubernetesMetadata { Service = service, Port = port };
        _schemeOwner = _kubernetes;
        return this;
    }

    /// <summary>
    /// Names the scheme of the source declared immediately before this call — <see cref="WithContainer"/>
    /// or <see cref="WithKubernetes"/> — as <c>"http"</c>. Mirrors Aspire's own
    /// <c>WithHttpEndpoint()</c>/<c>WithHttpsEndpoint()</c> pair; see <see cref="EndpointScheme"/> for
    /// what a scheme actually changes at resolution time.
    /// </summary>
    public ServiceDefinitionBuilder WithHttpEndpoint() => WithScheme(EndpointScheme.Http);

    /// <summary>Names the scheme of the most recently declared source as <c>"https"</c>. See <see cref="WithHttpEndpoint"/>.</summary>
    public ServiceDefinitionBuilder WithHttpsEndpoint() => WithScheme(EndpointScheme.Https);

    private ServiceDefinitionBuilder WithScheme(string scheme)
    {
        switch (_schemeOwner)
        {
            case ContainerMetadata container:
                RequireUnset(container.Scheme, $"{nameof(WithHttpEndpoint)}/{nameof(WithHttpsEndpoint)} for this {nameof(WithContainer)}");
                container.Scheme = scheme;
                break;
            case KubernetesMetadata kubernetes:
                RequireUnset(kubernetes.Scheme, $"{nameof(WithHttpEndpoint)}/{nameof(WithHttpsEndpoint)} for this {nameof(WithKubernetes)}");
                kubernetes.Scheme = scheme;
                break;
            default:
                throw new ServiceSourcesConfigurationException(
                    $"Service '{_serviceName}': {nameof(WithHttpEndpoint)}/{nameof(WithHttpsEndpoint)} must " +
                    $"immediately follow {nameof(WithContainer)} or {nameof(WithKubernetes)} — there is no " +
                    "endpoint-bearing source to name a scheme for yet.");
        }

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
    /// How often the step runs: <see cref="PrepareMode.OncePerCommit"/> (the default when left
    /// unset), <see cref="PrepareMode.Once"/>, <see cref="PrepareMode.Always"/>, or
    /// <see cref="PrepareMode.Never"/> — the enum behind yaml's four <c>mode</c> spellings.
    /// </param>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// <paramref name="mode"/> is not one of the four. A caller crossing the AppHost transport layer
    /// can hand an enum parameter an integer that names no member, and that is a mistake in an
    /// AppHost rather than a bug in here.
    /// </exception>
    public ServiceDefinitionBuilder WithPrepare(
        string[] command, string[]? windowsCommand = null, PrepareMode mode = PrepareMode.OncePerCommit)
    {
        RequireUnset(_prepare, nameof(WithPrepare));

        // Before PrepareModes.Written, which is a lookup over the defined members and total only
        // over those.
        if (!Enum.IsDefined(mode))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{_serviceName}': {nameof(WithPrepare)} was given mode '{(int)mode}', which is not a "
                + $"{nameof(PrepareMode)}. Set it to one of "
                + string.Join(", ", Enum.GetValues<PrepareMode>().Select(m => $"{nameof(PrepareMode)}.{m}"))
                + " — the four the yaml block spells "
                + string.Join(", ", Enum.GetValues<PrepareMode>().Select(m => $"'{PrepareModes.Written(m)}'"))
                + ".");
        }

        // Stored as the spelling the yaml block uses, so that PrepareMetadata.Mode carries one
        // representation whichever file the block came from and PreparePlan parses it in one place.
        _prepare = new PrepareMetadata
        {
            Command = command,
            WindowsCommand = windowsCommand,
            Mode = PrepareModes.Written(mode),
        };

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
        Repository = new RepositoryDefinition
        {
            Url = _repository ?? "",
            DefaultRef = _defaultRef,
            Prepare = _prepare,
            CheckoutName = _serviceName,
        },
        Project = _project ?? "",
        Url = _url,
        Container = _container,
        Kubernetes = _kubernetes,
        Kind = _kind ?? LocalKinds.Dotnet,
        KindOptions = _kindOptions,
        Origin = CatalogOrigin.Code,
    };
}
