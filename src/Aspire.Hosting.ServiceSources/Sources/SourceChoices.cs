using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The sources the interactive picker can offer for a service.
/// </summary>
internal static class SourceChoices
{
    // Most useful first; a source missing from the dictionary is simply never offered.
    internal static readonly string[] DisplayOrder = ["repository", "container", "url", "kubernetes", "path", "disabled"];

    public static IReadOnlyList<string> All() => [.. ServiceSourcesBuilderExtensions.Sources.Keys];

    public static IReadOnlyList<string> For(ServiceDefinition definition) =>
        [.. DisplayOrder.Where(name =>
            ServiceSourcesBuilderExtensions.Sources.TryGetValue(name, out var source) && source.IsOfferedFor(definition))];
}
