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

    /// <summary>The configuration key the per-backing-service entries live under.</summary>
    public const string BackingServicesKey = "ServiceSources:BackingServices";

    public const string FileName = "servicesources.local.json";

    public required IReadOnlyDictionary<string, ServiceDeveloperConfig> Services { get; init; }

    /// <summary>Where a developer would normally author this, named by the errors below.</summary>
    public required string FilePath { get; init; }

    public required bool FileFound { get; init; }

    /// <summary>
    /// A root key of the file that looks like a misspelling of <c>services</c>, when the file has no
    /// <c>services</c> key of its own and nothing is configured anywhere. Read by
    /// <see cref="NothingConfiguredError"/>, which is the one failure it explains.
    /// </summary>
    public required string? NearMissRootKey { get; init; }

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

        var section = builder.Configuration.GetSection(ServicesKey);

        // Before binding, and for every entry rather than only the ones an AddService call reaches:
        // LocalCheckoutPrefetch clones every "local" entry the moment the first local-sourced
        // service is resolved, including entries for services no AddService call ever names, so a
        // malformed one would otherwise pay for a checkout before anything looked at it. The keys
        // are checked as the developer spelled them, ahead of the canonicalization below.
        //
        // Every entry in one call, so that a file still to be moved onto the block shape is
        // reported once rather than a service at a time: checking them in a loop here threw on the
        // first faulted entry, which cost a startup per misconfigured service.
        DeveloperConfigValidator.ValidateAll(section.GetChildren(), DeveloperConfigShape.Service);

        var bound = section.Get<Dictionary<string, ServiceDeveloperConfig>>() ?? [];

        foreach (var config in bound.Values)
        {
            NormalizeBlankToAbsent(config, DeveloperConfigShape.Service);
        }

        var services = CanonicalizeToCatalog(bound, catalogNames);

        return new DeveloperConfiguration
        {
            Services = services,
            FilePath = path,
            FileFound = File.Exists(path),
            // Read only when nothing is configured, which is the only state that reaches the error
            // naming it — so an AppHost that starts never pays for the extra parse.
            NearMissRootKey = services.Count == 0
                ? DeveloperConfigFileSource.NearMissForServicesKey(path)
                : null,
        };
    }

    /// <summary>
    /// The backing-service entries, validated and bound the same way the service entries above are,
    /// and keyed case-insensitively as they arrived.
    /// </summary>
    /// <remarks>
    /// Read on its own rather than as part of <see cref="ReadFrom"/>, because it needs no catalog
    /// and the catalog is a file that may not exist. A backing service is declared by the
    /// <c>AddBackingService</c> call itself — the name in the AppHost's own code is the spelling,
    /// with no second list to reconcile it against — so an AppHost that connects to a database and
    /// adds no source-switched service at all is a complete AppHost, and asking it for a
    /// <c>servicesources.yaml</c> would be asking for an empty file to satisfy a lookup that never
    /// happens. It also keeps the two failures apart: a malformed catalog no longer decides whether
    /// a backing service can be resolved.
    /// <para>
    /// Every entry is checked rather than only the ones an <c>AddBackingService</c> call reaches,
    /// for the same reason the service side checks all of them: a mistake in an entry nothing has
    /// asked for yet is still a mistake, and finding it one startup at a time costs a startup per
    /// entry.
    /// </para>
    /// <para>
    /// Validated against its own shape, since a problem has to be reported against the shape the
    /// key was written under: sharing the service walk would describe a backing service's entry
    /// with a service's list of valid keys.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, BackingServiceDeveloperConfig> ReadBackingServicesFrom(
        IDistributedApplicationBuilder builder)
    {
        DeveloperConfigFileSource.EnsureRegistered(builder);

        var section = builder.Configuration.GetSection(BackingServicesKey);

        DeveloperConfigValidator.ValidateAll(section.GetChildren(), DeveloperConfigShape.BackingService);

        var bound = section.Get<Dictionary<string, BackingServiceDeveloperConfig>>() ?? [];

        foreach (var config in bound.Values)
        {
            NormalizeBlankToAbsent(config, DeveloperConfigShape.BackingService);
        }

        // Re-keyed onto configuration's own comparer: the binder produces an ordinal dictionary,
        // which would miss the name AddBackingService() asked for whenever a provider contributed
        // the key under a different casing than the AppHost's call spells it.
        return new Dictionary<string, BackingServiceDeveloperConfig>(bound, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Maps an empty string field to absent, throughout every block.
    /// </summary>
    /// <remarks>
    /// A higher configuration layer can set a key but has no way to remove one, so emptying it is
    /// the only gesture available for dropping a field the file below set — and an empty
    /// environment variable binds as "" rather than null, which every consumer would read as a
    /// configured value. Nullable numbers reach the same place by a different route: the binder maps
    /// an empty string to null for <c>int?</c> before this runs — so the gesture is the same
    /// everywhere, and only the string fields needed the walk below.
    ///
    /// Empty exactly, not merely blank. A value of one or more spaces is close enough to this
    /// gesture to be someone reaching for it, and far enough to be a typed value that lost its
    /// text, so <see cref="DeveloperConfigValidator"/> refuses it outright and names the
    /// spelling that works. Treating it as absent here instead is what made a whitespace
    /// <c>local.path</c> run the service from its managed checkout without a word.
    /// </remarks>
    private static void NormalizeBlankToAbsent(object config, DeveloperConfigShape shape)
    {
        foreach (var block in shape.Blocks)
        {
            if (block.GetValue(config) is { } instance)
            {
                NormalizeBlockBlankToAbsent(instance);
            }
        }
    }

    /// <summary>
    /// <see cref="NormalizeBlankToAbsent"/> for one block, and for any block nested inside it.
    /// </summary>
    /// <remarks>
    /// Recursive because <c>local.prepare</c> is a block inside a block, and the gesture has to mean
    /// the same thing at every depth: a higher layer blanking <c>local:prepare:mode</c> is dropping
    /// the mode the file below set, exactly as blanking <c>local:path</c> drops the path. A walk that
    /// stopped at the first level would leave that as the empty string, which the mode parse would
    /// then have to treat as a value nobody wrote.
    /// </remarks>
    private static void NormalizeBlockBlankToAbsent(object block)
    {
        foreach (var field in block.GetType().GetProperties())
        {
            if (field.PropertyType == typeof(string))
            {
                if (field.GetValue(block) is string { Length: 0 })
                {
                    field.SetValue(block, null);
                }

                continue;
            }

            if (DeveloperConfigField.BlockFieldsOf(field.PropertyType) is not null
                && field.GetValue(block) is { } nested)
            {
                NormalizeBlockBlankToAbsent(nested);
            }
        }
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
            // all, so neither of them is canonical. Recording the collision as null marks the name
            // as one no entry can be bound to; configuring it is reported below.
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
            if (!catalogSpelling.TryGetValue(name, out var spelling))
            {
                canonical[name] = config;
                continue;
            }

            if (spelling is null)
            {
                throw AmbiguousCatalogSpellingError(name, catalogNames);
            }

            canonical[spelling] = config;
        }

        return canonical;
    }

    /// <summary>
    /// The error for an entry naming a service the catalog spells two ways.
    /// </summary>
    /// <remarks>
    /// The alternative is to leave the entry on its own casing, which silently gives both catalog
    /// services the same source — <see cref="Services"/> compares case-insensitively, so each of
    /// them finds it — while only one of the two is the spelling anything enumerating the catalog
    /// matches on. Neither service can then be configured on its own, and nothing says so. The
    /// catalog is what has to change, so the catalog is what the message names.
    /// </remarks>
    private static ServiceSourcesConfigurationException AmbiguousCatalogSpellingError(
        string configuredName, IEnumerable<string> catalogNames)
    {
        var spellings = catalogNames
            .Where(name => string.Equals(name, configuredName, StringComparison.OrdinalIgnoreCase))
            .Select(name => $"'{name}'");

        return new ServiceSourcesConfigurationException(
            $"Configuration names service '{configuredName}', which 'servicesources.yaml' declares more than "
            + $"once under names differing only by case ({string.Join(", ", spellings)}). Configuration keys are "
            + "case-insensitive, so there is no key that reaches one of them and not the other — rename them in "
            + "'servicesources.yaml' so they differ by more than case.");
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
    /// <remarks>
    /// A near miss on the file's root key stops the message where the question mark is. Everything
    /// the long form goes on to say — which sources were consulted, how to write an entry, the
    /// environment variable that sets one — answers "why is nothing configured?", which the key
    /// just answered. Naming the file and asking the question is the whole of what is left to say.
    /// <para>
    /// Each spelling appears exactly once, which is why the message does not also say that the file
    /// has no <c>services</c> key — true, and implied by the question, but it puts a third
    /// near-identical word in a sentence whose whole work is letting the reader see two of them
    /// differ by one letter.
    /// </para>
    /// </remarks>
    private ServiceSourcesConfigurationException NothingConfiguredError(string serviceName) =>
        new("No service sources are configured: "
            + (NearMissRootKey is not null
                ? $"'{FilePath}' has a top-level key '{NearMissRootKey}'. Did you mean 'services'?"
                : $"'{ServicesKey}' is empty in every configuration source, "
                  + $"so no service has a source — including '{serviceName}'. "
                  + $"Create '{FilePath}' ({(FileFound ? "found, but it configures no services" : "not found")}) with "
                  + $"{{ \"services\": {{ \"{serviceName}\": {{ \"source\": \"...\" }} }} }}, "
                  + $"or set the environment variable {EnvironmentVariableFor(serviceName)}. "
                  + "Sources consulted: that file, appsettings.json, appsettings.{Environment}.json, user secrets, "
                  + "environment variables and command-line arguments."));

    private static string EnvironmentVariableFor(string serviceName) =>
        $"{ServicesKey.Replace(":", "__", StringComparison.Ordinal)}__{serviceName}__Source";
}
