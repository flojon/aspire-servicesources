using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceSourcesConfigCache
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, ConfigLoader<LoadedConfig>> Cache = new();

    /// <summary>
    /// The backing-service entries, cached apart from <see cref="Cache"/> because they are loaded
    /// apart from it — see <see cref="DeveloperConfiguration.ReadBackingServicesFrom"/>.
    /// </summary>
    private static readonly ConditionalWeakTable<
        IDistributedApplicationBuilder,
        ConfigLoader<IReadOnlyDictionary<string, BackingServiceDeveloperConfig>>> BackingServiceCache = new();

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, CodeCatalogAccumulator> CodeCatalogs = new();

    /// <summary>
    /// The code-declared catalog accumulator for one AppHost builder, created on first
    /// <c>AddServiceCatalog</c> call. <see cref="CodeCatalogAccumulator.Freeze"/> is called by
    /// <see cref="LoadedConfig.Load"/>, under the same <see cref="CodeCatalogAccumulator"/> lock
    /// <see cref="CodeCatalogAccumulator.Configure"/> takes.
    /// </summary>
    internal static CodeCatalogAccumulator CodeCatalogFor(IDistributedApplicationBuilder builder) =>
        CodeCatalogs.GetValue(builder, static _ => new CodeCatalogAccumulator());

    internal sealed class CodeCatalogAccumulator
    {
        // Plain object rather than System.Threading.Lock: this package still targets net8.0.
        private readonly object _gate = new();

        private bool _frozen;

        public ServiceCatalogBuilder Builder { get; } = new();

        public void Configure(Action<ServiceCatalogBuilder> configure)
        {
            lock (_gate)
            {
                // Raised here rather than left to Builder.AddService's own frozen check, both
                // because a call that configures nothing (an empty lambda, or one that only
                // reconfigures an already-declared service and throws its own error first) must
                // still be caught, and because this is a different failure from "you mutated a
                // frozen builder" — the AppHost's ordering is wrong, not its use of the builder.
                // Design: "A ServiceCatalogBuilder captured and mutated after this point
                // contributes nothing." This error does not latch — it is raised here, in
                // AddServiceCatalog itself, rather than inside ConfigLoader<LoadedConfig>.Load — so
                // it would re-throw per call if reached twice. In practice there is only one such
                // call reachable per builder: the first AddServiceCatalog after resolution throws
                // and the AppHost stops.
                if (_frozen)
                {
                    throw new ServiceSourcesConfigurationException(
                        "AddServiceCatalog(…) was called after the service catalog had already been read, so " +
                        "its entries could not be seen. Because a service is resolved as it is added, the " +
                        "catalog must be declared before the first AddService(…) — near the top of the AppHost, " +
                        "next to UseDeferredCheckout() and UseJava().");
                }

                configure(Builder);
            }
        }

        /// <summary>
        /// Builds every accumulated entry and marks this accumulator frozen, so a later
        /// <see cref="Configure"/> call reports the ordering error above instead of silently
        /// contributing nothing.
        /// </summary>
        public IReadOnlyDictionary<string, ServiceDefinition> Freeze()
        {
            lock (_gate)
            {
                _frozen = true;
                return Builder.Freeze();
            }
        }
    }

    /// <summary>
    /// The whole loaded configuration, for callers that work across services rather than resolving
    /// one — the parallel checkout prefetch, which needs the full set of <c>"local"</c> services
    /// before any of them has been asked for by name.
    /// </summary>
    public static LoadedConfig LoadedFor(IDistributedApplicationBuilder builder) =>
        // The factory has to stay free of side effects: ConditionalWeakTable.GetValue may run it
        // concurrently for the same key and keep only one of the results, so anything it did on the
        // builder would be done twice while only one instance survived to say it had happened.
        // Loading behind a lock on the instance that actually won is the shape LocalCheckoutPrefetch
        // uses, for the same reason. Registering servicesources.local.json is guarded that way
        // too — see DeveloperConfigFileSource, which owns it because an entry point registers it
        // before any of this runs.
        Cache.GetValue(builder, static _ => new ConfigLoader<LoadedConfig>())
            .Load(builder, LoadedConfig.Load);

    /// <summary>
    /// One backing service's developer config, or an entry with no source — which means
    /// <c>"local"</c> — when nothing configures it.
    /// </summary>
    /// <remarks>
    /// Unconfigured is the default rather than an error, unlike <see cref="ResolveService"/>: a
    /// backing service with no entry runs from the factory the AppHost passed to
    /// <c>AddBackingService</c>, which is what an AppHost nobody has pointed at a cluster does for
    /// every one of them. There is nothing for the developer to fix, so there is nothing to report.
    /// <para>
    /// A blank source takes the same route. It arrives from a higher layer blanking the key — the
    /// one gesture configuration offers for dropping a value a layer below set — and dropping the
    /// source is asking for the default, not asking for a source nobody named.
    /// </para>
    /// </remarks>
    public static BackingServiceDeveloperConfig ResolveBackingService(
        IDistributedApplicationBuilder builder, string name) =>
        BackingServicesFor(builder).TryGetValue(name, out var config) ? config : new BackingServiceDeveloperConfig();

    /// <summary>
    /// Every backing-service entry, for the same kind of caller <see cref="LoadedFor"/> serves.
    /// </summary>
    public static IReadOnlyDictionary<string, BackingServiceDeveloperConfig> BackingServicesFor(
        IDistributedApplicationBuilder builder) =>
        BackingServiceCache
            .GetValue(builder, static _ =>
                new ConfigLoader<IReadOnlyDictionary<string, BackingServiceDeveloperConfig>>())
            .Load(builder, DeveloperConfiguration.ReadBackingServicesFrom);

    public static (ServiceDefinition Definition, ServiceDeveloperConfig DeveloperConfig) ResolveService(
        IDistributedApplicationBuilder builder, string serviceName)
    {
        var loaded = LoadedFor(builder);

        if (!loaded.Catalog.Services.TryGetValue(serviceName, out var definition))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' was not found in the service catalog{DescribeCatalogSources(loaded)}.");
        }

        if (!loaded.DeveloperConfig.Services.TryGetValue(serviceName, out var developerConfig))
        {
            throw loaded.DeveloperConfig.NotConfiguredError(serviceName);
        }

        // An entry with a blank source is an entry with no source. It arrives that way from an
        // entry that names only its blocks, and from a higher layer blanking the key — the one
        // gesture configuration offers for dropping a value a layer below set. Either way the
        // developer's problem is a source that is missing, not one this package fails to
        // recognise, so it takes the same route as an entry that is absent altogether.
        if (string.IsNullOrWhiteSpace(developerConfig.Source))
        {
            throw loaded.DeveloperConfig.NotConfiguredError(serviceName);
        }

        return (definition, developerConfig);
    }

    /// <summary>
    /// Names whichever catalog source(s) actually contributed to <paramref name="loaded"/>, for a
    /// not-found error that must never claim a code-only service could have been found in a yaml
    /// file that doesn't exist for this AppHost (design finding 5), while still naming
    /// 'servicesources.yaml' for the still-majority yaml-only AppHost, which loses real diagnostics
    /// without it.
    /// </summary>
    private static string DescribeCatalogSources(LoadedConfig loaded)
    {
        if (loaded.YamlPath is null)
        {
            // Code-only catalog: no yaml file exists for this AppHost.
            return "";
        }

        return loaded.HasCodeEntries
            ? $" (declared in {CatalogOrigin.Code.Describe()} and in {CatalogOrigin.FromYaml(loaded.YamlPath).Describe()})"
            : $" ({CatalogOrigin.FromYaml(loaded.YamlPath).Describe()})";
    }

    /// <summary>
    /// One builder's slot in a cache above. Holding the load behind this rather than in the table's
    /// factory is what makes it happen exactly once per builder.
    /// </summary>
    /// <remarks>
    /// Generic over what is loaded because there are two of these — the catalog and the service
    /// entries, and the backing-service entries — and the once-per-builder guarantee and the
    /// latching below are the whole of what either needs.
    /// </remarks>
    private sealed class ConfigLoader<T>
        where T : class
    {
        // Plain object rather than System.Threading.Lock: this package still targets net8.0.
        private readonly object _gate = new();

        private T? _loaded;

        private ExceptionDispatchInfo? _failure;

        /// <summary>
        /// A load that throws a configuration error is remembered and rethrown rather than retried,
        /// so every later caller is told what the first one was told: such an error is not
        /// transient, and a second walk of the same providers would only arrive at it again.
        /// </summary>
        /// <remarks>
        /// Only that kind of failure is latched, because only that kind is known to be permanent.
        /// The load also reads two files off disk, so an <see cref="IOException"/> from a file
        /// something else holds open for a moment can reach here — and latching it would fail every
        /// later <c>AddService()</c> call over a condition that had already passed by the time the
        /// second one asked. Anything unrecognised is left to the next caller to retry, which at
        /// worst repeats a deterministic failure and re-reports it unchanged.
        /// </remarks>
        public T Load(IDistributedApplicationBuilder builder, Func<IDistributedApplicationBuilder, T> load)
        {
            lock (_gate)
            {
                _failure?.Throw();

                if (_loaded is not null)
                {
                    return _loaded;
                }

                try
                {
                    return _loaded = load(builder);
                }
                catch (ServiceSourcesConfigurationException ex)
                {
                    _failure = ExceptionDispatchInfo.Capture(ex);
                    throw;
                }
            }
        }
    }

    internal sealed class LoadedConfig
    {
        public required Catalog.CodeServiceCatalog Catalog { get; init; }

        public required DeveloperConfiguration DeveloperConfig { get; init; }

        /// <summary>
        /// This AppHost's <c>servicesources.yaml</c> path, if the file exists — <c>null</c> for a
        /// code-only catalog. Lets <see cref="DescribeCatalogSources"/> name the right thing for a
        /// not-found service (design finding 5) without claiming a code-only catalog could have been
        /// found in a yaml file that isn't there.
        /// </summary>
        public string? YamlPath { get; init; }

        /// <summary>Whether this AppHost declared at least one service via <c>AddServiceCatalog</c>.</summary>
        public required bool HasCodeEntries { get; init; }

        public static LoadedConfig Load(IDistributedApplicationBuilder builder)
        {
            // Freeze first, under the same lock CodeCatalogFor's Configure calls take — an
            // AddServiceCatalog reaching this builder after this point contributes nothing (design:
            // "A ServiceCatalogBuilder captured and mutated after this point contributes nothing").
            var codeEntries = CodeCatalogFor(builder).Freeze();

            var yamlPath = Path.Combine(builder.AppHostDirectory, "servicesources.yaml");
            var yamlExists = File.Exists(yamlPath);

            if (codeEntries.Count == 0 && !yamlExists)
            {
                throw new ServiceSourcesConfigurationException(
                    $"No service catalog found. Declare one with builder.AddServiceCatalog(catalog => …) " +
                    $"before the first AddService(…) call, or create '{yamlPath}'.");
            }

            var merged = new Dictionary<string, ServiceDefinition>(StringComparer.Ordinal);

            foreach (var (name, definition) in codeEntries)
            {
                merged[name] = definition;
            }

            if (yamlExists)
            {
                var yamlCatalog = ServiceCatalogLoader.Load(yamlPath);

                foreach (var (name, metadata) in yamlCatalog.Services)
                {
                    // OrdinalIgnoreCase comparison done by hand (design finding 11) — never
                    // new Dictionary<string, ServiceDefinition>(StringComparer.OrdinalIgnoreCase),
                    // which would (a) throw a raw ArgumentException on the second yaml case-variant
                    // instead of the existing AmbiguousCatalogSpellingError, and (b) make lookup
                    // itself case-insensitive, silently changing behavior for an existing yaml-only
                    // AppHost (acceptance criterion 3).
                    //
                    // Scanned against codeEntries specifically, not against the merged dictionary
                    // this loop is building: merged already holds earlier yaml entries by the time a
                    // later one is processed, and scanning it would misreport a same-file yaml-vs-yaml
                    // case collision (e.g. "orders:" then "Orders:") as a code/yaml duplicate — it has
                    // to fall through to the AmbiguousCatalogSpellingError below untouched instead.
                    var codeCollision = codeEntries.Keys.FirstOrDefault(
                        existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));

                    if (codeCollision is not null)
                    {
                        throw new ServiceSourcesConfigurationException(
                            $"Service '{name}' is declared twice: in {CatalogOrigin.Code.Describe()} and in " +
                            $"'{yamlPath}'. A service belongs to one catalog; remove one of the two. To vary a " +
                            "service per developer, set its 'source' in 'servicesources.local.json' instead.");
                    }

                    // A yaml-vs-yaml case collision (e.g. "orders:" and "Orders:" both in the same
                    // file) is NOT caught here — ServiceCatalogLoader/ServiceCatalog.Services is
                    // Ordinal, so both entries survive the yaml load and land here as two distinct
                    // keys. Left to DeveloperConfiguration.CanonicalizeToCatalog's existing
                    // AmbiguousCatalogSpellingError, reached via ReadFrom below with the merged
                    // (Ordinal) key set — unchanged from today.
                    merged[name] = metadata.ToDefinition(yamlPath, name);
                }
            }

            var catalog = new Catalog.CodeServiceCatalog { Services = merged };

            // The catalog first, and its names handed over: unchanged from before this task, and now
            // covers code-declared names too (design: "canonical-spelling reconciliation covers
            // code-declared names too").
            return new LoadedConfig
            {
                Catalog = catalog,
                DeveloperConfig = DeveloperConfiguration.ReadFrom(builder, catalog.Services.Keys),
                YamlPath = yamlExists ? yamlPath : null,
                HasCodeEntries = codeEntries.Count > 0,
            };
        }
    }
}
