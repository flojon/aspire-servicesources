using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;

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
        public (
            IReadOnlyDictionary<string, ServiceDefinition> Services,
            IReadOnlyDictionary<string, Catalog.RepositoryDefinition> Repositories) Freeze()
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
            var (codeEntries, codeRepositories) = CodeCatalogFor(builder).Freeze();

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

            // Seeded from the code catalog's own repositories; a yaml catalog's are merged in below.
            // Task 9's composition checks (a name colliding with an ungrouped service, or declared in
            // both catalogs) land here beside the existing duplicate-service check.
            var repositories = new Dictionary<string, Catalog.RepositoryDefinition>(StringComparer.Ordinal);
            foreach (var (name, repository) in codeRepositories)
            {
                repositories[name] = repository;
            }

            if (yamlExists)
            {
                var (yamlCatalog, yamlRepositories) = ServiceCatalogLoader.Load(yamlPath);

                // Snapshotted before the loop below adds to `repositories`: this is what tells the
                // case-collision message which side is which. Without it, a same-file yaml-vs-yaml
                // case collision (two 'repositories:' entries differing only by case, both already
                // added to `repositories` by earlier iterations of this same loop) would be
                // misreported as a collision with the code catalog even when code declared no
                // repositories at all.
                var codeRepositoryNames = repositories.Keys.ToHashSet(StringComparer.Ordinal);

                foreach (var (name, repository) in yamlRepositories)
                {
                    // A repository's own name is checked here against the code catalog's
                    // repositories specifically — repositories is seeded from codeRepositories above
                    // and this loop is the only writer to it before this point, so any existing key
                    // here really is a code/yaml collision rather than a yaml-vs-yaml one.
                    if (repositories.ContainsKey(name))
                    {
                        throw new ServiceSourcesConfigurationException(
                            $"Repository '{name}' is declared twice: in {CatalogOrigin.Code.Describe()} and in " +
                            $"'{yamlPath}'. A repository belongs to one catalog; remove one of the two.");
                    }

                    // Case-only, unlike the equivalent service check further down (which defers to
                    // DeveloperConfiguration.CanonicalizeToCatalog's AmbiguousCatalogSpellingError):
                    // that path only fires when a developer's config happens to reference one of the
                    // ambiguous spellings, and a repository's own name becomes a checkout directory
                    // unconditionally, whether or not anyone's local config ever mentions it — the
                    // same reasoning ServiceCatalogBuilder.AddRepository's own case check applies to
                    // two code-declared names. The default filesystem on Windows and macOS is
                    // case-insensitive, so 'checkouts/Monorepo' and 'checkouts/monorepo' are the same
                    // directory there. Reachable from two yaml entries as well as one of each: yaml's
                    // own Repositories dictionary is Ordinal, so 'Monorepo:'/'monorepo:' in the same
                    // file both survive ServiceCatalogLoader.Load and land here as two distinct keys.
                    var repositoryCaseCollision = repositories.Keys.FirstOrDefault(
                        existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));

                    if (repositoryCaseCollision is not null)
                    {
                        var collisionOrigin = codeRepositoryNames.Contains(repositoryCaseCollision)
                            ? CatalogOrigin.Code.Describe()
                            : $"'{yamlPath}'";

                        throw new ServiceSourcesConfigurationException(
                            $"Repository '{name}' in '{yamlPath}' differs only by case from repository " +
                            $"'{repositoryCaseCollision}' in {collisionOrigin}. Two repositories " +
                            "must differ by more than case.");
                    }

                    repositories[name] = repository;
                }

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
                    merged[name] = metadata.ToDefinition(yamlPath, name, yamlRepositories);
                }
            }

            // "One namespace, checked once" (design #291): a repository's own name is also the
            // directory an ungrouped service's own name would claim (design finding 2), so the two
            // must never collide. A repository dict key is always identical to its own
            // RepositoryDefinition.CheckoutName by construction (ServiceCatalogBuilder.AddRepository
            // and ServiceCatalogLoader.Load both key repositories by the name whose CheckoutName they
            // set), so checking every ungrouped service's own name against the repositories dict is
            // the whole of this check — no separate reverse index is needed.
            foreach (var (serviceName, definition) in merged)
            {
                if (definition.Repository.CheckoutName != serviceName)
                {
                    // Grouped: this service's own name never becomes a checkout directory, so it has
                    // nothing to collide with a repository name over.
                    continue;
                }

                if (repositories.ContainsKey(serviceName))
                {
                    throw new ServiceSourcesConfigurationException(
                        $"'{serviceName}' names both an ungrouped service — whose managed checkout is keyed on " +
                        "its own name — and a repository declared under the same name, so the two would share " +
                        "one checkout directory. Rename the service, or the repository, so they no longer match.");
                }

                // Case-only — see the repository/repository check above for why this cannot be left
                // to DeveloperConfiguration.CanonicalizeToCatalog's incidental catch: both an
                // ungrouped service's own name and a repository's name become checkout directories
                // unconditionally, and the default filesystem on Windows and macOS does not
                // distinguish them by case.
                var serviceCaseCollision = repositories.Keys.FirstOrDefault(
                    existing => string.Equals(existing, serviceName, StringComparison.OrdinalIgnoreCase));

                if (serviceCaseCollision is not null)
                {
                    throw new ServiceSourcesConfigurationException(
                        $"'{serviceName}' names an ungrouped service, and differs only by case from repository " +
                        $"'{serviceCaseCollision}' — the two would still share one checkout directory on a " +
                        "filesystem that does not distinguish them by case. Rename the service, or the " +
                        "repository, so they no longer match even by case.");
                }
            }

            var catalog = new Catalog.CodeServiceCatalog { Services = merged, Repositories = repositories };

            // Read ahead of the warning below rather than at the return statement (its usual place):
            // the warning has to know which catalog shape each service's developer actually resolves
            // through, which only this has — the catalog shape alone (a non-blank Repository.Url)
            // says a service *could* resolve locally, not that it does.
            var developerConfig = DeveloperConfiguration.ReadFrom(builder, catalog.Services.Keys, catalog.Repositories.Keys);

            // The warn-and-continue counterpart of the check above (design question 5, settled as
            // "warn"): two ungrouped services naming the identical repository URL each get their own
            // checkout, downloaded and reconciled separately, which is exactly what grouping them
            // would avoid. Not an error — an existing catalog with this shape keeps working — but
            // worth naming, since it usually means grouping was overlooked rather than intended.
            foreach (var sharedUrl in merged
                .Where(entry => entry.Value.Repository.CheckoutName == entry.Key)
                // A blank Url is not a repository at all: every service gets a RepositoryDefinition
                // regardless of source (design finding 2 mints an anonymous one unconditionally), so
                // a kubernetes- or url-sourced service — which never sets 'repository:' — carries one
                // whose Url defaults to "". Grouping those together would warn about services that
                // were never candidates for sharing a checkout in the first place.
                .Where(entry => !string.IsNullOrEmpty(entry.Value.Repository.Url))
                // The catalog shape alone does not say which source is actually in effect: a service
                // can declare both a 'repository:' block and, say, a 'kubernetes:' block (README
                // "Combining sources on one catalog entry"), with servicesources.local.json resolving
                // it through the other one — in which case nothing is ever cloned for it, and this
                // warning's advice ("share one checkout") describes work that never happens. Matched
                // the same way LocalCheckoutPrefetch.Run decides what it will actually clone —
                // case-insensitively, since AddService resolves the source the same way.
                .Where(entry =>
                    developerConfig.Services.TryGetValue(entry.Key, out var devConfig)
                    && string.Equals(devConfig.Source, "local", StringComparison.OrdinalIgnoreCase))
                .GroupBy(entry => entry.Value.Repository.Url, StringComparer.Ordinal)
                .Where(group => group.Count() > 1))
            {
                var serviceNames = sharedUrl.Select(entry => entry.Key).Order(StringComparer.Ordinal).ToArray();

                ServiceSourcesWarnings.For(builder).AddNotice(
                    $"Services {string.Join(", ", serviceNames.Select(name => $"'{name}'"))} are all 'local' and " +
                    $"declare the same repository '{GitUrl.Redact(sharedUrl.Key)}', but none of them are grouped " +
                    "— each gets its own checkout, cloned and reconciled separately. To share one checkout " +
                    "instead, group them: AddRepository(...)/WithSharedRepository(...) in code, or a " +
                    "repositories: entry every member's repositoryRef: names, in yaml.");
            }

            // The catalog first, and its names handed over: unchanged from before this task, and now
            // covers code-declared names too (design: "canonical-spelling reconciliation covers
            // code-declared names too").
            return new LoadedConfig
            {
                Catalog = catalog,
                DeveloperConfig = developerConfig,
                YamlPath = yamlExists ? yamlPath : null,
                HasCodeEntries = codeEntries.Count > 0,
            };
        }
    }
}
