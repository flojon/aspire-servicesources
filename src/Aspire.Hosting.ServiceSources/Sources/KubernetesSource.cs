using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class KubernetesSource(IPortAllocator portAllocator) : IServiceSource
{
    public IReadOnlySet<string> RelevantFields { get; } = new HashSet<string> { "context", "namespace", "port" };

    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var args = BuildPortForwardArgs(serviceName, metadata, config, portAllocator, out var localPort, out var remotePort);

        // Built by hand rather than via AddExecutable so the resource can be a
        // ServiceExecutableResource, which adds the IResourceWithServiceDiscovery that
        // ExecutableResource lacks.
        var executableBuilder = builder
            .AddResource(new ServiceExecutableResource($"{serviceName}-portforward", "kubectl", builder.AppHostDirectory))
            .WithArgs(args)
            .WithHttpEndpoint(port: localPort, targetPort: localPort, isProxied: false);

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
        if (metadata.Kubernetes is null || string.IsNullOrWhiteSpace(metadata.Kubernetes.Service))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' source is 'kubernetes' but servicesources.yaml has no kubernetes.service entry.");
        }

        if (string.IsNullOrWhiteSpace(config.Context))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': source 'kubernetes' requires 'context' in servicesources.local.json.");
        }

        remotePort = config.Port ?? metadata.Kubernetes.Port ?? throw new ServiceSourcesConfigurationException(
            $"Service '{serviceName}': no 'port' configured for source 'kubernetes' — set it in " +
            "servicesources.local.json or servicesources.yaml's kubernetes.port.");

        if (remotePort is < 1 or > 65535)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': port value '{remotePort}' is not a valid port (must be between 1 and 65535).");
        }

        var @namespace = config.Namespace ?? "default";

        localPort = portAllocator.AllocatePort();

        return
        [
            "port-forward",
            $"svc/{metadata.Kubernetes.Service}",
            $"{localPort}:{remotePort}",
            "--context",
            config.Context,
            "--namespace",
            @namespace,
        ];
    }
}
