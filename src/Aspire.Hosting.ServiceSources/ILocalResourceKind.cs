using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Turns a cloned/checked-out local repository into a real Aspire resource for one non-dotnet
/// "local" service kind (e.g. JavaScript, Java). Implemented by satellite packages and registered
/// via <see cref="ServiceSourcesBuilderExtensions.AddLocalKind"/>.
/// </summary>
public interface ILocalResourceKind
{
    /// <summary>
    /// <paramref name="repoRoot"/> is the already-resolved local checkout directory (cloning and
    /// ref checkout have already happened by the time this is called). <paramref name="rawConfig"/>
    /// is the service's opaque per-kind yaml block — parse it with
    /// <see cref="LocalKindConfig.Parse{T}"/>.
    /// </summary>
    IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder,
        string serviceName,
        string repoRoot,
        object? rawConfig);

    /// <summary>
    /// Optional pre-flight check, called for a service immediately before <see cref="Resolve"/> and
    /// before that service has added anything to the app model. Implementations that parse
    /// <paramref name="rawConfig"/> in <see cref="Resolve"/> should parse it here too —
    /// <see cref="LocalKindConfig.Parse{T}"/> is cheap and side-effect free — so a typo'd options
    /// block is reported without a half-created resource. Throw
    /// <see cref="ServiceSourcesConfigurationException"/> to report a problem; the default is a no-op.
    /// </summary>
    /// <remarks>
    /// This used to run for every "local" service before <em>any</em> of them had touched the app
    /// model, so failures could be aggregated. That guarantee is gone: <c>AddService()</c> now
    /// returns the real resource, so each service is fully resolved before the next is even
    /// mentioned. Services resolved earlier in <c>Program.cs</c> are already in the app model when
    /// this runs.
    /// </remarks>
    void Validate(string serviceName, object? rawConfig)
    {
    }
}
