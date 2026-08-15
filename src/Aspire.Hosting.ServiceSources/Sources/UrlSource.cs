using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Sources;

// Resolves a service reachable at a fixed, pre-known URL. Doesn't delegate to Aspire's
// `ExternalServiceResource` — it has no `EndpointAnnotation`, so it can't satisfy
// `IResourceWithServiceDiscovery` (microsoft/aspire#9965, #15961, #15993; tracked here as #5) —
// so `ServiceResource.CreateFacadeForUri` builds the `EndpointAnnotation` by hand instead.
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
