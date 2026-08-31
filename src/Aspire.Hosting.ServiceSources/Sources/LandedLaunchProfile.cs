using System.Text.Json;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The parts of a deferred service's <c>launchSettings.json</c> that only become readable once its
/// checkout has landed — read by <see cref="DeferredCheckout"/> after the clone, to recover what
/// composition could not see.
/// </summary>
/// <remarks>
/// <para>
/// Aspire reads a project's launch profile while composing the AppHost and turns it into endpoint
/// annotations, environment variables and command-line arguments there and then. A deferred service
/// has no repository on disk at that point, so all three come out empty and nothing re-runs the
/// step: <c>ExecutableCreator.PrepareProjectExecutablesAsync</c> runs for every project resource at
/// startup, including one whose executable is withheld.
/// </para>
/// <para>
/// Environment and arguments can be put back, because they are resolved from annotations when the
/// executable is created — which for an explicit-start resource is after the clone. Endpoints cannot:
/// ports are allocated during composition and the spec is frozen. So this type exists to close the
/// gap where it can be closed, and to let the one remaining gap be reported precisely rather than
/// guessed at.
/// </para>
/// </remarks>
internal sealed record LandedLaunchProfile(
    IReadOnlyList<string> ApplicationUrls,
    IReadOnlyDictionary<string, string> EnvironmentVariables)
{
    private static readonly LandedLaunchProfile Empty = new([], new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// Reads the effective launch profile beside <paramref name="projectFile"/>, or an empty result
    /// when there is no <c>launchSettings.json</c>, no <c>"commandName": "Project"</c> profile in it,
    /// or it cannot be parsed.
    /// </summary>
    /// <remarks>
    /// Unreadable is treated as absent throughout. This recovers fidelity that would otherwise be
    /// silently lost, so failing to recover it must leave the run exactly as it would have been
    /// rather than break it — the caller's warning is what makes the shortfall visible.
    /// </remarks>
    public static LandedLaunchProfile Read(string projectFile)
    {
        var settingsPath = Path.Combine(
            Path.GetDirectoryName(projectFile) ?? ".", "Properties", "launchSettings.json");

        if (!File.Exists(settingsPath))
        {
            return Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(
                File.ReadAllBytes(settingsPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (!document.RootElement.TryGetProperty("profiles", out var profiles)
                || profiles.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }

            // The first "Project" profile, which is what Aspire falls back to when nothing names one.
            foreach (var profile in profiles.EnumerateObject())
            {
                if (profile.Value.ValueKind != JsonValueKind.Object
                    || !profile.Value.TryGetProperty("commandName", out var commandName)
                    || commandName.ValueKind != JsonValueKind.String
                    || !string.Equals(commandName.GetString(), "Project", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return new LandedLaunchProfile(
                    ReadApplicationUrls(profile.Value),
                    ReadEnvironmentVariables(profile.Value));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty;
        }

        return Empty;
    }

    /// <remarks>
    /// <c>applicationUrl</c> is a semicolon-separated list, the shape Kestrel's <c>--urls</c> takes.
    /// </remarks>
    private static List<string> ReadApplicationUrls(JsonElement profile)
    {
        if (!profile.TryGetProperty("applicationUrl", out var applicationUrl)
            || applicationUrl.ValueKind != JsonValueKind.String
            || applicationUrl.GetString() is not { Length: > 0 } value)
        {
            return [];
        }

        return [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private static Dictionary<string, string> ReadEnvironmentVariables(JsonElement profile)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!profile.TryGetProperty("environmentVariables", out var environment)
            || environment.ValueKind != JsonValueKind.Object)
        {
            return variables;
        }

        foreach (var variable in environment.EnumerateObject())
        {
            // Only strings. A profile may hold numbers or booleans; an environment variable is text,
            // and inventing a formatting rule for the others is worse than leaving them out.
            if (variable.Value.ValueKind == JsonValueKind.String
                && variable.Value.GetString() is { } value)
            {
                variables[variable.Name] = value;
            }
        }

        return variables;
    }
}
