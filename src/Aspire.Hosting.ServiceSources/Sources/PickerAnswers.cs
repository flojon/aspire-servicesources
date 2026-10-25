using System.Text;
using System.Text.Json.Nodes;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
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

        return CheckSection(service, source, root, definition, appHostDirectory);
    }

    /// <summary>
    /// The same checks over a service's whole entry as it would be written, so fields already in the file count.
    /// </summary>
    /// <returns>The reason the entry would fail the next run, or <see langword="null"/> when it is fine.</returns>
    public static string? ValidateEntry(
        string service, string source, JsonObject entry, ServiceDefinition definition, string appHostDirectory)
    {
        var offered = SourceChoices.For(definition)
            .FirstOrDefault(choice => string.Equals(choice, source, StringComparison.OrdinalIgnoreCase)) ?? source;

        var wrapper = new JsonObject { [service] = entry.DeepClone() };

        using var root = new ConfigurationRoot([new JsonStreamConfigurationProvider(
            new JsonStreamConfigurationSource { Stream = new MemoryStream(Encoding.UTF8.GetBytes(wrapper.ToJsonString())) })]);

        return CheckSection(service, offered, root, definition, appHostDirectory);
    }

    private static string? CheckSection(
        string service, string source, IConfigurationRoot root, ServiceDefinition definition, string appHostDirectory)
    {
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
