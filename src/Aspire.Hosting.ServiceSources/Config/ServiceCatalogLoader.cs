using System.Reflection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceCatalogLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // All three sets are derived from the metadata types rather than hand-listed, so a property
    // added to ServiceMetadata/RepositoryMetadata (or to one of their nested blocks) can never be
    // accepted by the typed pass while being rejected as "unknown" by the checks in Load below.
    private static readonly HashSet<string> KnownTopLevelProperties = YamlPropertyNames(typeof(ServiceMetadata));

    private static readonly HashSet<string> KnownRepositoryProperties = YamlPropertyNames(typeof(RepositoryMetadata));

    private static readonly HashSet<string> KnownRootProperties = YamlPropertyNames(typeof(ServiceCatalog));

    /// <summary>
    /// A kind whose name matches a well-known <see cref="ServiceMetadata"/> key can't be expressed
    /// in yaml: its options block would be bound as that typed property instead, and validated
    /// against that property's schema. <see cref="Sources.LocalKindRegistry.Register"/> rejects such
    /// names up front so the collision can never reach the loader.
    /// </summary>
    internal static bool IsReservedKindName(string kind) => KnownTopLevelProperties.Contains(kind);

    /// <summary>
    /// Nested-block name (e.g. <c>prepare</c>) to the keys valid inside it — derived from
    /// <see cref="ServiceMetadata"/>'s own nested blocks, and reused unchanged for a repository
    /// entry's blocks too: <see cref="RepositoryMetadata.Prepare"/> is the identical
    /// <see cref="PrepareMetadata"/> type <see cref="ServiceMetadata.Prepare"/> is, so there is
    /// nothing a second, repository-specific derivation would say differently. If a repository ever
    /// grows a nested block <see cref="ServiceMetadata"/> does not have, this needs widening to merge
    /// in <see cref="RepositoryMetadata"/>'s own nested blocks too.
    /// </summary>
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

    public static (ServiceCatalog Catalog, IReadOnlyDictionary<string, RepositoryDefinition> Repositories) Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service catalog file not found at '{path}'. Expected a 'servicesources.yaml' file in the AppHost project directory.");
        }

        var yaml = File.ReadAllText(path);
        var catalog = Deserializer.Deserialize<ServiceCatalog>(yaml) ?? new ServiceCatalog();
        var raw = Deserializer.Deserialize<RawServiceCatalog>(yaml) ?? new RawServiceCatalog();

        // IgnoreUnmatchedProperties() applies to the root document too, so a misspelled 'services:'
        // would otherwise deserialize to an empty catalog and be reported later as "service 'x' was
        // not found" — pointing at the service name rather than at the actual typo.
        var rawRoot = Deserializer.Deserialize<Dictionary<string, object?>>(yaml) ?? [];
        foreach (var rootKey in rawRoot.Keys)
        {
            if (!KnownRootProperties.Contains(rootKey))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Unknown top-level property '{rootKey}' in '{path}'. Expected one of: " +
                    string.Join(", ", KnownRootProperties) + ".");
            }
        }

        // Built before any service is converted, so a service's own repositoryRef can be validated
        // against the finished map, and so two services naming the same ref get the same instance —
        // see ServiceMetadata.ToDefinition.
        var repositories = new Dictionary<string, RepositoryDefinition>(StringComparer.Ordinal);
        foreach (var (name, metadata) in catalog.Repositories)
        {
            // A repository key with nothing under it deserializes to a null entry, same as a service.
            if (metadata is null)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Repository '{name}': entry is empty. Expected at least a 'repository' property.");
            }

            if (raw.Repositories.TryGetValue(name, out var rawRepository))
            {
                foreach (var key in rawRepository.Keys)
                {
                    if (!KnownRepositoryProperties.Contains(key))
                    {
                        throw new ServiceSourcesConfigurationException(
                            $"Repository '{name}': unknown property '{key}'. Expected one of: " +
                            string.Join(", ", KnownRepositoryProperties) + ".");
                    }

                    if (KnownNestedProperties.TryGetValue(key, out var knownNested) &&
                        rawRepository[key] is System.Collections.IDictionary nestedBlock)
                    {
                        foreach (var nestedKeyObj in nestedBlock.Keys)
                        {
                            var nestedKey = nestedKeyObj?.ToString() ?? "";
                            if (!knownNested.Contains(nestedKey))
                            {
                                throw new ServiceSourcesConfigurationException(
                                    $"Repository '{name}': unknown property '{nestedKey}' inside '{key}'. Expected one of: " +
                                    string.Join(", ", knownNested) + ".");
                            }
                        }
                    }
                }
            }

            repositories[name] = new RepositoryDefinition
            {
                Url = metadata.Repository,
                DefaultRef = metadata.DefaultRef,
                Prepare = metadata.Prepare,
                CheckoutName = name,
            };
        }

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
            // Only a non-dotnet kind has an options block to exempt from the checks below. The
            // built-in "dotnet" kind is resolved from the service's top-level repository/project
            // metadata and never reads KindConfig, and LocalKindRegistry.Register refuses to
            // register "dotnet" at all — so a `dotnet:` block is always stray or misspelled.
            // Exempting it would have it silently accepted here and then silently ignored.
            var kindBlockKey = metadata.Kind == LocalKinds.Dotnet ? null : metadata.Kind;

            foreach (var key in rawService.Keys)
            {
                if (!KnownTopLevelProperties.Contains(key) && key != kindBlockKey)
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{name}': unknown property '{key}'. Expected one of: " +
                        string.Join(", ", KnownTopLevelProperties) +
                        (kindBlockKey is null ? "." : ", or a block matching the service's kind."));
                }

                // The service's own kind block is opaque to core — validating it against a typed
                // block that happens to share its name would reject the block's real properties.
                // LocalKindRegistry.Register makes this unreachable for a registered kind; it still
                // matters for an unregistered one, which must fail with "kind is not registered".
                if (key == kindBlockKey)
                {
                    continue;
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

            if (kindBlockKey is not null && rawService.TryGetValue(kindBlockKey, out var kindBlock))
            {
                metadata.KindConfig = kindBlock;
            }

            // repositoryRef joins a repositories: entry, so the fields that entry now owns can't
            // also be set here — repository/defaultRef would be ambiguous with the entry's own, and
            // prepare has moved to the repository entirely (design "prepare moves to the repository").
            if (metadata.RepositoryRef is { } repositoryRef)
            {
                if (!repositories.ContainsKey(repositoryRef))
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{name}': repositoryRef '{repositoryRef}' does not name a repositories entry. Expected one of: " +
                        string.Join(", ", repositories.Keys) + ".");
                }

                if (rawService.ContainsKey("repository") || rawService.ContainsKey("defaultRef"))
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{name}': repositoryRef cannot be combined with 'repository' or 'defaultRef' — " +
                        $"those belong on the repositories entry '{repositoryRef}' instead.");
                }

                if (rawService.ContainsKey("prepare"))
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{name}': repositoryRef cannot be combined with 'prepare' — " +
                        $"move it to the repositories entry '{repositoryRef}' instead.");
                }
            }
        }

        return (catalog, repositories);
    }
}
