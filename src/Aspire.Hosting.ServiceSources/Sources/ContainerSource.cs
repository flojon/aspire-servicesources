using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class ContainerSource : IServiceSource
{
    public IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition,
        ServiceDeveloperConfig config, RepositoryDeveloperConfig? repositoryConfig = null)
    {
        var (image, tag, port) = ResolveContainerConfig(serviceName, definition, config);

        // Catalog-only, exactly as container.port is: the image decides what it serves on that
        // port, so there is nothing per-developer to override.
        var scheme = EndpointScheme.Resolve(
            serviceName, "container", developerScheme: null, definition.Container?.Scheme, definition.Origin);

        // Built by hand rather than via AddContainer so the resource can be a
        // ServiceContainerResource, which adds the IResourceWithServiceDiscovery that
        // ContainerResource lacks. WithImage/WithImageTag are what AddContainer itself uses.
        var containerBuilder = builder.AddResource(new ServiceContainerResource(serviceName))
            .WithImage(image)
            .WithEndpoint(targetPort: port, scheme: scheme, name: scheme);

        if (tag is not null)
        {
            containerBuilder.WithImageTag(tag);
        }

        return ResolvedService.Bridge(containerBuilder, serviceName, "container");
    }

    internal static (string Image, string? Tag, int Port) ResolveContainerConfig(
        string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config)
    {
        if (definition.Container is null || string.IsNullOrWhiteSpace(definition.Container.Image))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}' source is 'container' but {Raw.Origin(definition.Origin)} has no "
                + $"container.image entry.");
        }

        var port = definition.Container.Port ?? throw ServiceSourcesConfigurationException.For(
            $"Service '{new Name(serviceName)}' source is 'container' but {Raw.Origin(definition.Origin)} has no "
            + $"container.port entry.");

        if (port is < 1 or > 65535)
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': container.port value '{port}' is not a valid port (must be between 1 and 65535).");
        }

        var tag = string.IsNullOrWhiteSpace(config.Container.Tag) ? definition.Container.DefaultTag : config.Container.Tag;

        return (definition.Container.Image, tag, port);
    }
}
