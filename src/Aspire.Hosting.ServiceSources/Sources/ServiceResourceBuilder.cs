using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Re-keys today's <c>ServiceConfigurationExtensions.IsUnreachable&lt;T&gt;</c> table by the
/// concrete annotation type native vocabulary adds, instead of a capability type parameter —
/// <see cref="ServiceResourceBuilder.WithAnnotation{TAnnotation}"/> has no single generic capability
/// parameter to key on any more, since every native method (<c>WithEnvironment</c>, <c>WaitFor</c>,
/// …) calls <c>WithAnnotation</c> directly rather than through one dispatcher.
/// </summary>
internal static class Reachability
{
    /// <summary>
    /// Sources that resolve to something already running elsewhere, so what the AppHost configures
    /// here is not the service itself — shared with
    /// <see cref="ServiceConfigurationExtensions.Unwrap{T}"/>'s own out-of-band check, since both are
    /// the same policy re-expressed at a different key (annotation type here, capability type there).
    /// </summary>
    internal static readonly HashSet<string> OutOfBandSources = new(StringComparer.Ordinal) { "url", "kubernetes" };

    /// <summary>
    /// Annotation types Aspire attaches as decoration alongside a capability call — never something
    /// an AppHost author asked to configure — so they must dual-write silently rather than gate.
    /// <c>WaitFor</c> is the case that surfaced this: it calls both
    /// <c>WithAnnotation(waitAnnotation)</c> <em>and</em> <c>WithRelationship(...)</c> (itself
    /// <c>WithAnnotation(new ResourceRelationshipAnnotation(...))</c>, the dashboard link) for a
    /// single AppHost call, and gating both would report a single reachable <c>WaitFor</c> as two
    /// skips instead of zero. <c>EndpointReferenceAnnotation</c> is the same shape for a reference
    /// read from an endpoint.
    /// </summary>
    /// <remarks>
    /// Deliberately a denylist of known decoration, not an allowlist of known capabilities: an
    /// earlier revision of this table listed the five annotation types this ticket's own vocabulary
    /// adds and treated everything else as reachable by default. That missed every other Aspire
    /// extension method already bound to <see cref="IResourceWithEndpoints"/>/
    /// <see cref="IResourceWithArgs"/> (<c>AsHttp2Service</c>, <c>WithMcpServer</c>,
    /// <c>WithHttpHealthCheck</c>, <c>WithEndpointProxySupport</c>, <c>WithLaunchToolArgs</c>, and any
    /// future one) — those add capability annotations too, just ones this table never named, so they
    /// dual-write onto a <c>"kubernetes"</c> service's real <c>kubectl port-forward</c> process (the
    /// exact wrong-process failure mode <see cref="ServiceConfigurationExtensions.Unwrap{T}"/>'s own
    /// remarks describe) or vanish onto a <c>"url"</c> facade with no real resource and no warning.
    /// Failing closed on anything unrecognized is what <c>Configure&lt;T&gt;</c>'s skip-with-warning
    /// existed to guarantee in the first place.
    /// </remarks>
    private static readonly HashSet<Type> DecorationAnnotationTypes =
    [
        typeof(ResourceRelationshipAnnotation),
        typeof(EndpointReferenceAnnotation),
    ];

    /// <summary>
    /// Whether an annotation of <paramref name="annotationType"/> cannot reach the service behind
    /// <paramref name="source"/> — an exact re-expression of
    /// <c>ServiceConfigurationExtensions.IsUnreachable&lt;T&gt;</c> (design §5): every capability
    /// annotation is unreachable for <c>"url"</c>; only <see cref="WaitAnnotation"/> survives for
    /// <c>"kubernetes"</c>, whose real resource is a genuine <c>kubectl port-forward</c> process
    /// worth ordering against. Only <see cref="DecorationAnnotationTypes"/> is always
    /// reachable — everything else gates, on the assumption that an annotation type this table does
    /// not recognize is configuration, not bookkeeping. Keyed by <see cref="Type"/> identity, not
    /// <c>Type.Name</c>: both entries are public, so there is no accessibility reason (unlike
    /// <see cref="CapabilityLabel"/>'s documented internal-type workaround) to risk a same-named type
    /// from another namespace silently bypassing this fail-closed gate.
    /// </summary>
    public static bool IsUnreachable(Type annotationType, string source) =>
        OutOfBandSources.Contains(source)
        && !DecorationAnnotationTypes.Contains(annotationType)
        && !(string.Equals(source, "kubernetes", StringComparison.Ordinal) && annotationType == typeof(WaitAnnotation));

