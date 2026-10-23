using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Checks the interactive picker's answers before they are written, and finds layers that would hide them.
/// </summary>
internal static class PickerAnswers
{
    /// <returns>The reason the answers are unusable, or <see langword="null"/> when they are fine.</returns>
    public static string? Validate(
        string service,
        string source,
        IReadOnlyDictionary<string, string> fields,
        ServiceDefinition definition)
    {
        // A stale or forged choice must not reach the file; match with the same case rules as the sources dictionary.
        if (!SourceChoices.For(definition).Contains(source, StringComparer.OrdinalIgnoreCase))
        {
            return $"Source '{source}' is not available for service '{service}'.";
        }

        var data = new Dictionary<string, string?> { [$"{service}:source"] = source };

        foreach (var field in SourceFollowUp.FieldsFor(source, definition))
        {
            var hasValue = fields.TryGetValue(field.Key, out var value) && !string.IsNullOrWhiteSpace(value);

            if (!hasValue && field.Required)
            {
                return $"'{field.Key}' ({field.Label}) is required for source '{source}'.";
            }
        }

        foreach (var (key, value) in fields)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                data[$"{service}:{key}"] = value;
            }
        }

        using var root = new ConfigurationRoot([new MemoryConfigurationProvider(
            new MemoryConfigurationSource { InitialData = data })]);

        try
        {
            DeveloperConfigValidator.ValidateAll([root.GetSection(service)], DeveloperConfigShape.Service);
        }
        catch (ServiceSourcesConfigurationException ex)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>
    /// The first layer above the developer-config file that blanks the service's <c>source</c>, if any.
    /// </summary>
    /// <remarks>
    /// The file's layer is always provider 0, inserted by <see cref="DeveloperConfigFileSource"/> as lowest precedence.
    /// </remarks>
    public static string? ShadowingProvider(IConfigurationRoot root, string service)
    {
        var key = $"{DeveloperConfiguration.ServicesKey}:{service}:source";

        foreach (var provider in root.Providers.Skip(1))
        {
            if (provider.TryGet(key, out var value) && string.IsNullOrWhiteSpace(value))
            {
                return provider.GetType().Name;
            }
        }

        return null;
    }
}
