using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The typed <c>dotnet:</c> block of a service's catalog entry: which <c>launchSettings.json</c>
/// profile the project runs under, or an opt-out of launch profiles altogether.
/// </summary>
internal sealed class DotnetMetadata
{
    public string? LaunchProfileName { get; set; }

    public bool? ExcludeLaunchProfile { get; set; }

    internal static (string? Name, bool Exclude) Resolve(DotnetMetadata? metadata)
    {
        var name = string.IsNullOrWhiteSpace(metadata?.LaunchProfileName) ? null : metadata!.LaunchProfileName;
        return (name, name is null && metadata?.ExcludeLaunchProfile == true);
    }

    internal static void Validate(string serviceName, string kind, DotnetMetadata? metadata)
    {
        if (metadata is null) return;

        if (kind != LocalKinds.Dotnet)
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': a 'dotnet' block only applies to services of kind 'dotnet', but this service's kind is '{new Name(kind)}'. Remove the 'dotnet' block or change the kind.");

        if (metadata.ExcludeLaunchProfile == true && !string.IsNullOrWhiteSpace(metadata.LaunchProfileName))
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': 'launchProfileName' and 'excludeLaunchProfile: true' contradict each other (excluding launch profiles ignores any named profile). Drop one of the two.");
    }
}
