using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The sources the interactive picker can offer for a service.
/// </summary>
internal static class SourceChoices
{
    // Most useful first; a source missing from the dictionary is simply never offered.
    internal static readonly string[] DisplayOrder = ["repository", "container", "url", "kubernetes", "path", "disabled"];

    public static IReadOnlyList<string> For(ServiceDefinition definition) =>
        For(definition, name => ServiceSourcesBuilderExtensions.Sources.GetValueOrDefault(name));

    internal static IReadOnlyList<string> For(ServiceDefinition definition, Func<string, IServiceSource?> lookup) =>
        [.. DisplayOrder.Where(name => lookup(name) is { } source && SourceProbe.Evaluate(source, EmptyInput(definition)).Offered)];

    /// <summary>What <paramref name="source"/> asks for and why it would refuse <paramref name="definition"/> with no developer config.</summary>
    public static SourceRequirements Requirements(string source, ServiceDefinition definition) =>
        ServiceSourcesBuilderExtensions.Sources.TryGetValue(source, out var found)
            ? SourceProbe.Evaluate(found, EmptyInput(definition))
            : SourceRequirements.Unknown;

    // With an empty config no check reaches the filesystem, and the service name only appears in a message nobody reads.
    private static SourceInput EmptyInput(ServiceDefinition definition) =>
        new("", definition, new ServiceDeveloperConfig(), "");
}
