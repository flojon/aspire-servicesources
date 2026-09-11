using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Redirects a <see cref="WaitAnnotation"/> that targets a <see cref="ServiceResource"/> facade to
/// the real resource behind it, once per builder, at <c>BeforeStartEvent</c>.
/// </summary>
/// <remarks>
/// <see cref="ResolvedService.Bridge"/>/<see cref="ResolvedService.BridgeUnregistered"/> never add the
/// facade to the app model, so nothing ever publishes a state for it: a
/// <c>WaitFor</c>/<c>WaitForCompletion</c> naming it — whether written against the consumer directly
/// (<c>container.WaitFor(service)</c>) or dual-written through another <see cref="ServiceResource"/>
/// (<c>service.WaitForCompletion(otherService)</c>) — sits in <c>Waiting</c> for the life of the run,
/// with no error and no warning (#328). Registered from both bridge methods rather than only from
/// <see cref="UrlSource"/>'s conditional subscription, since this affects every source that has a
/// real resource, not only an AppHost that also happens to use <c>"url"</c>.
/// <para>
/// The real resource behind a facade is found by the <see cref="ServiceSourceAnnotation"/> instance
/// the bridge methods add to both objects — the one correlation that survives the facade never being
/// registered. A <c>"url"</c> facade has no real resource to redirect to; its waits are removed
/// instead by <see cref="UrlSource.DropWaitsOnUrlServices"/>, whose own <c>BeforeStartEvent</c>
/// subscription this runs independently of — whichever of the two sees a given wait first leaves
/// nothing for the other to act on.
/// </para>
/// </remarks>
internal sealed class ServiceWaitRetargeting
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, ServiceWaitRetargeting> Cache = new();

    private readonly object _gate = new();
    private bool _subscribed;

    public static void EnsureSubscribed(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static _ => new ServiceWaitRetargeting()).Subscribe(builder);

    private void Subscribe(IDistributedApplicationBuilder builder)
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;

            builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
            {
                Rewrite(@event.Model);
                return Task.CompletedTask;
            });
        }
    }

    private static void Rewrite(DistributedApplicationModel model)
    {
        var realResourcesBySource = new Dictionary<ServiceSourceAnnotation, IResource>();

        foreach (var resource in model.Resources)
        {
            if (resource.Annotations.OfType<ServiceSourceAnnotation>().FirstOrDefault() is { } sourceAnnotation)
            {
                realResourcesBySource[sourceAnnotation] = resource;
            }
        }

        // Nothing this package resolved is registered on this builder at all — the common case for
        // any AppHost that never called AddService, and for the many BeforeStartEvent publications a
        // test issues before adding one.
        if (realResourcesBySource.Count == 0)
        {
            return;
        }

        foreach (var resource in model.Resources)
        {
            // Materialised before mutating: Annotations is the live collection being changed.
            var facadeTargetedWaits = resource.Annotations
                .OfType<WaitAnnotation>()
                .Where(wait => wait.Resource is ServiceResource)
                .ToArray();

            foreach (var wait in facadeTargetedWaits)
            {
                var facadeSource = wait.Resource.Annotations
                    .OfType<ServiceSourceAnnotation>()
                    .FirstOrDefault();

                if (facadeSource is null || !realResourcesBySource.TryGetValue(facadeSource, out var real))
                {
                    continue;
                }

                resource.Annotations.Remove(wait);
                resource.Annotations.Add(new WaitAnnotation(real, wait.WaitType, wait.ExitCode)
                {
                    WaitBehavior = wait.WaitBehavior,
                });
            }
        }
    }
}
