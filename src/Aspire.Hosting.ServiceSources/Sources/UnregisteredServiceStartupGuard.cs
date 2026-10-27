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
/// The rewrite runs in this handler, so a later handler that adds a consuming annotation escapes it.
/// It applies to every container, not just those whose annotations name a <c>"url"</c> service: an
/// opaque callback or a connection string reaches the service without leaving such an annotation.
/// </para>
/// </remarks>
internal static class UnregisteredServiceStartupGuard
{
    /// <summary>Sources whose facade this guard's wait-drop applies to.</summary>
    private static readonly HashSet<string> UnregisteredSources = new(StringComparer.Ordinal) { "url", "disabled" };

    /// <summary>Subscribes (once per builder) the <c>BeforeStartEvent</c> pre-flight below.</summary>
    public static void EnsureRegistered(IDistributedApplicationBuilder builder) =>
        RegistrationFor(builder).EnsureRegistered(builder);

    /// <summary>
    /// Records a <c>"url"</c>-sourced facade for the container rewrite. The facade is never added to the
    /// model, and a consumer reaching it through a callback or connection string leaves no annotation naming it.
    /// </summary>
    public static void TrackUrlService(IDistributedApplicationBuilder builder, ServiceResource facade) =>
        RegistrationFor(builder).Track(facade);

    private static CheckRegistration RegistrationFor(IDistributedApplicationBuilder builder) =>
        Registrations.GetValue(builder, static _ => new CheckRegistration());

    /// <summary>Keyed weakly; guarded since <c>AddService</c> can run concurrently (xUnit does).</summary>
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, CheckRegistration> Registrations = new();

    private sealed class CheckRegistration
    {
        private readonly List<ServiceResource> _urlServices = [];
        private bool _registered;

        public void Track(ServiceResource facade)
        {
            lock (this)
            {
                _urlServices.Add(facade);
            }
        }

        public ServiceResource[] UrlServices()
        {
            lock (this)
            {
                return [.. _urlServices];
            }
        }

        public void EnsureRegistered(IDistributedApplicationBuilder builder)
        {
            lock (this)
            {
                if (_registered)
                {
                    return;
                }

                _registered = true;
                Subscribe(builder, this);
            }
        }
    }

    private static void Subscribe(IDistributedApplicationBuilder builder, CheckRegistration registration)
    {
        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            var logger = (ILogger?)@event.Services.GetService<ILoggerFactory>()?.CreateLogger("Aspire.Hosting.ServiceSources")
                ?? NullLogger.Instance;

            var urlServices = registration.UrlServices();

            if (urlServices.Length > 0)
            {
                foreach (var consumer in @event.Model.Resources.OfType<ContainerResource>())
                {
                    UrlContainerReferenceRewrite.Apply(consumer, urlServices, logger);
                }
            }

            var warnings = ServiceSourcesWarnings.For(builder);

            DropWaitsOnUnregisteredServices(@event.Model, warnings);

            // Flushed here since these skips are recorded *during* BeforeStartEvent — the warnings
            // class's own flush handler may have already run, or may not exist yet.
            warnings.Flush(@event.Services);

            return Task.CompletedTask;
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
    /// <paramref name="resource"/>'s source if it's an unregistered facade, else <see langword="null"/>.
    /// </summary>
    private static string? UnregisteredSourceOf(IResource resource) =>
        resource is ServiceResource
            ? resource.Annotations.OfType<ServiceSourceAnnotation>()
                .Select(a => a.Source)
                .FirstOrDefault(UnregisteredSources.Contains)
            : null;
}
