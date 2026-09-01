using Microsoft.Extensions.Configuration;

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
    /// Reads the developer's selection out of <paramref name="builder"/>'s configuration. Whichever
    /// entry point the AppHost called first has already put <c>servicesources.local.json</c> into
    /// that chain; the call below covers the internal paths that reach a read without one, and is a
    /// no-op once the file is registered.
    /// </summary>
    /// <param name="catalogNames">
    /// The service names <c>servicesources.yaml</c> declares, which decide the spelling the entries
    /// are keyed by — see <see cref="CanonicalizeToCatalog"/>.
    /// </param>
    public static DeveloperConfiguration ReadFrom(
        IDistributedApplicationBuilder builder, IEnumerable<string> catalogNames)
    {
        DeveloperConfigFileSource.EnsureRegistered(builder);

        var path = Path.Combine(builder.AppHostDirectory, FileName);

        var bound = builder.Configuration.GetSection(ServicesKey).Get<Dictionary<string, ServiceDeveloperConfig>>() ?? [];

        return new DeveloperConfiguration
        {
            Services = CanonicalizeToCatalog(bound, catalogNames),
            FilePath = path,
            FileFound = File.Exists(path),
        };
    }

    /// <summary>
    /// Re-keys the bound entries to the spelling the catalog uses, and keeps the result comparing
    /// keys the way configuration does rather than the way the CLR does.
    /// </summary>
    /// <remarks>
    /// Configuration keys are case-insensitive, so the section's own children are merged that way
    /// across providers and the surviving key carries whichever casing a provider happened to use —
    /// an environment variable naming <c>Orders</c> yields that key even though the catalog says
    /// <c>orders</c>. Binding produces an ordinal dictionary, which would then miss the name
    /// <c>AddService()</c> asked for; the comparer alone fixes the lookup but not the key, and the
    /// key is what anything enumerating these entries against the catalog matches on.
    /// <see cref="Sources.LocalCheckoutPrefetch"/> is that: an entry left on the provider's casing
    /// is dropped from the parallel prefetch, and the cold clone it would have started serializes
    /// on the <c>AddService()</c> thread instead. The catalog is the authority on how a service is
    /// spelled, so the entries move onto its spelling once, here, rather than every consumer having
    /// to know which comparer its keys arrived under.
    /// </remarks>
    private static Dictionary<string, ServiceDeveloperConfig> CanonicalizeToCatalog(
        Dictionary<string, ServiceDeveloperConfig> bound, IEnumerable<string> catalogNames)
    {
        var catalogSpelling = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in catalogNames)
        {
            // Two catalog names differing only by case cannot be told apart through configuration at
            // all, so neither of them is canonical. Recording the collision as null leaves such an
            // entry on its own casing instead of binding it to whichever name was enumerated first.
            if (!catalogSpelling.TryAdd(name, name))
            {
                catalogSpelling[name] = null;
            }
        }

        // Still case-insensitive after the re-keying: an entry naming a service the catalog doesn't
        // describe has no spelling to adopt, and looking one up has to keep working so that the
        // failure comes from the catalog lookup, which can say so, rather than from a miss here.
        var canonical = new Dictionary<string, ServiceDeveloperConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, config) in bound)
        {
            canonical[catalogSpelling.GetValueOrDefault(name) ?? name] = config;
        }

        return canonical;
    }

    /// <summary>
    /// The error for "this service has no source", which is a different problem from the one below
    /// and has to stay distinguishable from it: here the developer has configured services, just
    /// not this one, so the fix is one entry rather than a whole file.
    /// </summary>
    /// <remarks>
    /// Configured is not the same as configured in a file — CI pins every service from the
    /// environment and ships no file at all — so the advice branches on <see cref="FileFound"/>
    /// rather than sending the developer to edit a path that holds nothing.
    /// </remarks>
    public ServiceSourcesConfigurationException NotConfiguredError(string serviceName) =>
        Services.Count == 0
            ? NothingConfiguredError(serviceName)
            : new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' has no source configured. Set '{ServicesKey}:{serviceName}:source' — "
                + (FileFound
                    ? $"add \"{serviceName}\": {{ \"source\": \"...\" }} under \"services\" in '{FilePath}', "
                    : $"create '{FilePath}' with {{ \"services\": {{ \"{serviceName}\": {{ \"source\": \"...\" }} }} }}, ")
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
}
