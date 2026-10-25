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
        var (image, tag, port, scheme) = Validate(serviceName, definition, config);

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

    public Type? DeclaredResourceType(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition) =>
        definition.Container is null ? null : typeof(ServiceContainerResource);

    public bool IsOfferedFor(ServiceDefinition definition) =>
        TryGetImageAndPort(definition, out _, out _) == ContainerConfigProblem.None;

    /// <summary>
    /// Everything <see cref="Resolve"/> can refuse an entry for, before it builds anything: shared with
    /// <see cref="CanResolve"/> so the two cannot drift apart.
    /// </summary>
    private static (string Image, string? Tag, int Port, string Scheme) Validate(
        string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config)
    {
        var (image, tag, port) = ResolveContainerConfig(serviceName, definition, config);

        // Catalog-only, exactly as container.port is: the image decides what it serves on that
        // port, so there is nothing per-developer to override.
        var scheme = EndpointScheme.Resolve(
            serviceName, "container", developerScheme: null, definition.Container?.Scheme, definition.Origin);

        return (image, tag, port, scheme);
    }

    public bool CanResolve(ServiceDefinition definition, ServiceDeveloperConfig config)
    {
        try
        {
            Validate("", definition, config);
            return true;
        }
        catch (ServiceSourcesConfigurationException)
        {
            return false;
        }
    }

    private enum ContainerConfigProblem { None, NoImage, NoPort, PortOutOfRange }

    private static ContainerConfigProblem TryGetImageAndPort(ServiceDefinition definition, out string image, out int port)
    {
        image = "";
        port = 0;

        if (definition.Container is null || string.IsNullOrWhiteSpace(definition.Container.Image))
        {
            return ContainerConfigProblem.NoImage;
        }

        if (definition.Container.Port is not { } declaredPort)
        {
            return ContainerConfigProblem.NoPort;
        }

        if (declaredPort is < 1 or > 65535)
        {
            port = declaredPort;
            return ContainerConfigProblem.PortOutOfRange;
        }

        image = definition.Container.Image;
        port = declaredPort;
        return ContainerConfigProblem.None;
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
