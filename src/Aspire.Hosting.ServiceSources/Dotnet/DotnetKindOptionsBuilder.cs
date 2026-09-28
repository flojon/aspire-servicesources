using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Dotnet;

/// <summary>
/// A fluent handle over a service's <c>dotnet:</c> options: the typed alternative to the yaml
/// block of the same name.
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class DotnetKindOptionsBuilder
{
    private readonly string _serviceName;
    private readonly DotnetMetadata _options = new();

    internal DotnetKindOptionsBuilder(string serviceName)
    {
        _serviceName = serviceName;
    }

    /// <summary>
    /// Runs the project under this <c>launchSettings.json</c> profile instead of Aspire's default
    /// choice. The name must exist in the project's <c>Properties/launchSettings.json</c>.
    /// </summary>
    public DotnetKindOptionsBuilder WithLaunchProfileName(string launchProfileName)
    {
        if (string.IsNullOrWhiteSpace(launchProfileName))
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(_serviceName)}': {Raw.Literal(nameof(WithLaunchProfileName))} - a launch profile name is required and cannot be empty or whitespace.");
        _options.LaunchProfileName = launchProfileName;
        return this;
    }

    /// <summary>
    /// Runs the project without any launch profile, so no profile-derived endpoints or environment
    /// variables are applied. Contradicts <see cref="WithLaunchProfileName"/>.
    /// </summary>
    public DotnetKindOptionsBuilder ExcludeLaunchProfile()
    {
        _options.ExcludeLaunchProfile = true;
        return this;
    }

    internal DotnetMetadata Build() => _options;
}
