using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceCatalogLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly HashSet<string> KnownTopLevelProperties = new(StringComparer.Ordinal)
    {
        "repository", "project", "defaultRef", "kind", "kubernetes", "url", "container",
    };

    public static ServiceCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service catalog file not found at '{path}'. Expected a 'servicesources.yaml' file in the AppHost project directory.");
        }

        var yaml = File.ReadAllText(path);
        var catalog = Deserializer.Deserialize<ServiceCatalog>(yaml) ?? new ServiceCatalog();
        var raw = Deserializer.Deserialize<RawServiceCatalog>(yaml) ?? new RawServiceCatalog();

        foreach (var (name, metadata) in catalog.Services)
        {
            // YamlDotNet assigns null for an empty `kind:` scalar, overriding the "dotnet" default —
            // normalize before it's used as a dictionary key or compared against raw property names.
            if (string.IsNullOrWhiteSpace(metadata.Kind))
            {
                metadata.Kind = "dotnet";
            }

            if (!raw.Services.TryGetValue(name, out var rawService))
            {
                continue;
            }

            // IgnoreUnmatchedProperties() above is required so a legitimate per-kind block (e.g.
            // "javascript:") doesn't trip the typed pass — but that also silently drops real typos
            // on the well-known top-level fields (e.g. "repositry:"). Catch those here instead: any
            // top-level key that's neither a known ServiceMetadata property nor this service's own
            // kind block is an error, not a silently-ignored no-op. (Typos nested *inside* an
            // existing typed block like `kubernetes:` are a separate, pre-existing concern and out
            // of scope for this fix.)
            foreach (var key in rawService.Keys)
            {
                if (!KnownTopLevelProperties.Contains(key) && key != metadata.Kind)
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{name}': unknown property '{key}'. Expected one of: " +
                        "repository, project, defaultRef, kind, kubernetes, url, container, or a block matching the service's kind.");
                }
            }

            if (rawService.TryGetValue(metadata.Kind, out var kindBlock))
            {
                metadata.KindConfig = kindBlock;
            }
        }

        return catalog;
    }
}
