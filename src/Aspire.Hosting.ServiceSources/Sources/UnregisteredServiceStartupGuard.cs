using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The shared <c>BeforeStartEvent</c> pre-flight for every source <see cref="ResolvedService.BridgeUnregistered"/>
/// leaves with no real, registered resource behind the facade — <c>"url"</c> and <c>"disabled"</c>
/// today. Catches, once per builder, the one hazard both share regardless of what either source
/// exposes: a consumer's <c>WaitFor</c>/<c>WaitForCompletion</c> naming one of them would otherwise
/// wait the life of the run for a resource nothing ever publishes a state for (#170), so the wait is
/// dropped instead — with a warning, through the same channel a skipped <c>Configure</c> call uses.
/// </summary>
/// <remarks>
/// The <b>container-reference</b> refusal <see cref="UrlSource"/>'s remarks describe (#58, #72) stays
/// scoped to <c>"url"</c> alone and is not generalized here: that failure comes from <c>"url"</c>'s
/// eagerly-set <see cref="AllocatedEndpoint"/> confusing DCP's container-to-host networking, and
/// <c>"disabled"</c> registers no <see cref="EndpointAnnotation"/> at all — a container that
/// <c>WithReference</c>s a disabled service finds no endpoints to wire up and simply gets none,
/// hitting neither DCP failure.
/// </remarks>
internal static class UnregisteredServiceStartupGuard
{
    /// <summary>
    /// Sources <see cref="ResolvedService.BridgeUnregistered"/> is used for — the ones whose facade
    /// this guard's wait-drop applies to.
    /// </summary>
    private static readonly HashSet<string> UnregisteredSources = new(StringComparer.Ordinal) { "url", "disabled" };

    /// <summary>
    /// Subscribes (once per builder) the <c>BeforeStartEvent</c> pre-flight below.
    /// </summary>
    public static void EnsureRegistered(IDistributedApplicationBuilder builder) =>
        Registrations.GetValue(builder, static _ => new CheckRegistration()).EnsureRegistered(builder);

    /// <summary>
    /// Keyed weakly so a builder isn't kept alive for the process lifetime by this bookkeeping, and
    /// guarded because <c>AddService</c> can run on more than one builder concurrently (xUnit does
    /// exactly that). Same shape as <see cref="LocalKindRegistry"/> and <see cref="LocalCheckoutPrefetch"/>.
    /// </summary>
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
        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            foreach (var consumer in @event.Model.Resources.OfType<ContainerResource>())
            {
                if (ConsumedUrlService(consumer) is not { } urlService)
                {
                    continue;
                }

                throw ServiceSourcesConfigurationException.For(
                    // consumer.Name is an Aspire resource name, which Aspire's own validator already bounds,
                    // so it is safe as Raw; urlService.Name is a catalog key and goes through Name.
                    $"Container '{Raw.Escaped(consumer.Name)}' references service " +
                    $"'{new Name(urlService.Name)}', whose source is 'url'. " +
                    $"A 'url'-sourced service has no resource for Aspire to run, so DCP has no Service object to " +
                    $"plumb container-to-host networking through, and the container would fail to start. " +
                    $"Reference it from a project or executable instead, or {Raw.Literal(OutOfBandSourceAdvice.SwitchSource)}. " +
                    $"Tracked as issue #72.");
            }

            var warnings = ServiceSourcesWarnings.For(builder);

            DropWaitsOnUnregisteredServices(@event.Model, warnings);

            // Flushed here rather than left to the warnings class's own BeforeStartEvent handler,
            // because these skips are recorded *during* that event: by now that handler has either
            // run already, or — if the call above is what created it — was subscribed too late to
            // run at all, since Aspire snapshots an event's subscription list before dispatching.
            // Flush reports each skip once, so the two paths cannot double-log.
            warnings.Flush(@event.Services);

