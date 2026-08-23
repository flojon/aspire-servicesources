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
/// </remarks>
internal sealed class UrlSource : IServiceSource
{
    public IReadOnlySet<string> RelevantFields { get; } = new HashSet<string> { "url" };

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

            return Task.CompletedTask;
        });
    }

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
        var rawUrl = config.Url ?? metadata.Url?.Url;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' source is 'url' but no 'url' is configured — set it in " +
                "servicesources.local.json or servicesources.yaml's url.url.");
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
