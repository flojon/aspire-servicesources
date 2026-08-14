using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class ClusterSource(IPortAllocator portAllocator) : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var args = BuildPortForwardArgs(serviceName, metadata, config, portAllocator, out var localPort, out var remotePort);

        var executableBuilder = builder
            .AddExecutable($"{serviceName}-portforward", "kubectl", builder.AppHostDirectory, args)
            .WithHttpEndpoint(port: localPort, targetPort: localPort, isProxied: false);

        return ServiceResource.CreateFacade(builder, serviceName, executableBuilder);
    }

    internal static string[] BuildPortForwardArgs(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        IPortAllocator portAllocator,
        out int localPort,
        out int remotePort)
    {
        if (metadata.Cluster is null || string.IsNullOrWhiteSpace(metadata.Cluster.Service))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' source is 'cluster' but servicesources.yaml has no cluster.service entry.");
        }

        if (string.IsNullOrWhiteSpace(config.Context))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': source 'cluster' requires 'context' in servicesources.local.json.");
        }

        remotePort = config.Port ?? metadata.Cluster.Port ?? throw new ServiceSourcesConfigurationException(
            $"Service '{serviceName}': no 'port' configured for source 'cluster' — set it in " +
            "servicesources.local.json or servicesources.yaml's cluster.port.");

        var @namespace = config.Namespace ?? "default";

        localPort = portAllocator.AllocatePort();

        return
        [
            "port-forward",
            $"svc/{metadata.Cluster.Service}",
            $"{localPort}:{remotePort}",
            "--context",
            config.Context,
            "--namespace",
            @namespace,
        ];
    }
}