            return Task.CompletedTask;
        });

        // Created eagerly, and after the subscription above so that its flush handler is registered
        // behind this one. That ordering is what keeps a dropped wait in the *same* grouped message
        // as the service's skipped Configure calls instead of a second one after them.
        _ = ServiceSourcesWarnings.For(builder);
    }

    /// <summary>
    /// Removes every <see cref="WaitAnnotation"/> in the model that waits on a service resolved
    /// through <see cref="ResolvedService.BridgeUnregistered"/> — <c>"url"</c> or <c>"disabled"</c> —
    /// after the container check above has had its say about references.
    /// </summary>
    /// <remarks>
    /// This method — not a shared <c>IResourceWithoutLifetime</c> marker — is what makes such a wait
    /// resolve instead of hanging (#170): <see cref="ServiceResource"/> is shared by every source, so
    /// it cannot declare that marker unconditionally without also suppressing <c>WaitFor</c> on
    /// <c>container</c>/<c>kubernetes</c>/<c>local</c>-sourced services. Removing the annotation here,
    /// before Aspire's wait machinery ever evaluates one, is what a project or executable consumer
    /// needs. The annotation is still <i>there</i> until this runs, though, and Aspire reads it in a
    /// second place that has nothing to do with waiting: <c>GetResourceDependenciesAsync</c> counts a
    /// wait target as a dependency of the waiter. For a <b>container</b> consumer of a <c>"url"</c>
    /// service that puts it back into the set DCP plumbs container-to-host networking for, and it
    /// fails to start for the same reason a <c>WithReference</c> would — except silently, with no
    /// error naming a cause, because nothing was referenced. Measured: with the annotation left in
    /// place the container reaches <c>FailedToStart</c> and nothing is logged; with it removed it
    /// runs. A <c>"disabled"</c> service has no such failure mode to begin with — see this type's own
    /// remarks — but the wait would still hang for the same "nothing publishes a state" reason, so the
    /// removal applies to it identically.
    /// <para>
    /// Removed for every consumer rather than only containers, so that one rule holds everywhere: a
    /// wait on one of these services is dropped, because there is no lifetime to order against. The
    /// <c>"WaitFor"</c> relationship <c>WaitFor()</c> also records is left alone — it is dashboard
    /// grouping, carries no dependency, and a container consumer of one starts fine with it.
    /// </para>
    /// <para>
    /// Each drop is <b>reported</b>, through the same channel a skipped <c>Configure</c> call goes
    /// through. A <c>WaitFor</c> in <c>Program.cs</c> is configuration like any other, and dropping
    /// it silently is the failure mode issue #53 was filed about: the developer who set the service's
    /// source is not usually the one who wrote the wait, and without a warning the consumer simply
    /// starts early with nothing said. See <see cref="ServiceSourcesWarnings"/>.
    /// </para>
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

                // `resource` is what this loop ever sees — the registered resource, which for a
                // bridged consumer is the real one behind a facade that shares this same annotation
                // instance in its own collection (#327).
                RealToFacadeRegistry.MirrorRemoval(resource, wait);

                // Aspire writes these itself, one per resource a connection-string expression
                // references, so there is no call in Program.cs for a warning to send anyone to.
                // Reporting them would mean warning a developer about something they did not write
                // — noise of exactly the kind the grouped message exists to avoid. The cost is that
                // a hand-written WaitFor on a connection-string resource goes unreported too.
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
    /// The <c>"url"</c>-sourced service <paramref name="consumer"/> consumes, or
    /// <see langword="null"/> if it consumes none.
    /// </summary>
    /// <remarks>
    /// Two annotations, because Aspire records the same dependency differently depending on how the
    /// AppHost wrote it. <c>WithReference(service)</c> leaves an
    /// <see cref="EndpointReferenceAnnotation"/>; <c>WithEnvironment("X", service.GetEndpoint("https"))</c>
    /// leaves only a <see cref="ResourceRelationshipAnnotation"/>. Both reach DCP as the same
    /// container-to-host wiring and fail identically, so matching just the first let the second
    /// through to the raw DCP trace this pre-flight exists to replace. Relationships are narrowed to
    /// <see cref="ReferenceRelationship"/> so that <c>WithParentRelationship</c>, which implies no
    /// networking, is not failed.
    /// <para>
    /// Matching on annotation shape bounds what this can catch. A container that consumes the
    /// service in a form leaving neither annotation — <c>WithEnvironment("X",
    /// ReferenceExpression.Create($"{svc.GetEndpoint("https")}"))</c>, or an
    /// <see cref="EnvironmentCallbackAnnotation"/> the AppHost writes itself — still reaches DCP and
    /// still produces the raw trace. Those carry the dependency inside an opaque delegate that would
    /// have to be executed to inspect, so this is a floor on the diagnostics rather than a
    /// guarantee: the common spellings are named, the rest fail as they did before.
    /// </para>
    /// <para>
    /// One gap in that floor is <b>inspectable</b> and still open: a container that reaches the
    /// service through a connection string —
    /// <c>container.WithReference(builder.AddConnectionString("cs",
    /// ReferenceExpression.Create($"{svc.GetEndpoint("https")}")))</c> — leaves a
    /// <c>ConnectionStringReferenceAnnotation</c> pointing at the <c>ConnectionStringResource</c>,
    /// not at the service, so nothing here matches. Measured: the url service is still in the set
    /// <c>GetResourceDependenciesAsync</c> returns for that container, and it still fails the way
    /// #58 describes. Closing it means walking the connection string's expression for an endpoint on
    /// a url-sourced <see cref="ServiceResource"/>, which widens what this pre-flight refuses and
    /// belongs with #72 rather than here. Note that the wait side of the same shape <i>is</i> handled — see
    /// <see cref="DropWaitsOnUnregisteredServices"/> — so a connection string that a container does not
    /// reference is fine.
    /// </para>
    /// </remarks>
    private static ServiceResource? ConsumedUrlService(ContainerResource consumer)
    {
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

            if (consumed is ServiceResource candidate && IsUrlSourcedService(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="resource"/> is the <c>"url"</c>-sourced facade — narrower than
    /// <see cref="UnregisteredSourceOf"/>, since the container-reference refusal above stays scoped to
    /// <c>"url"</c> alone (see this type's own remarks).
    /// </summary>
    private static bool IsUrlSourcedService(IResource resource) =>
        resource is ServiceResource
        && resource.Annotations.OfType<ServiceSourceAnnotation>().Any(a => a.Source == "url");

    /// <summary>
    /// <paramref name="resource"/>'s source, if it is a facade resolved through
    /// <see cref="ResolvedService.BridgeUnregistered"/> — <see langword="null"/> for anything else,
    /// including a facade resolved through <see cref="ResolvedService.Bridge"/>, which has a real
    /// resource for a wait to target.
    /// </summary>
    private static string? UnregisteredSourceOf(IResource resource) =>
        resource is ServiceResource
            ? resource.Annotations.OfType<ServiceSourceAnnotation>()
                .Select(a => a.Source)
                .FirstOrDefault(UnregisteredSources.Contains)
            : null;
}
