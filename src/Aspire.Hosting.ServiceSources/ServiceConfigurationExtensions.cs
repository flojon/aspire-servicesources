using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Lets the AppHost apply its own configuration — references, environment variables, wait
/// ordering — to the resource <c>AddService()</c> resolved.
/// </summary>
/// <remarks>
/// <para>
/// The resolved resource's type depends on the service's source, which each developer sets in
/// <c>servicesources.local.json</c> and can change without touching the AppHost. It may also be a
/// type this package has never heard of, produced by a satellite
/// <see cref="ILocalResourceKind"/> delegating to an official Aspire integration. So the
/// capabilities available cannot be expressed in <c>AddService</c>'s return type, and these methods
/// name the capability they need and check for it at runtime.
/// </para>
/// <para>
/// Both methods are deliberately named so as not to collide with anything in Aspire's own API, and
/// deliberately not a fluent mirror of it. Not because a mirror would be <i>ambiguous</i>:
/// <see cref="IResourceBuilder{T}"/> is covariant, so a <c>WithEnvironment</c> declared here on
/// <c>IResourceBuilder&lt;IResourceWithServiceDiscovery&gt;</c> does bind to
/// <c>IResourceBuilder&lt;ProjectResource&gt;</c> as well, but overload resolution prefers Aspire's
/// own generic overload for an exact receiver, so <c>AddProject(...).WithEnvironment(...)</c> keeps
/// compiling and keeps calling Aspire's. The reasons are the other two: that same covariance would
/// put every mirrored method into IntelliSense on <i>every</i> resource builder in any AppHost
/// referencing this package, and a mirror only ever covers the subset of Aspire's API somebody
/// remembered to mirror. <c>Configure&lt;T&gt;</c> needs no updating as Aspire's API grows, and
/// reaches satellite-specific extension methods this package has never heard of.
/// </para>
/// </remarks>
public static class ServiceConfigurationExtensions
{
    /// <summary>
    /// Applies <paramref name="configure"/> to the resolved resource, viewed as
    /// <typeparamref name="T"/> — the capability the configuration needs, such as
    /// <see cref="IResourceWithEnvironment"/> or <see cref="IResourceWithWaitSupport"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// builder.AddService("backend")
    ///        .Configure&lt;IResourceWithEnvironment&gt;(r => r
    ///            .WithReference(planningDb)
    ///            .WithEnvironment("DBPASSWORD", postgres.Resource.PasswordParameter))
    ///        .Configure&lt;IResourceWithWaitSupport&gt;(r => r.WaitForCompletion(migrations));
    /// </code>
    /// </example>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The resolved resource is not a <typeparamref name="T"/> — see <see cref="As{T}"/>.
    /// </exception>
    [AspireExportIgnore(Reason =
        "A generic method projects into ATS with its type parameter dropped, and here T *is* " +
        "the capability being requested, so the export would arrive broken rather than absent. " +
        "Guest-language AppHosts use the non-generic shims in ServiceConfigurationExports " +
        "instead, which delegate here.")]
    public static IResourceBuilder<IResourceWithServiceDiscovery> Configure<T>(
        this IResourceBuilder<IResourceWithServiceDiscovery> service, Action<IResourceBuilder<T>> configure)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(configure);

        var annotation = service.Resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault();

        // Skipped, not applied and not thrown. A developer switching this service to a remote source
        // in their own servicesources.local.json must not break a Program.cs they don't own — that
        // per-developer switch is the point of the package. The skip is logged rather than silent.
        if (annotation is not null && IsUnreachable<T>(annotation.Source))
        {
            ServiceConfigurationWarnings.For(service.ApplicationBuilder)
                .AddSkip(annotation.ServiceName, annotation.Source, $"Configure<{typeof(T).Name}>");
            return service;
        }

        configure(service.As<T>());

