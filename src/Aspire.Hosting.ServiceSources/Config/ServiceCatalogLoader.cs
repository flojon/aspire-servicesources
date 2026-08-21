using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceCatalogLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // Both sets are derived from the metadata types rather than hand-listed, so a property added to
    // ServiceMetadata (or to one of its nested blocks) can never be accepted by the typed pass while
    // being rejected as "unknown" by the checks in Load below.
    private static readonly HashSet<string> KnownTopLevelProperties = YamlPropertyNames(typeof(ServiceMetadata));

    private static readonly Dictionary<string, HashSet<string>> KnownNestedProperties =
        YamlProperties(typeof(ServiceMetadata))
            .Where(p => IsNestedBlock(p.PropertyType))
            .ToDictionary(
                p => CamelCaseNamingConvention.Instance.Apply(p.Name),
                p => YamlPropertyNames(p.PropertyType),
                StringComparer.Ordinal);

    private static IEnumerable<PropertyInfo> YamlProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<YamlIgnoreAttribute>() is null);

    private static HashSet<string> YamlPropertyNames(Type type) =>
        YamlProperties(type)
            .Select(p => CamelCaseNamingConvention.Instance.Apply(p.Name))
            .ToHashSet(StringComparer.Ordinal);

    // A nested yaml block is a metadata class declared alongside ServiceMetadata; scalar properties
    // (including Nullable<int>, which is a struct) and the untyped kind block are not.
    private static bool IsNestedBlock(Type type) =>
        type.IsClass && type != typeof(string) && type.Namespace == typeof(ServiceMetadata).Namespace;

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
            // A service key with nothing under it deserializes to a null entry — report that by name
            // rather than dereferencing it below.
            if (metadata is null)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{name}': entry is empty. Expected at least a 'repository' property.");
            }

            // YamlDotNet assigns null for an empty `kind:` scalar, overriding the "dotnet" default —
            // normalize before it's used as a dictionary key or compared against raw property names.
            if (string.IsNullOrWhiteSpace(metadata.Kind))
            {
                metadata.Kind = LocalKinds.Dotnet;
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
                        string.Join(", ", KnownTopLevelProperties) + ", or a block matching the service's kind.");
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
