using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>What <see cref="ContainerSource"/>'s check computed for its build.</summary>
internal readonly record struct ContainerChecked(string Image, string? Tag, int Port, string Scheme);

internal sealed class ContainerSource : ServiceSource<ContainerChecked>, IServiceSource
{
    public override bool IsSwitchRemedy => true;

    // Nothing is asked: the image and port are catalog facts no dialog answer replaces.
    protected override IReadOnlyList<FollowUpField> FieldsFor(ServiceDefinition definition) => [];

    protected override ContainerChecked Check(SourceInput input)
    {
        var (image, tag, port) = ResolveContainerConfig(input.ServiceName, input.Definition, input.Config);

        // Catalog-only, exactly as container.port is: the image decides what it serves on that
        // port, so there is nothing per-developer to override.
        var scheme = EndpointScheme.Resolve(
            input.ServiceName, "container", developerScheme: null, input.Definition.Container?.Scheme, input.Definition.Origin);

        return new ContainerChecked(image, tag, port, scheme);
    }

    protected override IResourceBuilder<ServiceResource> Build(
        IDistributedApplicationBuilder builder, SourceInput input, ContainerChecked checkedValues)
    {
        var (image, tag, port, scheme) = checkedValues;

        // Built by hand rather than via AddContainer so the resource can be a
        // ServiceContainerResource, which adds the IResourceWithServiceDiscovery that
        // ContainerResource lacks. WithImage/WithImageTag are what AddContainer itself uses.
        var containerBuilder = builder.AddResource(new ServiceContainerResource(input.ServiceName))
            .WithImage(image)
            .WithEndpoint(targetPort: port, scheme: scheme, name: scheme);

        if (tag is not null)
        {
            containerBuilder.WithImageTag(tag);
        }

        return ResolvedService.Bridge(containerBuilder, input.ServiceName, "container");
    }

    public Type? DeclaredResourceType(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition) =>
        definition.Container is null ? null : typeof(ServiceContainerResource);

    internal static (string Image, string? Tag, int Port) ResolveContainerConfig(
        string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config)
    {
        if (definition.Container is null || string.IsNullOrWhiteSpace(definition.Container.Image))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}' source is 'container' but {Raw.Origin(definition.Origin)} has no "
                + $"container.image entry.").AsMissingSetting("container:image");
        }

        var port = definition.Container.Port ?? throw ServiceSourcesConfigurationException.For(
            $"Service '{new Name(serviceName)}' source is 'container' but {Raw.Origin(definition.Origin)} has no "
            + $"container.port entry.").AsMissingSetting("container:port");

        if (port is < 1 or > 65535)
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': container.port value '{port}' is not a valid port (must be between 1 and 65535).");
        }

        var tag = string.IsNullOrWhiteSpace(config.Container.Tag) ? definition.Container.DefaultTag : config.Container.Tag;

        return (definition.Container.Image, tag, port);
    }
}
