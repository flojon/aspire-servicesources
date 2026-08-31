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

    /// <summary>
    /// <see cref="Resolve"/> for a checkout that has not happened yet: <paramref name="repoRoot"/>
    /// is the directory the clone <em>will</em> land in, and nothing is there. Return
    /// <see langword="null"/> — the default — to say this kind does not support deferral, in which
    /// case core falls back to waiting for the checkout and calling <see cref="Resolve"/> as usual.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called only when the AppHost opted in with <c>UseDeferredCheckout()</c> and this service's
    /// managed checkout is genuinely cold. Build the resource exactly as <see cref="Resolve"/>
    /// would, but touch no file under <paramref name="repoRoot"/>: hand the checks that need the
    /// working tree back as <see cref="DeferredLocalResource.ValidateCheckout"/> and core will run
    /// them after the clone. Everything else is unchanged, including the endpoints — those cannot be
    /// added later, so a kind that can only learn its endpoints by reading the repository should
    /// return <see langword="null"/> rather than register a service nothing can resolve.
    /// </para>
    /// <para>
    /// Holding the resource back and starting it is core's job, not the handler's, and it covers
    /// every resource this call adds to the app model — see <see cref="DeferredLocalResource"/>.
    /// </para>
    /// </remarks>
    DeferredLocalResource? ResolveDeferred(
        IDistributedApplicationBuilder builder,
        string serviceName,
        string repoRoot,
        object? rawConfig) => null;
}
