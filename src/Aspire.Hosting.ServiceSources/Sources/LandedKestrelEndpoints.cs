using System.Text.Json;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The <c>Kestrel:Endpoints:*:Url</c> values a landed checkout's configuration files declare — the
/// endpoints Aspire would have turned into annotations had the repository been on disk while composing.
/// </summary>
internal static class LandedKestrelEndpoints
{
    private static readonly JsonDocumentOptions ParseOptions =
        new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>
    /// The environment Aspire layers environment-specific settings files for: its own process's,
    /// with <c>ASPNETCORE_ENVIRONMENT</c> ahead of <c>DOTNET_ENVIRONMENT</c> and no default.
    /// </summary>
    public static string? ProcessEnvironment() =>
        Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");

    /// <summary>
    /// Reads the same optional files, in the same order, as Aspire's own Kestrel lookup; the later
    /// file wins per endpoint name, as in configuration. Unreadable files count as absent.
    /// </summary>
    public static IReadOnlyList<string> Read(string projectFile, string? environment)
    {
        var directory = Path.GetDirectoryName(projectFile) ?? ".";
        var name = Path.GetFileName(directory);

        var files = new List<string> { "appsettings.json" };
        if (IsSafeFileNamePart(environment))
        {
            files.Add($"appsettings.{environment}.json");
        }

        files.Add($"{name}.settings.json");
        if (IsSafeFileNamePart(environment))
        {
            files.Add($"{name}.settings.{environment}.json");
        }

        var urls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            Merge(Path.Combine(directory, file), urls);
        }

        return [.. urls.Values];
    }

    // The value comes from the environment, and must not be able to steer the path out of the project directory.
    private static bool IsSafeFileNamePart(string? value) =>
        !string.IsNullOrEmpty(value) && value.IndexOfAny([.. Path.GetInvalidFileNameChars(), '/', '\\']) < 0;

    private static void Merge(string path, Dictionary<string, string> urls)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), ParseOptions);

            if (Child(document.RootElement, "Kestrel") is not { } kestrel
                || Child(kestrel, "Endpoints") is not { } endpoints
                || endpoints.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var endpoint in endpoints.EnumerateObject())
            {
                if (Child(endpoint.Value, "Url") is { ValueKind: JsonValueKind.String } url
                    && url.GetString() is { Length: > 0 } value)
                {
                    urls[endpoint.Name] = value;
                }
            }
        }
        // InvalidOperationException: a lone surrogate or invalid UTF-8 in a string fails GetString.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
        }
    }

    // Configuration keys are case-insensitive, so the JSON property names have to be too.
    private static JsonElement? Child(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }
}
