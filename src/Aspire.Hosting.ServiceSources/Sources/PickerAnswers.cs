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
    /// <summary>
    /// Refuses answers that would fail the next run's <c>AddService</c>, with the message that run would give.
    /// </summary>
    /// <param name="appHostDirectory">What a <c>path.path</c> answer is resolved against, as it is at run time.</param>
    /// <returns>The reason the answers are unusable, or <see langword="null"/> when they are fine.</returns>
    public static string? Validate(
        string service,
        string source,
        IReadOnlyDictionary<string, string> fields,
        ServiceDefinition definition,
        string appHostDirectory)
    {
        // A stale or forged choice must not reach the file; match with the same case rules as the sources dictionary.
        var offered = SourceChoices.For(definition)
            .FirstOrDefault(choice => string.Equals(choice, source, StringComparison.OrdinalIgnoreCase));

        if (offered is null)
        {
            return $"Source '{source}' is not available for service '{service}'.";
        }

        // The follow-up rules and the resolve checks below match on the sources' own spelling.
        source = offered;

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
            var section = root.GetSection(service);

            DeveloperConfigValidator.ValidateAll([section], DeveloperConfigShape.Service);

            var config = section.Get<ServiceDeveloperConfig>() ?? new ServiceDeveloperConfig();

            ThrowIfUnresolvable(service, source, definition, config, appHostDirectory);
        }
        catch (ServiceSourcesConfigurationException ex)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>
    /// The configuration checks each source's <c>Resolve</c> makes, minus anything with side effects.
    /// </summary>
    private static void ThrowIfUnresolvable(
        string service, string source, ServiceDefinition definition, ServiceDeveloperConfig config, string appHostDirectory)
    {
        switch (source)
        {
            case "url":
                UrlSource.ResolveUrl(service, definition, config);
                break;
            case "kubernetes":
                KubernetesSource.Validate(service, definition, config);
                break;
            case "path" when config.Path.Path is { } overridePath:
                PathSource.ResolveOverride(service, overridePath, appHostDirectory);
                break;
        }
    }

    /// <summary>
    /// The first layer above the developer-config file that blanks the service's <c>source</c>, if any.
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
                // A file provider's ToString names its file, which tells the developer where to look; it carries no values.
                return provider.ToString();
            }
        }

        return null;
    }
}