    /// <summary>
    /// The capability name a skip warning should show for <paramref name="annotationType"/> — the
    /// native method an AppHost author actually called, not the annotation's own (often
    /// callback-suffixed) type name.
    /// </summary>
    /// <remarks>
    /// <c>"EnvironmentAnnotation"</c> is a literal, not <c>nameof</c>: that type
    /// (<c>Aspire.Hosting.ApplicationModel.EnvironmentAnnotation</c>) is <c>internal</c> to
    /// <c>Aspire.Hosting.dll</c>, so this package cannot name it at compile time — but Aspire's own
    /// public <c>WithEnvironment(builder, name, string value)</c> overload constructs one and passes
    /// it straight to <c>WithAnnotation&lt;EnvironmentAnnotation&gt;</c>, so <paramref
    /// name="annotationType"/> is genuinely this type at runtime for the AppHost's most common
    /// <c>WithEnvironment</c> call, and the label must still recognize it. The
    /// <see cref="EndpointAnnotation"/> label names three methods, not one: <c>WithEndpoint</c>,
    /// <c>WithHttpEndpoint</c> and <c>WithHttpsEndpoint</c> (and each one's own binary-compat shims)
    /// all resolve to a lookup against this same annotation type, so a skip triggered by any of them
    /// is the identical capability, reported once under one label rather than three.
    /// </remarks>
    public static string CapabilityLabel(Type annotationType) => annotationType.Name switch
    {
        "EnvironmentAnnotation" or nameof(EnvironmentCallbackAnnotation) => "WithEnvironment",
        nameof(CommandLineArgsCallbackAnnotation) => "WithArgs",
        nameof(EndpointAnnotation) => "WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint",
        nameof(WaitAnnotation) => "WaitFor/WaitForCompletion",
        _ => annotationType.Name,
    };
}

