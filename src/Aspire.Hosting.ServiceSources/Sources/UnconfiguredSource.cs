using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
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
        IDistributedApplicationBuilder builder, string serviceName)
    {
        var real = builder.AddResource(new UnconfiguredServiceResource(serviceName))
            .WithInitialState(new CustomResourceSnapshot
            {
                ResourceType = "Service (unconfigured)",
                State = new ResourceStateSnapshot(StateText, KnownResourceStateStyles.Info),
                Properties = [new ResourcePropertySnapshot("Reason", Reason(serviceName))],
            });

        return ResolvedService.Bridge(real, serviceName, SourceName);
    }

    // Kept free of file paths and of anything that assumes how the source gets chosen.
    private static string Reason(string serviceName) =>
        Raw.Compose($"No source has been chosen for this service. Set " +
            $"'{Raw.Literal(DeveloperConfiguration.ServicesKey)}:{Raw.Escaped(serviceName)}:source'.").ToString();
}
