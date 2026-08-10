using System.Text.Json;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class DeveloperConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static DeveloperConfigFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ServiceSourcesConfigurationException(
                $"Developer config file not found at '{path}'. Expected a 'servicesources.local.json' file (gitignored) in the AppHost project directory.");
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DeveloperConfigFile>(json, Options) ?? new DeveloperConfigFile();
    }
}
