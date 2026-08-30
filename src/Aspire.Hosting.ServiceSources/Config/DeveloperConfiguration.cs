using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// Each developer's source selection, read out of the AppHost's <see cref="IConfiguration"/> rather
/// than from a file of our own. <c>servicesources.local.json</c> is still where a developer
/// normally writes it, but it is now the lowest layer of the standard provider chain, so
/// appsettings, user secrets, environment variables and the command line all override it.
/// </summary>
internal sealed class DeveloperConfiguration
{
    /// <summary>The configuration key the per-service entries live under.</summary>
    public const string ServicesKey = "ServiceSources:Services";

    public const string FileName = "servicesources.local.json";

    public required IReadOnlyDictionary<string, ServiceDeveloperConfig> Services { get; init; }

    /// <summary>Where a developer would normally author this, named by the errors below.</summary>
    public required string FilePath { get; init; }

    public required bool FileFound { get; init; }

    /// <summary>
    /// Registers the file on <paramref name="builder"/>'s configuration and reads the result. The
    /// registration is a side effect of the first read rather than something the AppHost author
    /// arranges, which is what keeps this invisible to them; <see cref="ServiceSourcesConfigCache"/>
    /// calls this once per builder.
    /// </summary>
    public static DeveloperConfiguration ReadFrom(IDistributedApplicationBuilder builder)
    {
        var path = Path.Combine(builder.AppHostDirectory, FileName);
        AddFileSource(builder, path);

        var bound = builder.Configuration.GetSection(ServicesKey).Get<Dictionary<string, ServiceDeveloperConfig>>() ?? [];

        return new DeveloperConfiguration
        {
            // Configuration keys are case-insensitive, so the section's own children are merged
            // that way across providers and the surviving key carries whichever casing a provider
            // happened to use. Binding produces an ordinal dictionary, which would then miss the
            // name AddService asked for — and lose the file's entry to the environment variable
            // that merged with it — so the comparer has to match configuration's, not the CLR's.
            Services = new Dictionary<string, ServiceDeveloperConfig>(bound, StringComparer.OrdinalIgnoreCase),
            FilePath = path,
            FileFound = File.Exists(path),
        };
    }

    /// <summary>
    /// The error for "this service has no source", which is a different problem from the one below
    /// and has to stay distinguishable from it: here the developer has configured services, just
    /// not this one, so the fix is one entry rather than a whole file.
    /// </summary>
    public ServiceSourcesConfigurationException NotConfiguredError(string serviceName) =>
        Services.Count == 0
            ? NothingConfiguredError(serviceName)
            : new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' has no source configured. Set '{ServicesKey}:{serviceName}:source' — "
                + $"add \"{serviceName}\": {{ \"source\": \"...\" }} under \"services\" in '{FilePath}', "
                + $"or set the environment variable {EnvironmentVariableFor(serviceName)}.");

    /// <summary>
    /// A typo in a key, or a file that was never created, yields an empty section rather than a
    /// failure — so "nothing is configured at all" is reported deliberately, naming every source
    /// that was consulted, instead of arriving as a per-service message that sends the developer
    /// looking for a single missing entry.
    /// </summary>
    private ServiceSourcesConfigurationException NothingConfiguredError(string serviceName) =>
        new($"No service sources are configured: '{ServicesKey}' is empty in every configuration source, "
            + $"so no service has a source — including '{serviceName}'. "
            + $"Create '{FilePath}' ({(FileFound ? "found, but it configures no services" : "not found")}) with "
            + $"{{ \"services\": {{ \"{serviceName}\": {{ \"source\": \"...\" }} }} }}, "
            + $"or set the environment variable {EnvironmentVariableFor(serviceName)}. "
            + "Sources consulted: that file, appsettings.json, appsettings.{Environment}.json, user secrets, "
            + "environment variables and command-line arguments.");

    private static string EnvironmentVariableFor(string serviceName) =>
        $"{ServicesKey.Replace(":", "__", StringComparison.Ordinal)}__{serviceName}__Source";

    /// <summary>
    /// Registers <c>servicesources.local.json</c> as the <em>lowest</em>-precedence source in the
    /// AppHost's chain, so every standard provider can override an entry with no file edit.
    /// </summary>
    /// <remarks>
    /// The file's own root is <c>services</c>, too generic a key to occupy at the root of the
    /// AppHost's configuration, so its entries are re-keyed under <c>ServiceSources</c> as they are
    /// handed over — the file's shape on disk is unchanged, which is what keeps a TypeScript
    /// AppHost, with no natural place to author .NET configuration, working exactly as before.
    /// Parsing still goes through the JSON configuration provider; only the key prefix is ours.
    /// </remarks>
    private static void AddFileSource(IDistributedApplicationBuilder builder, string path)
    {
        var file = new ConfigurationBuilder().AddJsonFile(path, optional: true).Build();
        var reRooted = file.AsEnumerable()
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => $"ServiceSources:{entry.Key}", entry => entry.Value);

        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = reRooted });
    }
}
