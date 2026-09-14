using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The escape hatch to a kind-specific resource type native vocabulary cannot reach.
/// </summary>
/// <remarks>
/// <para>
/// <c>ServiceResource</c>'s five interfaces cover <c>WithEnvironment</c>, <c>WithReference</c>,
/// <c>WithArgs</c>, <c>WithHttpEndpoint</c>/<c>WithHttpsEndpoint</c> and
/// <c>WaitFor</c>/<c>WaitForCompletion</c> directly — there is no longer a <c>Configure&lt;T&gt;</c>
/// dispatcher standing between an AppHost and Aspire's own extension methods. What native vocabulary
/// cannot reach is a non-<c>dotnet</c> local kind's own vocabulary
/// (<c>service.Unwrap&lt;JavaScriptAppResource&gt;().WithRunScript("dev")</c>), which is open-ended
/// and package-external, so <see cref="Unwrap{T}"/> is what remains.
/// </para>
/// <para>
/// Named <c>Unwrap</c> rather than the earlier <c>As</c>: Aspire's own <c>As*</c> convention means
/// "reinterpret this resource for publish," always returning the <em>same</em> builder type
/// (<c>AsHttp2Service</c>, and similar) — never a downcast to a different type, which is exactly what
/// this method does. Renamed for that reason, not because the capability itself is retired.
/// </para>
/// </remarks>
public static class ServiceConfigurationExtensions
{
    /// <summary>
    /// The resolved resource's builder, viewed as <typeparamref name="T"/> — the real,
    /// source-specific resource behind the <see cref="ServiceResource"/> facade
    /// <c>AddService()</c> returns.
    /// </summary>
    /// <remarks>
    /// This <b>throws</b> for an out-of-band source rather than skipping: it has to return a
    /// builder, and the only alternatives would be handing back the <c>kubectl port-forward</c>
    /// executable — silently configuring the wrong process — or returning null.
    /// </remarks>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The resolved resource is not a <typeparamref name="T"/>, or <typeparamref name="T"/> cannot
    /// reach the service behind an out-of-band source (<c>"url"</c>, <c>"kubernetes"</c>) — see
    /// <see cref="IsUnreachable{T}"/> for the one capability that still can.
    /// </exception>
    [AspireExportIgnore(Reason =
        "A generic method projects into ATS with its type parameter dropped, and here T *is* " +
        "the resource type being requested, so the export would arrive broken rather than absent.")]
    public static IResourceBuilder<T> Unwrap<T>(this IResourceBuilder<IResourceWithServiceDiscovery> service)
        where T : IResource
    {
        var annotation = service.Resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault();

        // Read once, up front, so a mismatch message below can name the real type behind the facade
        // rather than the facade itself — service.Resource is unconditionally a ServiceResource, so
        // reporting *its* type on a mismatch would say "ServiceResource" no matter what T was wrong.
        var real = (service as Sources.ServiceResourceBuilder)?.Real?.Resource;

        // Checked before the cast, not after, because a source can resolve to a resource that
        // *accepts* the configuration while being the wrong thing to configure. A kubernetes-sourced
        // service is an ExecutableResource wrapping `kubectl port-forward`, so it takes environment
        // variables happily — and they would reach kubectl, never the service behind it. Silently
        // configuring the wrong process is exactly the failure mode issue #53 was filed about.
        if (annotation is not null && IsUnreachable<T>(annotation.Source))
        {
            throw new ServiceSourcesConfigurationException(Explain<T>(service.Resource, annotation, real));
        }

        // service.Resource is always the ServiceResource facade now (never the real, source-specific
        // object), so the cast goes through the ServiceResourceBuilder wrapper's own Real property
        // instead of service.Resource itself — the only way to reach the real resource behind it.
        // "url"'s Real is always null, but that path never reaches this check: IsUnreachable<T> is
        // already unconditionally true for "url", so the throw above fires first.
        if (real is T typed)
        {
            return service.ApplicationBuilder.CreateResourceBuilder(typed);
        }

        throw new ServiceSourcesConfigurationException(Explain<T>(service.Resource, annotation, real));
    }

    /// <summary>
    /// Whether <typeparamref name="T"/> cannot reach the service behind <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// Keyed on the capability as well as the source, because "runs out of band" and "nothing here
    /// can honour this" are not the same claim. A <c>"kubernetes"</c> service resolves to a real,
    /// registered <c>kubectl port-forward</c> executable: configuration that would reach the
    /// <i>process</i> is wrong, since it lands on kubectl rather than the service behind it, but
    /// start ordering is not — holding the port-forward back until a migration finishes is exactly
    /// what the AppHost asked for, and Aspire honours it.
    /// <para>
    /// Nothing is reachable for <c>"url"</c>: its resource is deliberately never registered (see
    /// <see cref="Sources.UrlSource"/>), so there is no process to order and no configuration to
    /// apply.
    /// </para>
    /// <para>
    /// <see cref="Reachability.OutOfBandSources"/> is the same source list keyed differently — this
    /// method gates by requested capability <em>type parameter</em>, <see cref="Reachability"/> gates
    /// by the annotation type native vocabulary already added — so the set itself is shared rather
    /// than redeclared.
    /// </para>
    /// </remarks>
    private static bool IsUnreachable<T>(string source)
        where T : IResource =>
        Reachability.OutOfBandSources.Contains(source)
        && !(string.Equals(source, "kubernetes", StringComparison.Ordinal) && typeof(T) == typeof(IResourceWithWaitSupport));

    /// <summary>
    /// Names the source as well as the type, because the source is what a developer changes to make
    /// the call apply — and what another developer may have changed to make it stop applying.
    /// </summary>
    /// <param name="resource">
    /// The facade — <c>service.Resource</c> is always a <see cref="ServiceResource"/>, so this is
    /// only ever used to name a resource that is <em>not</em> one of ours (the <c>annotation is
    /// null</c> branch below).
    /// </param>
    /// <param name="annotation">The facade's source tag, or <see langword="null"/> if it has none.</param>
    /// <param name="real">
    /// The actual resource behind the facade, when one exists — named in the mismatch message instead
    /// of <paramref name="resource"/>, which is always a <see cref="ServiceResource"/> and would
    /// otherwise make every mismatch read "is a ServiceResource" regardless of what was actually
    /// requested.
    /// </param>
    private static string Explain<T>(IResource resource, ServiceSourceAnnotation? annotation, IResource? real)
    {
        var name = annotation?.ServiceName ?? resource.Name;

        if (annotation is null)
        {
            return $"Resource '{name}' ({resource.GetType().Name}) is not a {typeof(T).Name}.";
        }

        var detail = annotation.Source switch
        {
            "url" =>
                "Source 'url' resolves to a fixed, already-running URL — there is no local process for " +
                "this AppHost to configure. Configure it wherever it actually runs, or give the service a " +
                "source that runs locally ('local' or 'container') in servicesources.local.json.",
            "kubernetes" =>
                "Source 'kubernetes' resolves to a 'kubectl port-forward' process in front of an " +
                "already-running service, so configuration applied here would reach the port-forward rather " +
                "than the service itself. Give the service a source that runs locally ('local' or " +
                "'container') in servicesources.local.json, or drop the configuration.",
            _ =>
                $"The resolved resource is a {(real ?? resource).GetType().Name}, which does not provide it.",
        };

        return $"Service '{name}' cannot be configured as {typeof(T).Name}: " +
               $"its source is '{annotation.Source}'. {detail}";
    }
}
