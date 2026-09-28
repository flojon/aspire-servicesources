using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Confirms a configured launch profile exists, so the error names the service, file and available
/// profiles. Aspire's own failure is a generic exception, or none at all when the file is absent.
/// </summary>
internal static class LaunchProfileCheck
{
    internal static void Verify(string serviceName, string projectFile, string? profileName)
    {
        if (profileName is null) return;

        var found = LandedLaunchProfile.ProfileNames(projectFile);

        if (found.State == LaunchSettingsState.Unreadable) return;

        var settingsFile = LandedLaunchProfile.SettingsPath(projectFile);

        if (found.State == LaunchSettingsState.Absent)
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': launch profile '{new Name(profileName)}' was requested but '{Raw.Escaped(settingsFile)}' does not exist. {FixHint}");

        if (found.Names.Contains(profileName, StringComparer.Ordinal)) return;

        var available = found.Names.Count == 0
            ? Raw.Literal("The file declares no profiles.")
            : Raw.Compose($"Profiles in that file: {Raw.Join(", ", found.Names.Select(Raw.Escaped))}.");

        throw ServiceSourcesConfigurationException.For(
            $"Service '{new Name(serviceName)}': launch profile '{new Name(profileName)}' was not found in '{Raw.Escaped(settingsFile)}'. {available} {FixHint}");
    }

    private static Raw FixHint { get; } = Raw.Literal(
        "Fix 'launchProfileName' under the service's 'dotnet' block (or the AsDotnet(o => o.WithLaunchProfileName(...)) call).");
}
