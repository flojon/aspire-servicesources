using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Sources;

// Resolves a service already reachable at a fixed, pre-known URL (e.g. behind a Kubernetes
// ingress, or any non-Kubernetes HTTP endpoint). This deliberately does not delegate to
// Aspire's own `AddExternalService`/`ExternalServiceResource`: that type has no
// `EndpointAnnotation` and can't produce an `IResourceWithServiceDiscovery`, by design — see
// https://github.com/microsoft/aspire/pull/9965#issuecomment-3026276843 for the Aspire team's
// own rationale ("endpoints don't fit ergonomically for external addresses where nothing needs
// to be allocated by Aspire/DCP"). That's a reasonable choice for Aspire's own external-service
// story, but this package needs "local"/"cluster"/"url" sources to be interchangeable behind one
// `IServiceSource` contract, so `ServiceResource.CreateFacadeForUri` builds the
// `EndpointAnnotation` by hand instead. Also relevant: microsoft/aspire#15961 and
// microsoft/aspire#15993 (open requests to make `ExternalServiceResource`'s internals
// reusable / extend it with header support) — tracked in this repo's issue #5.
internal sealed class UrlSource : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var uri = ResolveUrl(serviceName, metadata, config);

        return ServiceResource.CreateFacadeForUri(builder, serviceName, uri);
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
