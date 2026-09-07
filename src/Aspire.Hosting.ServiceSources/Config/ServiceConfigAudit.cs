using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// Reports <c>services</c> configuration that nothing reads — an entry naming no service
/// <c>servicesources.yaml</c> declares, or a misspelled <c>services</c> root key (#215).
/// </summary>
/// <remarks>
/// The narrower of the two gaps #215 raised: an entry whose name is in the catalog but that no
/// <c>AddService</c> call adds is not silent today — <see cref="Sources.LocalCheckoutPrefetch"/>
/// already reports the cost of cloning it speculatively. What is silent is an entry naming no
/// catalog service at all, which is skipped without a word and, unlike the backing-service side,
/// does not need a per-builder record of which calls happened to detect: <see
/// cref="DeveloperConfiguration.UndeclaredNames"/> already is that set, decided the moment
/// configuration is read against the catalog, independent of which of the catalog's services this
/// AppHost goes on to add.
/// <para>
/// What still waits for <c>BeforeStartEvent</c> is not the diff but the reporting of it: an AppHost
/// that never calls <see cref="ServiceSourcesBuilderExtensions.AddService"/> never loads
/// configuration against the catalog at all, and should hear nothing about a section it never
/// reads from.
/// </para>
/// <para>
/// A warning rather than an error, for the same reason as the backing-service side: a shared
/// <c>servicesources.local.json</c> may carry entries for an AppHost this one is not, and there is
/// deliberately no opt-out.
/// </para>
/// </remarks>
internal static class ServiceConfigAudit
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, Subscription> Subscriptions = new();

    /// <summary>
    /// Arranges for the audit to run once <paramref name="builder"/>'s AppHost is composed. Safe to
    /// call more than once; only the first call subscribes anything.
    /// </summary>
    public static void EnsureSubscribed(IDistributedApplicationBuilder builder) =>
        // The factory stays free of side effects: ConditionalWeakTable.GetValue may run it
        // concurrently for the same key and keep only one of the results, so subscribing in there
        // could leave a discarded instance's subscription behind. The same shape
        // BackingServiceConfigAudit and ServiceSourcesWarnings use, for the same reason.
        Subscriptions.GetValue(builder, static _ => new Subscription()).Ensure(builder);

    private sealed class Subscription
    {
        // Plain object rather than System.Threading.Lock: this package still targets net8.0.
        private readonly object _gate = new();

        private bool _subscribed;

        public void Ensure(IDistributedApplicationBuilder builder)
        {
            lock (_gate)
            {
                if (_subscribed)
                {
                    return;
                }

                _subscribed = true;

                builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
                {
                    // ReporterFor rather than For, and ReportNow rather than Flush: subscribing the
                    // warnings class's own flush handler from inside the event it handles would
                    // never run it, since Aspire has already snapshotted the subscriber list by
                    // then. Reporting only what this audit produced leaves everything else
                    // outstanding for whoever owns it. See BackingServiceConfigAudit, which takes
                    // the same route for the same reason.
                    ServiceSourcesWarnings.ReporterFor(builder).ReportNow(@event.Services, Report(builder));

                    return Task.CompletedTask;
                });
            }
        }
    }

    /// <summary>
    /// The warnings this AppHost's <c>services</c> configuration has earned, if any.
    /// </summary>
    /// <remarks>
    /// Reads the same <see cref="DeveloperConfiguration"/> <see cref="ServiceSourcesConfigCache.LoadedFor"/>
    /// already cached for the first <see cref="ServiceSourcesBuilderExtensions.AddService"/> call —
    /// nothing here re-reads the file or re-parses the catalog.
    /// </remarks>
    private static IReadOnlyList<string> Report(IDistributedApplicationBuilder builder)
    {
        var config = ServiceSourcesConfigCache.LoadedFor(builder).DeveloperConfig;

        var reasons = new List<string>();

        if (config.NearMissRootKey is { } nearMiss)
        {
            reasons.Add(MisspelledRootKeyReason(builder, nearMiss));
        }

        // Ordinal ordering so a file with several orphans names them in the same order every run,
        // rather than whichever order the providers merged them in.
        var orphans = config.UndeclaredNames
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        if (orphans.Length > 0)
        {
            reasons.Add(OrphanedEntriesReason(orphans, config.CatalogNames));
        }

        return reasons;
    }

    /// <summary>
    /// Explains entries nobody read, in terms of what they cost: not a service left on its default,
    /// but configuration a developer wrote that names no service at all.
    /// </summary>
    /// <remarks>
    /// One message for all of them rather than one each, the rule the warnings channel already
    /// follows for skipped calls and for the backing-service side of this same audit.
    /// <para>
    /// Each orphan is offered the catalog name it resembles — the whole of what a typo needs, and
    /// what turned <c>planning-fronend</c> into a silent no-op in the AppHost #215 was filed
    /// against. The catalog names are listed as well, since an orphan resembling none of them is
    /// just as likely to be an entry for an AppHost sharing this file that adds a different set of
    /// services.
    /// </para>
    /// </remarks>
    private static string OrphanedEntriesReason(IReadOnlyList<string> orphans, IReadOnlyList<string> catalogNames)
    {
        var candidates = catalogNames.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        var described = orphans.Select(orphan =>
        {
            var closest = NearMiss.Nearest(orphan, candidates, spelling: name => name).FirstOrDefault();

            return closest is null ? $"'{orphan}'" : $"'{orphan}' (did you mean '{closest}'?)";
        });

        return $"Service configuration that nothing read: {string.Join(", ", described)}. No service in "
            + $"'servicesources.yaml' is named {(orphans.Count == 1 ? "it" : "any of them")}, so "
            + $"{(orphans.Count == 1 ? "the entry configures" : "the entries configure")} nothing. This AppHost's "
            + $"catalog declares: {string.Join(", ", candidates.Select(name => $"'{name}'"))}. Correct the key "
            + $"under \"{DeveloperConfigFileSource.FileServicesKey}\" in '{DeveloperConfiguration.FileName}', or "
            + "wherever a higher layer set it, or remove the entry if it is deliberately unused.";
    }

    /// <summary>
    /// Explains a root key that resembles <c>services</c> closely enough to be a misspelling of it.
    /// </summary>
    /// <remarks>
    /// Says the file rather than the configuration key, because a root key is a property of the
    /// file alone: no other configuration layer has one to misspell.
    /// </remarks>
    private static string MisspelledRootKeyReason(IDistributedApplicationBuilder builder, string nearMiss) =>
        $"'{Path.Combine(builder.AppHostDirectory, DeveloperConfiguration.FileName)}' has a top-level key "
        + $"'{nearMiss}', and no '{DeveloperConfigFileSource.FileServicesKey}' key. Did you mean "
        + $"'{DeveloperConfigFileSource.FileServicesKey}'? Nothing read it, so nothing in it configured a "
        + "service — whatever this AppHost's services are configured with is coming from another layer, and "
        + "any service with no source anywhere else fails when it is added.";
}
