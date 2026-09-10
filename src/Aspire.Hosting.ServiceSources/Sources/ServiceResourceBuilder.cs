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
    private static readonly HashSet<string> OutOfBandSources = new(StringComparer.Ordinal) { "url", "kubernetes" };

    /// <summary>
    /// Whether an annotation of <paramref name="annotationType"/> cannot reach the service behind
    /// <paramref name="source"/> — an exact re-expression of
    /// <c>ServiceConfigurationExtensions.IsUnreachable&lt;T&gt;</c> (design §5): everything is
    /// unreachable for <c>"url"</c>; only <see cref="WaitAnnotation"/> survives for
    /// <c>"kubernetes"</c>, whose real resource is a genuine <c>kubectl port-forward</c> process
    /// worth ordering against.
    /// </summary>
    public static bool IsUnreachable(Type annotationType, string source) =>
        OutOfBandSources.Contains(source)
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
    /// <c>WithEnvironment</c> call, and the label must still recognize it.
    /// </remarks>
    public static string CapabilityLabel(Type annotationType) => annotationType.Name switch
    {
        "EnvironmentAnnotation" or nameof(EnvironmentCallbackAnnotation) => "WithEnvironment",
        nameof(CommandLineArgsCallbackAnnotation) => "WithArgs",
        nameof(EndpointAnnotation) => "WithHttpEndpoint/WithHttpsEndpoint",
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
}
