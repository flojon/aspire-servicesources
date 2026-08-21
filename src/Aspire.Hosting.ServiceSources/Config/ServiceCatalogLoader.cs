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

    private static readonly Dictionary<string, HashSet<string>> KnownNestedProperties = new(StringComparer.Ordinal)
    {
        ["kubernetes"] = new HashSet<string>(StringComparer.Ordinal) { "service", "port" },
        ["url"] = new HashSet<string>(StringComparer.Ordinal) { "url" },
        ["container"] = new HashSet<string>(StringComparer.Ordinal) { "image", "port", "defaultTag" },
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
            // on the well-known top-level fields (e.g. "repositry:") and on fields nested inside a
            // typed block (e.g. "kubernetes: { servicee: ... }"). Catch both here instead: any
            // top-level key that's neither a known ServiceMetadata property nor this service's own
            // kind block is an error, and so is any unknown key nested inside a typed block.
            foreach (var key in rawService.Keys)
            {
                if (!KnownTopLevelProperties.Contains(key) && key != metadata.Kind)
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{name}': unknown property '{key}'. Expected one of: " +
                        "repository, project, defaultRef, kind, kubernetes, url, container, or a block matching the service's kind.");
                }

                if (KnownNestedProperties.TryGetValue(key, out var knownNested) &&
                    rawService[key] is System.Collections.IDictionary nestedBlock)
                {
                    foreach (var nestedKeyObj in nestedBlock.Keys)
                    {
                        var nestedKey = nestedKeyObj?.ToString() ?? "";
                        if (!knownNested.Contains(nestedKey))
                        {
                            throw new ServiceSourcesConfigurationException(
                                $"Service '{name}': unknown property '{nestedKey}' inside '{key}'. Expected one of: " +
                                string.Join(", ", knownNested) + ".");
                        }
                    }
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
