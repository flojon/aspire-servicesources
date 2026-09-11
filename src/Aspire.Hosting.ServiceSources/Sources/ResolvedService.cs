using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Sources;

internal static class ResolvedService
{
    /// <summary>
    /// Wraps <paramref name="real"/> — the resource Aspire actually runs — behind a new
    /// <see cref="ServiceResource"/> facade that dual-writes future configuration to both, and
    /// returns a builder over the facade. Used by every source except <c>"url"</c>, which has no
    /// real resource to wrap — see <see cref="BridgeUnregistered"/>.
    /// </summary>
    public static IResourceBuilder<ServiceResource> Bridge<TResource>(
        IResourceBuilder<TResource> real, string serviceName, string source)
        // `class` is what lets IResourceBuilder<T>'s covariance pass `real` through unchanged below.
        where TResource : class, IResourceWithServiceDiscovery
    {
        var facade = new ServiceResource(serviceName);

        // Copies the SAME instances `real` already carries — a container/kubernetes source's own
        // EndpointAnnotation, a "local" project's launch-profile-derived endpoints and environment,
        // whatever a deferred registration added — so GetServiceEndpoint/GetEndpoint and a second
        // WithHttpEndpoint() call see them on the facade exactly as they sat on `real` before the
        // facade existed. Sharing the instance rather than its value is what keeps a later
        // mutation-in-place (WithEndpoint's update branch) visible on both collections.
        foreach (var annotation in real.Resource.Annotations)
        {
            facade.Annotations.Add(annotation);
        }

        var sourceAnnotation = new ServiceSourceAnnotation(serviceName, source);
        facade.Annotations.Add(sourceAnnotation);
        real.WithAnnotation(sourceAnnotation);

        // Subscribed from here, exactly as Tag did: this is the set the report is about — the
        // resources a source produced, and no others.
        ServiceStartupFailureNotices.For(real.ApplicationBuilder);

        // A WaitFor/WaitForCompletion naming the facade this method returns would otherwise target a
        // resource nothing ever publishes a state for (#328).
        ServiceWaitRetargeting.EnsureSubscribed(real.ApplicationBuilder);

        return new ServiceResourceBuilder(real.ApplicationBuilder, facade, real, source);
    }

    /// <summary>
    /// Tags <paramref name="facade"/> alone — no real resource exists to dual-write to. The
    /// <c>"url"</c> source's only case: <see cref="ServiceResourceBuilder"/>'s <c>real</c> is
    /// <see langword="null"/>, so every future annotation is unreachable and skipped with a warning
    /// (design §2.3, §5).
    /// </summary>
    public static IResourceBuilder<ServiceResource> BridgeUnregistered(
        IDistributedApplicationBuilder builder, ServiceResource facade, string serviceName, string source)
    {
        facade.Annotations.Add(new ServiceSourceAnnotation(serviceName, source));

        ServiceStartupFailureNotices.For(builder);

        return new ServiceResourceBuilder(builder, facade, real: null, source);
    }
}
