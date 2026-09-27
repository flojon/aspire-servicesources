using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Messages;
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
    /// reach the service behind an out-of-band source (<c>"url"</c>, <c>"kubernetes"</c>,
    /// <c>"disabled"</c>) — see <see cref="IsUnreachable{T}"/> for the one capability that still can.
    /// </exception>
    [AspireExportIgnore(Reason =
        "A generic method projects into ATS with its type parameter dropped, and here T *is* " +
        "the resource type being requested, so the export would arrive broken rather than absent.")]
    public static IResourceBuilder<T> Unwrap<T>(this IResourceBuilder<IResourceWithServiceDiscovery> service)
        where T : IResource
    {
        // The builder's own captured source, not the ServiceSourceAnnotation: that collection is
        // public and mutable, so stripping the annotation would otherwise un-gate this call.
        var serviceBuilder = service as Sources.ServiceResourceBuilder;
        var source = serviceBuilder?.Source;

        // Read once, up front, so a mismatch message below can name the real type behind the facade
        // rather than the facade itself — service.Resource is unconditionally a ServiceResource, so
        // reporting *its* type on a mismatch would say "ServiceResource" no matter what T was wrong.
        var real = serviceBuilder?.Real?.Resource;

        // Checked before the cast, not after, because a source can resolve to a resource that
        // *accepts* the configuration while being the wrong thing to configure. A kubernetes-sourced
        // service is an ExecutableResource wrapping `kubectl port-forward`, so it takes environment
        // variables happily — and they would reach kubectl, never the service behind it. Silently
        // configuring the wrong process is exactly the failure mode issue #53 was filed about.
        if (source is not null && IsUnreachable<T>(source))
        {
            throw ServiceSourcesConfigurationException.For($"{Explain<T>(service.Resource, source, real)}");
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

        throw ServiceSourcesConfigurationException.For($"{Explain<T>(service.Resource, source, real)}");
    }

    /// <summary>
    /// Configures the resolved resource as <typeparamref name="T"/> when it is one, and skips
    /// <paramref name="configure"/> with a startup warning when it is not, instead of throwing.
    /// </summary>
    /// <remarks>
    /// <see cref="Unwrap{T}(IResourceBuilder{IResourceWithServiceDiscovery})"/> has to throw for an
    /// out-of-band source: it has to return a <c>T</c> builder and there is no such builder to hand
    /// back. This overload has somewhere else to put that case — <paramref name="configure"/> simply
    /// never runs, and <paramref name="service"/> comes back unchanged — the same shape a developer
    /// switching a service to <c>"url"</c>, <c>"kubernetes"</c> or <c>"disabled"</c> in their own
    /// <c>servicesources.local.json</c> already gets from <c>WithEnvironment</c>, <c>WithArgs</c> and
    /// the rest. Prefer this over the type-returning overload whenever the call site does not need to
    /// keep the unwrapped builder afterwards, so that switch does not break a <c>Program.cs</c> it
    /// wasn't meant to.
    /// <para>
    /// A reachable source whose resource is not a <typeparamref name="T"/> skips too, rather than
    /// throwing, as long as another source the catalog entry declares could produce one: switching a
    /// <c>java</c> service to <c>"container"</c> produces exactly that mismatch. When no declared
    /// source could ever produce a <typeparamref name="T"/>, the call is an AppHost mistake that no
    /// source switch will fix, so it throws.
    /// </para>
    /// </remarks>
    /// <returns><paramref name="service"/> itself, so native calls can still be chained after it.</returns>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// No source the service's catalog entry declares could ever resolve to a <typeparamref name="T"/>.
    /// </exception>
    [AspireExportIgnore(Reason =
        "A generic method projects into ATS with its type parameter dropped, and here T *is* " +
        "the resource type being requested, so the export would arrive broken rather than absent.")]
    public static IResourceBuilder<ServiceResource> Unwrap<T>(
        this IResourceBuilder<ServiceResource> service, Action<IResourceBuilder<T>> configure)
        where T : IResource
    {
        ConfigureIfResolvedAs(service, configure);
        return service;
    }

    /// <inheritdoc cref="Unwrap{T}(IResourceBuilder{ServiceResource}, Action{IResourceBuilder{T}})"/>
    /// <remarks>
    /// The same overload for a builder held as the type
    /// <see cref="Unwrap{T}(IResourceBuilder{IResourceWithServiceDiscovery})"/> accepts, so code
    /// holding one is not steered to the throwing form.
    /// </remarks>
    [AspireExportIgnore(Reason =
        "A generic method projects into ATS with its type parameter dropped, and here T *is* " +
        "the resource type being requested, so the export would arrive broken rather than absent.")]
    public static IResourceBuilder<IResourceWithServiceDiscovery> Unwrap<T>(
        this IResourceBuilder<IResourceWithServiceDiscovery> service, Action<IResourceBuilder<T>> configure)
        where T : IResource
    {
        ConfigureIfResolvedAs(service, configure);
        return service;
    }

    private static void ConfigureIfResolvedAs<T>(
        IResourceBuilder<IResourceWithServiceDiscovery> service, Action<IResourceBuilder<T>> configure)
        where T : IResource
    {
        ArgumentNullException.ThrowIfNull(configure);

        // Not a builder AddService() returned, so there is no source to skip on.
        if (service is not Sources.ServiceResourceBuilder serviceBuilder)
        {
            configure(service.Unwrap<T>());
            return;
        }

        if (IsUnreachable<T>(serviceBuilder.Source))
        {
            ServiceSourcesWarnings.For(service.ApplicationBuilder)
                .AddSkip(service.Resource.Name, serviceBuilder.Source, $"Unwrap<{typeof(T).Name}>");
            return;
        }

        var real = serviceBuilder.Real?.Resource;

        if (real is T typed)
        {
            configure(service.ApplicationBuilder.CreateResourceBuilder(typed));
            return;
        }

        // Skipping is only right when a source switch caused the mismatch; if no declared source
        // could ever produce a T, the call is dead for everyone and silently dropping it hides that.
        if (serviceBuilder.DeclaredResolutions is { } declared
            && !declared.Any(d => !IsUnreachable<T>(d.Source) && CouldBe(d.ResourceType, typeof(T))))
        {
            var sources = Raw.Join(", ", declared.Select(d => Raw.Compose(
                $"'{new Name(d.Source)}' ({Raw.Escaped(d.ResourceType.Name)})")));

            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(service.Resource.Name)}': Unwrap<{Raw.Escaped(typeof(T).Name)}> can never "
                + $"apply — none of the sources its catalog entry declares, {sources}, resolves to "
                + $"{Raw.Escaped(WithArticle(typeof(T).Name))}. Remove the call, or unwrap a type one of those "
                + $"sources resolves to.");
        }

        ServiceSourcesWarnings.For(service.ApplicationBuilder).AddNotice(Raw.Compose(
            $"Service '{new Name(service.Resource.Name)}': skipped Unwrap<{Raw.Escaped(typeof(T).Name)}> because "
            + $"{ResolvedAs<T>(serviceBuilder.Source, real ?? service.Resource)}. It runs whenever the service "
            + $"resolves to {Raw.Escaped(WithArticle(typeof(T).Name))}; if it never should, remove the call or "
            + $"unwrap the type it does resolve to."));
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
    /// Nothing is reachable for <c>"url"</c> or <c>"disabled"</c>: neither's resource is ever
    /// registered (see <see cref="Sources.UrlSource"/> and <see cref="Sources.DisabledSource"/>), so
    /// there is no process to order and no configuration to apply.
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
    /// The facade — <c>service.Resource</c> is always a <see cref="ServiceResource"/>, so its type is
    /// only ever named for a resource that is <em>not</em> one of ours (the <c>source is null</c>
    /// branch below).
    /// </param>
    /// <param name="source">
    /// The source the builder was resolved from, or <see langword="null"/> for a builder
    /// <c>AddService()</c> did not return.
    /// </param>
    /// <param name="real">
    /// The actual resource behind the facade, when one exists — named in the mismatch message instead
    /// of <paramref name="resource"/>, which is always a <see cref="ServiceResource"/> and would
    /// otherwise make every mismatch read "is a ServiceResource" regardless of what was actually
    /// requested.
    /// </param>
    private static Raw Explain<T>(IResource resource, string? source, IResource? real)
        where T : IResource
    {
        // The catalog key is caller-controlled and reaches a log through this exception, exactly as it
        // does through the warnings that share the sentence below — Name applies the same escaping
        // those messages compose through, without rendering to a string first and risking a second
        // escape pass here.
        var name = new Name(resource.Name);

        if (source is null)
        {
            return Raw.Compose(
                $"Resource '{name}' ({Raw.Escaped(resource.GetType().Name)}) is not "
                + $"{Raw.Escaped(WithArticle(typeof(T).Name))}.");
        }

        var opening = Raw.Compose($"Service '{name}' cannot be configured as {Raw.Escaped(typeof(T).Name)}");

        // Shared with the warnings rather than re-spelled: the same offer with the precondition
        // dropped is a dead end for a service whose catalog entry declares neither block. Keyed on
        // the same predicate the throw above gates on, so the two cannot disagree about a source.
        if (IsUnreachable<T>(source))
        {
            return Raw.Compose(
                $"{opening}: its source is '{new Name(source)}' — "
                + $"{Raw.Escaped(OutOfBandSourceAdvice.SourceDetail(source))}. "
                + $"{Raw.Escaped(OutOfBandSourceAdvice.ConfigureInsteadClause(source))}, or "
                + $"{Raw.Literal(OutOfBandSourceAdvice.SwitchSource)}.");
        }

        return Raw.Compose($"{opening}: {ResolvedAs<T>(source, real ?? resource)}.");
    }

    /// <summary>
    /// The mismatch clause both <see cref="Unwrap{T}(IResourceBuilder{IResourceWithServiceDiscovery})"/>'s
    /// exception and the delegate overloads' skip warning end in.
    /// </summary>
    private static Raw ResolvedAs<T>(string source, IResource resolved)
        where T : IResource =>
        Raw.Compose(
            $"its source '{new Name(source)}' resolves to {Raw.Escaped(WithArticle(resolved.GetType().Name))}, "
            + $"not {Raw.Escaped(WithArticle(typeof(T).Name))}");

    /// <summary>
    /// Whether a resource declared as <paramref name="declared"/> could, at runtime, be a
    /// <paramref name="requested"/>.
    /// </summary>
    /// <remarks>
    /// Errs towards "could": a subclass of an unsealed class may implement any interface, and a
    /// declared interface (a kind that did not name its type) may be anything.
    /// </remarks>
    private static bool CouldBe(Type declared, Type requested) =>
        requested.IsAssignableFrom(declared)
        || declared.IsAssignableFrom(requested)
        || (requested.IsInterface && !declared.IsSealed)
        || (declared.IsInterface && !requested.IsSealed);

    private static string WithArticle(string typeName) =>
        ("AEIOU".Contains(typeName[0], StringComparison.Ordinal) ? "an " : "a ") + typeName;
}