        return service;
    }

    /// <summary>
    /// The resolved resource's builder, viewed as <typeparamref name="T"/>. Reaches anything
    /// <see cref="Configure{T}"/> would, plus a satellite kind's own extension methods
    /// (<c>service.As&lt;JavaScriptAppResource&gt;().WithRunScript("dev")</c>).
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Configure{T}"/>, this <b>throws</b> for an out-of-band source rather than
    /// skipping: it has to return a builder, and the only alternatives would be handing back the
    /// <c>kubectl port-forward</c> executable — silently configuring the wrong process — or
    /// returning null. Prefer <see cref="Configure{T}"/> for anything that should survive a
    /// developer switching the service's source; reach for this when the AppHost genuinely requires
    /// a specific resource type.
    /// </remarks>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The resolved resource is not a <typeparamref name="T"/>, or <typeparamref name="T"/> cannot
    /// reach the service behind an out-of-band source (<c>"url"</c>, <c>"kubernetes"</c>) — see
    /// <see cref="IsUnreachable{T}"/> for the one capability that still can.
    /// </exception>
    [AspireExportIgnore(Reason =
        "A generic method projects into ATS with its type parameter dropped, and here T *is* " +
        "the capability being requested, so the export would arrive broken rather than absent. " +
        "Guest-language AppHosts use the non-generic shims in ServiceConfigurationExports " +
        "instead, which delegate here.")]
    public static IResourceBuilder<T> As<T>(this IResourceBuilder<IResourceWithServiceDiscovery> service)
        where T : IResource
    {
        var annotation = service.Resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault();

        // Checked before the cast, not after, because a source can resolve to a resource that
        // *accepts* the configuration while being the wrong thing to configure. A kubernetes-sourced
        // service is an ExecutableResource wrapping `kubectl port-forward`, so it takes environment
        // variables happily — and they would reach kubectl, never the service behind it. Silently
        // configuring the wrong process is exactly the failure mode issue #53 was filed about.
        if (annotation is not null && IsUnreachable<T>(annotation.Source))
        {
            throw new ServiceSourcesConfigurationException(Explain<T>(service.Resource, annotation));
        }

        if (service.Resource is T typed)
        {
            return service.ApplicationBuilder.CreateResourceBuilder(typed);
        }

        throw new ServiceSourcesConfigurationException(Explain<T>(service.Resource, annotation));
    }

    /// <summary>
    /// Sources that resolve to something already running elsewhere, so what the AppHost configures
    /// here is not the service itself. <see cref="Configure{T}"/> skips and logs for these;
    /// <see cref="As{T}"/> throws, because it must return a builder.
    /// </summary>
    private static readonly HashSet<string> OutOfBandSources = new(StringComparer.Ordinal) { "url", "kubernetes" };

    /// <summary>
    /// Whether <typeparamref name="T"/> cannot reach the service behind <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// Keyed on the capability as well as the source, because "runs out of band" and "nothing here
    /// can honour this" are not the same claim. A <c>"kubernetes"</c> service resolves to a real,
    /// registered <c>kubectl port-forward</c> executable: configuration that would reach the
    /// <i>process</i> is wrong, since it lands on kubectl rather than the service behind it, but
    /// start ordering is not — holding the port-forward back until a migration finishes is exactly
    /// what the AppHost asked for, and Aspire honours it. Skipping that too meant a
    /// <c>Configure&lt;IResourceWithWaitSupport&gt;</c> written against a local service silently lost
    /// its ordering when someone switched the service to <c>"kubernetes"</c>.
    /// <para>
    /// Nothing is reachable for <c>"url"</c>: its resource is deliberately never registered (see
    /// <see cref="Sources.UrlSource"/>), so there is no process to order and no configuration to
    /// apply.
    /// </para>
    /// </remarks>
    private static bool IsUnreachable<T>(string source)
        where T : IResource =>
        OutOfBandSources.Contains(source)
        && !(string.Equals(source, "kubernetes", StringComparison.Ordinal) && typeof(T) == typeof(IResourceWithWaitSupport));

    /// <summary>
    /// Names the source as well as the type, because the source is what a developer changes to make
    /// the call apply — and what another developer may have changed to make it stop applying.
    /// </summary>
    private static string Explain<T>(IResource resource, ServiceSourceAnnotation? annotation)
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
                $"The resolved resource is a {resource.GetType().Name}, which does not provide it.",
        };

        return $"Service '{name}' cannot be configured as {typeof(T).Name}: " +
               $"its source is '{annotation.Source}'. {detail}";
    }
}
