using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Resolves a service reachable at a fixed, pre-known URL.
/// </summary>
/// <remarks>
/// The odd one out: every other source registers its resource, but there is nothing here for Aspire
/// to run. Aspire's <c>ExternalServiceResource</c> would be the natural home, but it is
/// <c>sealed</c> and carries no <see cref="EndpointAnnotation"/>, so it cannot satisfy
/// <see cref="IResourceWithServiceDiscovery"/> (microsoft/aspire#9965, #15961, #15993; tracked here
/// as #72). So this source keeps building the <see cref="EndpointAnnotation"/> by hand, with the
/// <see cref="AllocatedEndpoint"/> set eagerly since DCP will never allocate one, and leaves the
/// resource unregistered.
/// <para>
/// That is issue #58, which stays open for this source as #72: a <b>container</b> consumer of one
/// of these fails inside DCP with
/// <c>"Host endpoint 'x' on resource 'y' should have an associated DCP Service resource already set
/// up"</c>. <see cref="RegisterContainerConsumerCheck"/> catches that case up front and explains it.
/// Host-process consumers work and are unaffected.
/// </para>
/// <para>
/// Registering the resource (#58's option 1) clears that DCP failure but replaces it with a worse
/// one: the consuming container is never created and nothing says why. Delegating to
/// <c>ExternalServiceResource</c> (option 2) is the route that would work, and is what the upstream
/// issue blocks. See <see cref="ServiceUrlResource"/> for both.
/// </para>
/// <para>
/// The other consequence of leaving the resource unregistered is that nothing ever publishes a
/// state for it, so a consumer's <c>WaitFor</c> on one waited for the life of the run (#170). That
/// one is fixed rather than pre-flighted, in two halves: <see cref="ServiceUrlResource"/> declares
/// <see cref="IResourceWithoutLifetime"/>, which Aspire's wait machinery filters on, and
/// <see cref="DropWaitsOnUrlServices"/> removes the now-inert annotation before start, because
/// Aspire also reads it as a dependency and a container consumer fails on that.
/// </para>
/// </remarks>
internal sealed class UrlSource : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var uri = ResolveUrl(serviceName, metadata, config);

        var resource = new ServiceUrlResource(serviceName);
        var endpoint = new EndpointAnnotation(
            ProtocolType.Tcp, uriScheme: uri.Scheme, name: uri.Scheme, transport: "http", port: uri.Port, targetPort: uri.Port)
        {
            TargetHost = uri.Host,
            IsProxied = false,
        };
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(
            endpoint, uri.Host, uri.Port, EndpointBindingMode.SingleAddress, targetPortExpression: null);
        resource.Annotations.Add(endpoint);

        RegisterContainerConsumerCheck(builder);

        return ResolvedService.Tag(builder.CreateResourceBuilder(resource), serviceName, "url");
    }

    /// <summary>
    /// Subscribes (once per builder) a <c>BeforeStartEvent</c> pre-flight that turns the DCP failure
    /// described in this class's remarks into a ServiceSources error naming the actual cause.
    /// Runs before DCP starts anything, so the AppHost fails with an explanation instead of a stack
    /// trace from inside <c>ContainerCreator</c>.
    /// </summary>
    private static void RegisterContainerConsumerCheck(IDistributedApplicationBuilder builder)
        => ContainerConsumerCheckRegistrations.GetValue(builder, static _ => new CheckRegistration())
            .EnsureRegistered(builder);

    /// <summary>
    /// Keyed weakly so a builder isn't kept alive for the process lifetime by this bookkeeping, and
    /// guarded because AddService can run on more than one builder concurrently (xUnit does exactly
    /// that). Same shape as <see cref="LocalKindRegistry"/> and <see cref="LocalCheckoutPrefetch"/>.
    /// </summary>
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, CheckRegistration>
        ContainerConsumerCheckRegistrations = new();

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

                throw new ServiceSourcesConfigurationException(
                    $"Container '{consumer.Name}' references service '{urlService.Name}', whose source is 'url'. " +
                    "A 'url'-sourced service has no resource for Aspire to run, so DCP has no Service object to " +
                    "plumb container-to-host networking through, and the container would fail to start. " +
                    "Reference it from a project or executable instead, or give the service a source that runs " +
                    "locally ('local' or 'container') in servicesources.local.json. " +
                    "Tracked as issue #72; it depends on microsoft/aspire#9965.");
            }

            var warnings = ServiceSourcesWarnings.For(builder);

            DropWaitsOnUrlServices(@event.Model, warnings);

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
    /// Removes every <see cref="WaitAnnotation"/> in the model that waits on a url-sourced service,
    /// after the check above has had its say about references.
    /// </summary>
    /// <remarks>
    /// <see cref="ServiceUrlResource"/> declaring <see cref="IResourceWithoutLifetime"/> is what
    /// makes such a wait resolve instead of hanging (#170), and for a project or executable consumer
    /// that is the whole of it — Aspire's wait machinery filters the annotation out and the resource
    /// starts. The annotation is still <i>there</i>, though, and Aspire reads it in a second place
    /// that has nothing to do with waiting: <c>GetResourceDependenciesAsync</c> counts a wait target
    /// as a dependency of the waiter. For a <b>container</b> consumer that puts the url service back
    /// into the set DCP plumbs container-to-host networking for, and it fails to start for the same
    /// reason a <c>WithReference</c> would — except silently, with no error naming a cause, because
    /// nothing was referenced. Measured: with the annotation left in place the container reaches
    /// <c>FailedToStart</c> and nothing is logged; with it removed it runs.
    /// <para>
    /// Removed for every consumer rather than only containers, so that one rule holds everywhere: a
    /// wait on a url-sourced service is dropped, because there is no lifetime to order against. The
    /// <c>"WaitFor"</c> relationship <c>WaitFor()</c> also records is left alone — it is dashboard
    /// grouping, carries no dependency, and a container consumer of one starts fine with it.
    /// </para>
    /// <para>
    /// Each drop is <b>reported</b>, through the same channel a skipped <c>Configure</c> call goes
    /// through. A <c>WaitFor</c> in <c>Program.cs</c> is configuration like any other, and dropping
    /// it silently is the failure mode issue #53 was filed about: the developer who set
    /// <c>Source=url</c> in their own <c>servicesources.local.json</c> is not usually the one who
    /// wrote the wait, and without a warning the consumer simply starts early with nothing said.
    /// See <see cref="ServiceSourcesWarnings"/>.
    /// </para>
    /// </remarks>
    private static void DropWaitsOnUrlServices(
        DistributedApplicationModel model, ServiceSourcesWarnings warnings)
    {
        foreach (var resource in model.Resources)
        {
            // Materialised before removing: Annotations is the live collection being mutated.
            var waitsOnUrlServices = resource.Annotations
                .OfType<WaitAnnotation>()
                .Where(wait => wait.Resource is ServiceUrlResource)
                .ToArray();

            foreach (var wait in waitsOnUrlServices)
            {
                resource.Annotations.Remove(wait);

                // Aspire writes these itself, one per resource a connection-string expression
                // references, so there is no call in Program.cs for a warning to send anyone to.
                // Reporting them would mean warning a developer about something they did not write
                // — noise of exactly the kind the grouped message exists to avoid. The cost is that
                // a hand-written WaitFor on a connection-string resource goes unreported too.
                if (resource is ConnectionStringResource)
                {
                    continue;
                }

                warnings.AddSkip(wait.Resource.Name, "url", $"{WaitCall(wait.WaitType)} from '{resource.Name}'");
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
    /// a <see cref="ServiceUrlResource"/>, which widens what this pre-flight refuses and belongs
    /// with #72 rather than here. Note that the wait side of the same shape <i>is</i> handled — see
    /// <see cref="DropWaitsOnUrlServices"/> — so a connection string that a container does not
    /// reference is fine.
    /// </para>
    /// </remarks>
    private static ServiceUrlResource? ConsumedUrlService(ContainerResource consumer)
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

            if (consumed is ServiceUrlResource urlService)
            {
                return urlService;
            }
        }

        return null;
    }

    internal static Uri ResolveUrl(string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var rawUrl = config.Url.Url ?? metadata.Url?.Url;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' source is 'url' but no URL is configured — set " +
                "'url.url' in servicesources.local.json or servicesources.yaml.");
        }

        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': 'url' value '{rawUrl}' is not a valid absolute URL.");
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': 'url' value '{rawUrl}' must use the http or https scheme.");
        }

        return uri;
    }
}