/// <summary>
/// The <see cref="IResourceBuilder{T}"/> an AppHost author actually holds after <c>AddService()</c>.
/// Every native vocabulary method this design buys back (<c>WithEnvironment</c>,
/// <c>WithReference</c>, <c>WithArgs</c>, <c>WithHttpEndpoint</c>/<c>WithHttpsEndpoint</c>,
/// <c>WaitFor</c>, <c>WaitForCompletion</c>) is an Aspire extension method that — for a new
/// annotation — ends in <c>builder.WithAnnotation(...)</c>, so this one override point intercepts
/// all of them (design §2, §3).
/// </summary>
internal sealed class ServiceResourceBuilder(
    IDistributedApplicationBuilder applicationBuilder,
    ServiceResource facade,
    IResourceBuilder<IResource>? real,
    string source)
    : IResourceBuilder<ServiceResource>
{
    public IDistributedApplicationBuilder ApplicationBuilder { get; } = applicationBuilder;

    public ServiceResource Resource { get; } = facade;

    // Exposed so ServiceConfigurationExtensions.Unwrap<T> (Task 8) can reach the real,
    // source-specific resource behind the facade — `Resource` above is always the facade itself,
    // never `real`, so Unwrap<T> cannot recover a kind-specific type through `Resource` alone.
    internal IResourceBuilder<IResource>? Real => real;

    // The closure-captured constructor parameter, not `Resource.Annotations.OfType<ServiceSourceAnnotation>()`:
    // that collection is public and mutable (Clear()/Remove()), so re-deriving source from it — as
    // ServiceSourcesBuilderExtensions.GateEndpointCall once did — lets any code that strips the
    // resource's own bookkeeping annotation silently un-gate every later endpoint call (#334 reopened).
    internal string Source => source;

    public IResourceBuilder<ServiceResource> WithAnnotation<TAnnotation>(
        TAnnotation annotation, ResourceAnnotationMutationBehavior behavior = ResourceAnnotationMutationBehavior.Append)
        where TAnnotation : IResourceAnnotation
    {
        // Unconditional on `real` being non-null: IsUnreachable(_, "url") is already true for every
        // annotation type regardless of whether a real resource exists, so gating this on
        // `real is not null` (as an earlier draft of this design did) would silently apply every
        // annotation to the facade alone for "url", with no warning at all — the opposite of
        // Configure<T>'s preserved skip-with-warning behaviour.
        if (Reachability.IsUnreachable(typeof(TAnnotation), source))
        {
            ServiceSourcesWarnings.For(ApplicationBuilder)
                .AddSkip(Resource.Name, source, Reachability.CapabilityLabel(typeof(TAnnotation)));
            return this;
        }

        // Mirrors Aspire's own DistributedApplicationResourceBuilder<T>.WithAnnotation exactly:
        // Replace removes any existing TAnnotation before adding, Append does not.
        if (behavior == ResourceAnnotationMutationBehavior.Replace
            && Resource.Annotations.OfType<TAnnotation>().SingleOrDefault() is { } existing)
        {
            Resource.Annotations.Remove(existing);
        }

        // The SAME instance goes into both collections — never a copy. This is what keeps a
        // second WithHttpEndpoint() call's mutation-in-place visible through both objects, and what
        // makes an EndpointAnnotation's AllocatedEndpoint — set by DCP against the real, registered
        // object during an actual run — readable through the facade afterwards.
        Resource.Annotations.Add(annotation);

        // real.WithAnnotation (not a direct Annotations.Add) so the real resource's own
        // Replace-or-Append bookkeeping runs too, against its own possible pre-existing TAnnotation.
        real?.WithAnnotation(annotation, behavior);

        return this;
    }

    /// <summary>
    /// Runs <paramref name="call"/> and forwards to <c>real</c> every annotation the call left on the
    /// facade alone. Aspire's callback <c>WithEndpoint</c> overload adds its brand-new
    /// <see cref="EndpointAnnotation"/> straight to <c>Resource.Annotations</c> rather than through
    /// <see cref="WithAnnotation{TAnnotation}"/> — the one method in its whole extension class that
    /// does — so diffing the collection across the call is the only way this bridge can learn about it.
    /// </summary>
    /// <remarks>
    /// The <c>real</c>-side read is deliberately taken <em>after</em> <paramref name="call"/> returns,
    /// never as a second pre-call snapshot: every numeric overload dual-writes through
    /// <see cref="WithAnnotation{TAnnotation}"/> <em>during</em> the call, and a pre-call read would
    /// miss that write, so the instance would look new on the facade, absent from <c>real</c>, and be
    /// added a second time. Reading after is also what makes this a silent no-op if a future Aspire
    /// routes this add branch through <c>WithAnnotation</c> itself.
    /// <para>
    /// The forward goes to <c>real</c> directly rather than back through
    /// <see cref="WithAnnotation{TAnnotation}"/>, because the facade already holds the instance.
    /// </para>
    /// <para>
    /// Deliberately not filtered to <see cref="EndpointAnnotation"/>: the diff observes instances, so
    /// a type filter could only drop something the delegated call added that nobody anticipated —
    /// silently, which is what <see cref="Reachability"/>'s fail-closed denylist exists to avoid.
    /// The reachability re-check below cannot fire today by construction, whatever type the diff
    /// yields: an unreachable source never gets past the caller's gate, and on a reachable one
    /// <see cref="Reachability.IsUnreachable"/> is false for every annotation type. It keeps the
    /// invariant "nothing reaches <c>real</c> without consulting <see cref="Reachability"/>" true of
    /// this path too. A fired re-check warns and leaves the annotation on the facade rather than
    /// removing an object this package did not add, so the divergence is named rather than silent.
    /// </para>
    /// </remarks>
    internal static IResourceBuilder<ServiceResource> ForwardingAnnotationsAddedBy(
        IResourceBuilder<ServiceResource> builder, Func<IResourceBuilder<ServiceResource>> call)
    {
        if (builder is not ServiceResourceBuilder serviceBuilder || serviceBuilder.Real is not { } realBuilder)
        {
            return call();
        }

        var beforeOnFacade = new HashSet<IResourceAnnotation>(
            serviceBuilder.Resource.Annotations, ReferenceEqualityComparer.Instance);

        var result = call();

        var nowOnReal = new HashSet<IResourceAnnotation>(
            realBuilder.Resource.Annotations, ReferenceEqualityComparer.Instance);

        foreach (var annotation in serviceBuilder.Resource.Annotations.ToArray())
        {
            if (beforeOnFacade.Contains(annotation) || nowOnReal.Contains(annotation))
            {
                continue;
            }

            if (Reachability.IsUnreachable(annotation.GetType(), serviceBuilder.Source))
            {
                ServiceSourcesWarnings.For(serviceBuilder.ApplicationBuilder).AddSkip(
                    serviceBuilder.Resource.Name,
                    serviceBuilder.Source,
                    Reachability.CapabilityLabel(annotation.GetType()));
                continue;
            }

            realBuilder.WithAnnotation(annotation);
        }

        return result;
    }

    /// <summary>
    /// Runs <paramref name="call"/> and drops from <c>real</c> every
    /// <see cref="ResourceCommandAnnotation"/> the call removed from the facade alone. Aspire's
    /// <c>WithCommand</c> supersedes a same-named command with a direct
    /// <c>builder.Resource.Annotations.Remove(...)</c> rather than through
    /// <see cref="WithAnnotation{TAnnotation}"/>, while the replacement it adds afterwards
    /// dual-writes — so without this the real resource keeps both, and
    /// <c>ResourceCommandService.ResolveCommandAnnotation</c>'s <c>SingleOrDefault</c> throws the
    /// moment that command is invoked (#371).
    /// </summary>
    /// <remarks>
    /// The mirror is taken <em>after</em> <paramref name="call"/> returns rather than by removing the
    /// superseded annotation up front: Aspire validates its arguments before reaching the lookup, so
    /// a rejected call must leave both collections exactly as it found them.
    /// <para>
    /// Unreachable sources return early, keeping the invariant that nothing mutates <c>real</c>
    /// without consulting <see cref="Reachability"/>. It is not merely bookkeeping here: a
    /// <c>kubernetes</c> facade inherits whatever annotations its real <c>kubectl port-forward</c>
    /// already carried, and the add that follows the removal is skipped with a warning — so
    /// mirroring would strip a real command and put nothing back.
    /// </para>
    /// <para>
    /// Scoped to <see cref="ResourceCommandAnnotation"/>, and to instances the facade held before the
    /// call, which is the whole of what <c>WithCommand</c> can drop. A general removal diff over
    /// every annotation type is deliberately not built here.
    /// </para>
    /// </remarks>
    internal static IResourceBuilder<ServiceResource> MirroringCommandRemovalsBy(
        IResourceBuilder<ServiceResource> builder, Func<IResourceBuilder<ServiceResource>> call)
    {
        if (builder is not ServiceResourceBuilder serviceBuilder
            || serviceBuilder.Real is not { } realBuilder
            || Reachability.IsUnreachable(typeof(ResourceCommandAnnotation), serviceBuilder.Source))
        {
            return call();
        }

        var beforeOnFacade = serviceBuilder.Resource.Annotations.OfType<ResourceCommandAnnotation>().ToArray();

        var result = call();

        if (beforeOnFacade.Length == 0)
        {
            return result;
        }

        var stillOnFacade = new HashSet<IResourceAnnotation>(
            serviceBuilder.Resource.Annotations, ReferenceEqualityComparer.Instance);

        foreach (var annotation in beforeOnFacade)
        {
            if (!stillOnFacade.Contains(annotation))
            {
                realBuilder.Resource.Annotations.Remove(annotation);
            }
        }

        return result;
    }
}
