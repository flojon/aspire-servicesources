using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceCatalogLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

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
            if (raw.Services.TryGetValue(name, out var rawService) &&
                rawService.TryGetValue(metadata.Kind, out var kindBlock))
            {
                metadata.KindConfig = kindBlock;
            }
        }

        return catalog;
    }
}
