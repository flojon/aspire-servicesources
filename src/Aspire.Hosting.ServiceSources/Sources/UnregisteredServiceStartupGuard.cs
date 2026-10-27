using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Shared <c>BeforeStartEvent</c> pre-flight for every source <see cref="ResolvedService.BridgeUnregistered"/>
/// leaves unregistered — <c>"url"</c> and <c>"disabled"</c>. Drops a consumer's
/// <c>WaitFor</c>/<c>WaitForCompletion</c> on either (#170: nothing publishes a state for an
/// unregistered facade, so the wait would otherwise hang forever), reporting the drop as a skip.
/// </summary>
/// <remarks>
/// The container-reference rewrite stays scoped to <c>"url"</c> alone: DCP fails a container that
/// consumes <c>"url"</c>'s eagerly-set <see cref="AllocatedEndpoint"/>, and <c>"disabled"</c>
/// registers no <see cref="EndpointAnnotation"/> for a container to trip over.
/// <para>
/// The rewrite runs in this handler, so one subscribed earlier, or a later one that adds a consuming
/// annotation afterwards, escapes it. A container consuming a <c>"url"</c> service through an opaque
/// callback alone, or through a connection string built from its endpoint, leaves nothing to match
/// and is not rewritten either.
/// </para>
/// </remarks>
internal static class UnregisteredServiceStartupGuard
{
    /// <summary>Sources whose facade this guard's wait-drop applies to.</summary>
    private static readonly HashSet<string> UnregisteredSources = new(StringComparer.Ordinal) { "url", "disabled" };

    /// <summary>Subscribes (once per builder) the <c>BeforeStartEvent</c> pre-flight below.</summary>
    public static void EnsureRegistered(IDistributedApplicationBuilder builder) =>
        Registrations.GetValue(builder, static _ => new CheckRegistration()).EnsureRegistered(builder);

    /// <summary>Keyed weakly; guarded since <c>AddService</c> can run concurrently (xUnit does).</summary>
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, CheckRegistration> Registrations = new();

    private sealed class CheckRegistration
    {
        private bool _registered;

        public void EnsureRegistered(IDistributedApplicationBuilder builder)
        {
            lock (this)
            {
                if (_registered)
                {
                    return;
                }

                _registered = true;
                Subscribe(builder);
            }
        }
    }

    private static void Subscribe(IDistributedApplicationBuilder builder)
    {
        builder.Eventing.Subscribe<BeforeStartEvent>(async (@event, _) =>
        {
            var logger = (ILogger?)@event.Services.GetService<ILoggerFactory>()?.CreateLogger("Aspire.Hosting.ServiceSources")
                ?? NullLogger.Instance;

            foreach (var consumer in @event.Model.Resources.OfType<ContainerResource>())
            {
                var urlServices = ConsumedUrlServices(consumer);

                if (urlServices.Count > 0)
                {
                    await UrlContainerReferenceRewrite.ApplyAsync(consumer, urlServices, logger, @event.Services)
                        .ConfigureAwait(false);
                }
            }

            var warnings = ServiceSourcesWarnings.For(builder);

            DropWaitsOnUnregisteredServices(@event.Model, warnings);

            // Flushed here since these skips are recorded *during* BeforeStartEvent — the warnings
            // class's own flush handler may have already run, or may not exist yet.
            warnings.Flush(@event.Services);
        });

        // Created eagerly, after the subscription above, so a dropped wait lands in the same grouped
        // message as the service's skipped Configure calls rather than a second one after them.
        _ = ServiceSourcesWarnings.For(builder);
    }

