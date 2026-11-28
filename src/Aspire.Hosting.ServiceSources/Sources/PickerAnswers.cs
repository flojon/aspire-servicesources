using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Checks the interactive picker's answers before they are written, and finds layers that would hide them.
/// </summary>
internal static class PickerAnswers
{
    /// <summary>
    /// Refuses a service's entry, as it would be written, if the next run's <c>AddService</c> would fail on it,
    /// with the message that run would give. Fields already in the file count.
    /// </summary>
    /// <param name="appHostDirectory">What a <c>path.path</c> answer is resolved against, as it is at run time.</param>
    /// <returns>The reason the entry is unusable, or <see langword="null"/> when it is fine.</returns>
    public static string? Validate(
        string service, string source, JsonObject entry, ServiceDefinition definition, string appHostDirectory)
    {
        // A stale or forged choice must not reach the file; match with the same case rules as the sources dictionary.
        var offered = SourceChoices.For(definition)
            .FirstOrDefault(choice => string.Equals(choice, source, StringComparison.OrdinalIgnoreCase));

        if (offered is null)
        {
            return Raw.Compose($"Source '{Raw.Escaped(source)}' is not available for service '{new Name(service)}'.").ToString();
        }

        var wrapper = new JsonObject { [service] = entry.DeepClone() };

        using var root = new ConfigurationRoot([new JsonStreamConfigurationProvider(
            new JsonStreamConfigurationSource { Stream = new MemoryStream(Encoding.UTF8.GetBytes(wrapper.ToJsonString())) })]);

        try
        {
            var section = root.GetSection(service);

            DeveloperConfigValidator.ValidateAll([section], DeveloperConfigShape.Service);

            var config = section.Get<ServiceDeveloperConfig>() ?? new ServiceDeveloperConfig();

            return ServiceSourcesBuilderExtensions.Sources[offered]
                .Requirements(new SourceInput(service, definition, config, appHostDirectory))
                .Problem?.Message;
        }
        catch (ServiceSourcesConfigurationException ex)
        {
            return ex.Message;
        }
    }

    private static string? FilePath(FileConfigurationProvider file) =>
        file.Source.FileProvider?.GetFileInfo(file.Source.Path ?? string.Empty).PhysicalPath ?? file.Source.Path;

    // Names the file or kind of layer, which tells the developer where to look; it carries no values.
    private static string Describe(IConfigurationProvider provider) => provider switch
    {
        FileConfigurationProvider file => $"the file {FilePath(file)}",
        EnvironmentVariablesConfigurationProvider => "an environment variable",
        CommandLineConfigurationProvider => "a command-line argument",
        _ => $"a configuration source ({provider})",
    };

    /// <summary>
    /// A description of the first layer above the developer-config file that blanks the service's
    /// <c>source</c>, if any.
    /// </summary>
    /// <remarks>
    /// The file's layer is found by identity, not position: the catalog's <c>defaultSource</c> layer is
    /// inserted below it. With no file layer in the chain, every layer is above where the file will be.
    /// </remarks>
    public static string? ShadowingProvider(IConfigurationRoot root, string service)
    {
        var key = $"{DeveloperConfiguration.ServicesKey}:{service}:source";
        var providers = root.Providers.ToList();
        var above = providers.FindIndex(DeveloperConfigFileSource.IsFileLayer) + 1;

        foreach (var provider in providers.Skip(above))
        {
            if (provider.TryGet(key, out var value) && string.IsNullOrWhiteSpace(value))
            {
                return Describe(provider);
            }
        }

        return null;
    }
}
