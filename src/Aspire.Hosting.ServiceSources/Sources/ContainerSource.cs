using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class ContainerSource : IServiceSource
{
    public IReadOnlySet<string> RelevantFields { get; } = new HashSet<string> { "tag" };

    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var (image, tag, port) = ResolveContainerConfig(serviceName, metadata, config);

        var containerBuilder = tag is null
            ? builder.AddContainer(serviceName, image)
            : builder.AddContainer(serviceName, image, tag);

        containerBuilder.WithHttpEndpoint(targetPort: port);

        return ServiceResource.CreateFacade(builder, serviceName, containerBuilder);
    }

    internal static (string Image, string? Tag, int Port) ResolveContainerConfig(
        string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        if (metadata.Container is null || string.IsNullOrWhiteSpace(metadata.Container.Image))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' source is 'container' but servicesources.yaml has no container.image entry.");
        }

        var port = metadata.Container.Port ?? throw new ServiceSourcesConfigurationException(
            $"Service '{serviceName}' source is 'container' but servicesources.yaml has no container.port entry.");

        if (port is < 1 or > 65535)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': container.port value '{port}' is not a valid port (must be between 1 and 65535).");
        }

        var tag = string.IsNullOrWhiteSpace(config.Tag) ? metadata.Container.DefaultTag : config.Tag;

        return (metadata.Container.Image, tag, port);
    }
}