    /// <summary>
    /// Removes every <see cref="WaitAnnotation"/> that waits on an unregistered facade
    /// (<c>"url"</c>/<c>"disabled"</c>), before Aspire's wait machinery evaluates one.
    /// </summary>
    /// <remarks>
    /// Not a shared <c>IResourceWithoutLifetime</c> marker — <see cref="ServiceResource"/> is shared
    /// by every source, so that would also suppress <c>WaitFor</c> on sources that must keep working.
    /// Removed for every consumer, not just containers, and reported through
    /// <see cref="ServiceSourcesWarnings"/> — silently dropping a hand-written <c>WaitFor</c> was
    /// issue #53.
    /// </remarks>
    private static void DropWaitsOnUnregisteredServices(
        DistributedApplicationModel model, ServiceSourcesWarnings warnings)
    {
        foreach (var resource in model.Resources)
        {
            // Materialised before removing: Annotations is the live collection being mutated.
            var waitsOnUnregisteredServices = resource.Annotations
                .OfType<WaitAnnotation>()
                .Select(wait => (wait, source: UnregisteredSourceOf(wait.Resource)))
                .Where(entry => entry.source is not null)
                .ToArray();

            foreach (var (wait, source) in waitsOnUnregisteredServices)
            {
                resource.Annotations.Remove(wait);

                // Mirrors onto the real resource behind a bridged consumer's facade too (#327).
                RealToFacadeRegistry.MirrorRemoval(resource, wait);

                // Aspire adds these itself for a connection-string expression, so there's no
                // Program.cs call to point a warning at.
                if (resource is ConnectionStringResource)
                {
                    continue;
                }

                warnings.AddSkip(wait.Resource.Name, source!, $"{WaitCall(wait.WaitType)} from '{resource.Name}'");
            }
        }
    }

    /// <summary>
    /// The call an AppHost wrote to produce <paramref name="waitType"/>. Aspire's enum names two of
    /// the three differently from the methods that set them, and the warning has to name something
    /// the reader can search <c>Program.cs</c> for.
    /// </summary>
    private static string WaitCall(WaitType waitType) => waitType switch
    {
        WaitType.WaitForCompletion => "WaitForCompletion",
        WaitType.WaitUntilStarted => "WaitForStart",
        _ => "WaitFor",
    };

    /// <summary>
    /// Aspire's relationship type for a resource one depends on, as opposed to the <c>"Parent"</c>
    /// that <c>WithParentRelationship</c> records. Not exposed as a constant by Aspire.
    /// </summary>
    private const string ReferenceRelationship = "Reference";

    /// <summary>
    /// The <c>"url"</c>-sourced services <paramref name="consumer"/> consumes; empty if it consumes none.
    /// </summary>
    /// <remarks>
    /// Matches both shapes Aspire leaves depending on how the AppHost wrote the dependency:
    /// <c>WithReference(service)</c> (<see cref="EndpointReferenceAnnotation"/>) and
    /// <c>WithEnvironment("X", service.GetEndpoint(...))</c> (<see cref="ResourceRelationshipAnnotation"/>,
    /// narrowed to <see cref="ReferenceRelationship"/> so <c>WithParentRelationship</c> isn't failed).
    /// A dependency reaching DCP some other way (an opaque callback alone, a hand-written annotation,
    /// a connection string) isn't caught here, and so is not rewritten.
    /// </remarks>
    private static List<ServiceResource> ConsumedUrlServices(ContainerResource consumer)
    {
        var found = new List<ServiceResource>();

        foreach (var annotation in consumer.Annotations)
        {
            IResource? consumed = annotation switch
            {
                EndpointReferenceAnnotation endpointReference => endpointReference.Resource,
                ResourceRelationshipAnnotation relationship
                    when string.Equals(relationship.Type, ReferenceRelationship, StringComparison.Ordinal)
                    => relationship.Resource,
                _ => null,
            };

            if (consumed is ServiceResource candidate && IsUrlSourcedService(candidate) && !found.Contains(candidate))
            {
                found.Add(candidate);
            }
        }

        return found;
    }

    /// <summary>
    /// Whether <paramref name="resource"/> is the <c>"url"</c>-sourced facade — narrower than
    /// <see cref="UnregisteredSourceOf"/>, since the container check stays scoped to <c>"url"</c> alone.
    /// </summary>
    private static bool IsUrlSourcedService(IResource resource) =>
        resource is ServiceResource
        && resource.Annotations.OfType<ServiceSourceAnnotation>().Any(a => a.Source == "url");

    /// <summary>
    /// <paramref name="resource"/>'s source if it's an unregistered facade, else <see langword="null"/>.
    /// </summary>
    private static string? UnregisteredSourceOf(IResource resource) =>
        resource is ServiceResource
            ? resource.Annotations.OfType<ServiceSourceAnnotation>()
                .Select(a => a.Source)
                .FirstOrDefault(UnregisteredSources.Contains)
            : null;
}
