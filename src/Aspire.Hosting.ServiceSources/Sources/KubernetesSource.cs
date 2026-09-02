using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class KubernetesSource(IPortAllocator portAllocator) : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        // The block is checked first so a missing one is reported as that rather than as a scheme
        // problem, and the scheme ahead of BuildPortForwardArgs, whose last act is to allocate a
        // port: an unsupported scheme is config validation like the rest and shouldn't burn an
        // allocation on its way to throwing.
        var kubernetes = RequireKubernetesBlock(serviceName, metadata);
        var scheme = EndpointScheme.Resolve(serviceName, "kubernetes", config.Kubernetes.Scheme, kubernetes.Scheme);

        var args = BuildPortForwardArgs(serviceName, metadata, config, portAllocator, out var localPort, out _);

        // Built by hand rather than via AddExecutable so the resource can be a
        // ServiceExecutableResource, which adds the IResourceWithServiceDiscovery that
        // ExecutableResource lacks.
        //
        // Named for the service itself, like every other source. Aspire keys service discovery off
        // the resource name, so a suffix here would publish this service's endpoint as
        // "services__orders-portforward__..." and break consumers that resolve it as "orders".
        var executableBuilder = builder
            .AddResource(new ServiceExecutableResource(serviceName, "kubectl", builder.AppHostDirectory))
            .WithArgs(args)
            // Named for the scheme rather than always "http": the tunnel is byte-transparent, so a
            // pod serving TLS is reachable as https://localhost:<localPort> and consumers must be
            // able to ask for it under that name (#160). See EndpointScheme.
            .WithEndpoint(port: localPort, targetPort: localPort, scheme: scheme, name: scheme, isProxied: false);

        return ResolvedService.Tag(executableBuilder, serviceName, "kubernetes");
    }

    internal static string[] BuildPortForwardArgs(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        IPortAllocator portAllocator,
        out int localPort,
        out int remotePort)
    {
        var kubernetes = RequireKubernetesBlock(serviceName, metadata);

        if (string.IsNullOrWhiteSpace(config.Kubernetes.Context))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': source 'kubernetes' requires 'context' in servicesources.local.json.");
        }

        remotePort = config.Kubernetes.Port ?? kubernetes.Port ?? throw new ServiceSourcesConfigurationException(
            $"Service '{serviceName}': no 'port' configured for source 'kubernetes' — set it in " +
            "servicesources.local.json or servicesources.yaml's kubernetes.port.");

        if (remotePort is < 1 or > 65535)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': port value '{remotePort}' is not a valid port (must be between 1 and 65535).");
        }

        var @namespace = config.Kubernetes.Namespace ?? "default";

        localPort = portAllocator.AllocatePort();

        return
        [
            "port-forward",
            $"svc/{kubernetes.Service}",
            $"{localPort}:{remotePort}",
            "--context",
            config.Kubernetes.Context,
            "--namespace",
            @namespace,
        ];
    }

    /// <summary>
    /// The service's <c>kubernetes</c> block, which both the port-forward arguments and the endpoint
    /// scheme are read from, so its absence is the first thing either reports.
    /// </summary>
    private static KubernetesMetadata RequireKubernetesBlock(string serviceName, ServiceMetadata metadata)
    {
        if (metadata.Kubernetes is null || string.IsNullOrWhiteSpace(metadata.Kubernetes.Service))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' source is 'kubernetes' but servicesources.yaml has no kubernetes.service entry.");
        }

        return metadata.Kubernetes;
    }
}
