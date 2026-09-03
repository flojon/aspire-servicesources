using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Turns a cloned/checked-out local repository into a real Aspire resource for one non-dotnet
/// "local" service kind (e.g. JavaScript, Java). Registered
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
    /// Whether <see cref="ResolveDeferred"/> can build this service's resource without reading its
    /// checkout. Defaults to <see langword="false"/>, which is what keeps an existing handler on the
    /// eager path without changing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The point of asking separately is that this is answerable <em>before</em> anything is
    /// registered, which <see cref="ResolveDeferred"/> is not: that call adds resources to the app
    /// model, so it cannot be used to ask a speculative question. Core needs the speculative form to
    /// decide which services to clone ahead of demand.
    /// </para>
    /// <para>
    /// Must therefore touch no filesystem and add nothing to the app model — it is called for
    /// services that may never be added. <paramref name="rawConfig"/> is the same opaque per-kind
    /// block <see cref="Resolve"/> gets, because the answer can legitimately depend on it: a kind
    /// may build some of its options blocks without the checkout and not others. Must not throw
    /// either; a block too malformed to answer for is <see langword="false"/>, which routes it to
    /// the eager path where <see cref="Validate"/> reports it properly.
    /// </para>
    /// </remarks>
    bool SupportsDeferredCheckout(object? rawConfig) => false;

    /// <summary>
    /// <see cref="Resolve"/> for a checkout that has not happened yet: <paramref name="repoRoot"/>
    /// is the directory the clone <em>will</em> land in, and nothing is there. Return
    /// <see langword="null"/> — the default — to say this kind does not support deferral, in which
    /// case core falls back to waiting for the checkout and calling <see cref="Resolve"/> as usual.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called only when the AppHost opted in with <c>UseDeferredCheckout()</c>, this service's
    /// managed checkout is genuinely cold, and <see cref="SupportsDeferredCheckout"/> answered
    /// <see langword="true"/> for this same <paramref name="rawConfig"/>. Returning
    /// <see langword="null"/> anyway is still honoured, but it is no longer free: the checkout
    /// prefetch acts on <see cref="SupportsDeferredCheckout"/>, leaving a service that answered
    /// <see langword="true"/> out of the clones it starts ahead of demand, so declining here drops
    /// the service onto the eager path with no clone already running for it — it is cloned inline,
    /// alone, on the <c>AddService()</c> thread rather than alongside the other services. A kind
    /// that can decide in advance should say so there, where asking is free and the answer is
    /// acted on.
    /// Build the resource exactly as <see cref="Resolve"/>
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
