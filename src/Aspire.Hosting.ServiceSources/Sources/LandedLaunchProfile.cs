using System.Text.Json;
using Aspire.Hosting.ApplicationModel;

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
/// <para>
/// Arguments are the reason profile <em>selection</em> here has to agree with Aspire's rather than
/// merely resemble it. Aspire picks the profile again at start time to build the executable's
/// arguments (<c>ExecutableLaunchRecipe</c> calls <c>GetEffectiveLaunchProfile</c>), by which point
/// the checkout has landed and the real file is readable. Picking a different profile here would
/// hand the process one profile's environment and another's arguments and URLs.
/// </para>
/// </remarks>
internal sealed record LandedLaunchProfile(
    string? Name,
    IReadOnlyList<string> ApplicationUrls,
    IReadOnlyDictionary<string, string> EnvironmentVariables)
{
    private static readonly LandedLaunchProfile Empty =
        new(null, [], new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// The <c>commandName</c> values Aspire will launch, from
    /// <c>LaunchProfileExtensions.s_allowedCommandNames</c>. A profile with no <c>commandName</c> at
    /// all is allowed too, which is why this list is only half the test.
    /// </summary>
    private static readonly string[] AllowedCommandNames = ["Project", "Executable"];

    /// <summary>
    /// Reads the launch profile Aspire will select for <paramref name="resource"/> from the
    /// <c>launchSettings.json</c> beside <paramref name="projectFile"/>, or an empty result when
    /// there is no such file, no profile Aspire would select in it, or it cannot be parsed.
    /// </summary>
    /// <remarks>
    /// Unreadable is treated as absent throughout. This recovers fidelity that would otherwise be
    /// silently lost, so failing to recover it must leave the run exactly as it would have been
    /// rather than break it — the caller's warning is what makes the shortfall visible.
    /// </remarks>
    public static LandedLaunchProfile Read(string projectFile, IResource resource)
    {
        // ExcludeLaunchProfileAnnotation short-circuits every selector in Aspire, and means the
        // profile is deliberately discarded rather than not found. Nothing to restore, and
        // restoring it anyway would defeat the annotation.
        if (resource.Annotations.OfType<ExcludeLaunchProfileAnnotation>().Any())
        {
            return Empty;
        }

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

            if (SelectProfileName(resource, profiles) is not { } name
                || !profiles.TryGetProperty(name, out var profile)
                || profile.ValueKind != JsonValueKind.Object)
            {
                // A named profile that is not in the file leaves Aspire with no effective profile
                // either — GetLaunchProfile returns null and the selection does not fall through to
                // the next selector — so an empty result is the faithful answer, not a near miss.
                return Empty;
            }

            return new LandedLaunchProfile(
                name,
                ReadApplicationUrls(profile),
                ReadEnvironmentVariables(profile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty;
        }
    }

    /// <summary>
    /// The profile name Aspire's <c>SelectLaunchProfileName</c> would return for this resource,
    /// or <see langword="null"/> when it would select none.
    /// </summary>
    /// <remarks>
    /// The three selectors Aspire runs, in its order and with its semantics. Reimplemented rather
    /// than called because they are internal to Aspire.Hosting, and reimplemented faithfully because
    /// the cost of disagreeing is silent: the service would get one profile's environment and
    /// another's arguments, or — for a profile Aspire accepts and a stricter rule here does not —
    /// no environment at all, which is the <c>DOTNET_ENVIRONMENT</c> loss the restore exists to
    /// prevent.
    /// </remarks>
    private static string? SelectProfileName(IResource resource, JsonElement profiles)
    {
        // An explicitly named profile wins outright, and is returned whether or not the file has
        // it — Aspire looks it up afterwards and ends with no profile when it is missing, rather
        // than trying the next selector.
        if (resource.Annotations.OfType<LaunchProfileAnnotation>().LastOrDefault() is { } named)
        {
            return named.LaunchProfileName;
        }

        // The AppHost's own profile name, propagated to its projects by WithProjectDefaults from
        // AppHost:DefaultLaunchProfileName or DOTNET_LAUNCH_PROFILE. Unlike the annotation above it
        // does fall through when the file has no such profile.
        if (resource.Annotations.OfType<DefaultLaunchProfileAnnotation>().LastOrDefault() is { } fallback
            && profiles.TryGetProperty(fallback.LaunchProfileName, out var fallbackProfile)
            && fallbackProfile.ValueKind == JsonValueKind.Object)
        {
            return fallback.LaunchProfileName;
        }

        // Otherwise the first launchable profile in file order, which is the order Aspire sees too:
        // it enumerates the dictionary its deserializer filled from this same JSON.
        foreach (var profile in profiles.EnumerateObject())
        {
            if (profile.Value.ValueKind == JsonValueKind.Object && IsLaunchable(profile.Value))
            {
                return profile.Name;
            }
        }

        return null;
    }

    /// <remarks>
    /// A missing or empty <c>commandName</c> counts as launchable: Aspire tests
    /// <c>string.IsNullOrEmpty</c> before the allow list, and a profile that omits the property
    /// deserializes to a null one.
    /// </remarks>
    private static bool IsLaunchable(JsonElement profile)
    {
        if (!profile.TryGetProperty("commandName", out var commandName)
            || commandName.ValueKind != JsonValueKind.String
            || commandName.GetString() is not { Length: > 0 } value)
        {
            return true;
        }

        return Array.Exists(AllowedCommandNames, name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase));
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
