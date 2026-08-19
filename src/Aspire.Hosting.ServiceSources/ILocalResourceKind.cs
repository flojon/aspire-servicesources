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
}
