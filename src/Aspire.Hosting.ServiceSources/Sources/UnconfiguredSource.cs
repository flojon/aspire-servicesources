using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The registered resource standing in for a service nobody chose a source for. No endpoints and no
/// process; <see cref="IResourceWithoutLifetime"/> so a consumer's <c>WaitFor</c> cannot hang on it.
/// </summary>
internal sealed class UnconfiguredServiceResource(string name)
    : Resource(name), IResourceWithServiceDiscovery, IResourceWithoutLifetime;

/// <summary>
/// Resolves a declared service that no configuration layer gave a source. Not an
/// <see cref="IServiceSource"/>: a developer cannot name it as a source, only reach it by omission.
/// </summary>
internal static class UnconfiguredSource
{
    internal const string SourceName = "unconfigured";

    internal const string StateText = "Not configured";

    public static IResourceBuilder<ServiceResource> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition)
    {
        var configure = new UnconfiguredConfigureCommand(
            new Unconfigured(serviceName, definition), UnconfiguredRegistry.CreateWriter(builder.AppHostDirectory),
            builder.AppHostDirectory);

        var real = builder.AddResource(new UnconfiguredServiceResource(serviceName))
            .WithCommand(
                UnconfiguredConfigureCommand.Name,
                UnconfiguredConfigureCommand.DisplayName,
                configure.ExecuteAsync,
                new CommandOptions
                {
                    Description = "Choose a source for this service; it takes effect the next time the AppHost starts.",
                    IconName = "Settings",
                    IsHighlighted = true,
                })
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Service (unconfigured)",
                State = new ResourceStateSnapshot(StateText, KnownResourceStateStyles.Info),
                Properties = [new ResourcePropertySnapshot("Reason", Reason(serviceName))],
            });

        // After AddResource, so a duplicate name that throws there is never asked about twice.
        UnconfiguredRegistry.For(builder).Record(builder, serviceName, definition);

        return ResolvedService.Bridge(real, serviceName, SourceName);
    }

    // Names the command and the key, never a file path.
    private static string Reason(string serviceName) =>
        Raw.Compose($"No source has been chosen for this service. Use Configure on this row, or set " +
            $"'{Raw.Literal(DeveloperConfiguration.ServicesKey)}:{Raw.Escaped(serviceName)}:source'.").ToString();
}
