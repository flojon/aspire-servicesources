using System.Text.Json;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The <c>Kestrel:Endpoints:*:Url</c> values a landed checkout's <c>appsettings.json</c> declares —
/// the endpoints Aspire would have turned into annotations had the repository been on disk while
/// composing.
/// </summary>
internal static class LandedKestrelEndpoints
{
    private static readonly JsonDocumentOptions ParseOptions =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// Reads <c>appsettings.json</c> then <c>appsettings.{environment}.json</c> beside
    /// <paramref name="projectFile"/>; the later file wins per endpoint name, as in configuration.
    /// Unreadable files count as absent.
    /// </summary>
    public static IReadOnlyList<string> Read(string projectFile, LandedLaunchProfile profile)
    {
        var directory = Path.GetDirectoryName(projectFile) ?? ".";
        var environment = EnvironmentName(profile);

        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in new[] { "appsettings.json", $"appsettings.{environment}.json" })
        {
            Merge(Path.Combine(directory, file), urls);
        }

        return [.. urls.Values];
    }

    private static string EnvironmentName(LandedLaunchProfile profile)
    {
        foreach (var key in new[] { "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT" })
        {
            if (profile.EnvironmentVariables.TryGetValue(key, out var value) && value.Length > 0)
            {
                return value;
            }
        }

        return "Production";
    }

    private static void Merge(string path, Dictionary<string, string> urls)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), ParseOptions);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("Kestrel", out var kestrel)
                || kestrel.ValueKind != JsonValueKind.Object
                || !kestrel.TryGetProperty("Endpoints", out var endpoints)
                || endpoints.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var endpoint in endpoints.EnumerateObject())
            {
                if (endpoint.Value.ValueKind == JsonValueKind.Object
                    && endpoint.Value.TryGetProperty("Url", out var url)
                    && url.ValueKind == JsonValueKind.String
                    && url.GetString() is { Length: > 0 } value)
                {
                    urls[endpoint.Name] = value;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }
}
