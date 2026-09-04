using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.BackingServices;

/// <summary>
/// Reports backing-service configuration that nothing read — an entry naming no
/// <c>AddBackingService</c> call, or a misspelled <c>backingServices</c> root key (#206).
/// </summary>
/// <remarks>
/// Neither can be caught where the configuration is read, because the state they produce is the
/// legitimate default: a backing service with no entry runs from the factory the AppHost passed, and
/// that is what an AppHost nobody has pointed anywhere does for every one of them. The service side
/// can fail loudly on the same shape only because a service with no entry has no source at all.
/// <para>
/// What makes it detectable is composition being finished. By <c>BeforeStartEvent</c> the set of
/// names <c>AddBackingService</c> was called with is known, and an entry outside that set was read
/// by nobody.
/// </para>
/// <para>
/// A warning rather than an error: a shared <c>servicesources.local.json</c> may carry entries for
/// backing services only some configurations add, which is the same reason the service side
/// validates every entry without requiring each to be used. There is deliberately no opt-out —
/// whether deliberately-unused entries are common is what this warning will find out, and designing
/// for that first would be designing without the answer.
/// </para>
/// </remarks>
internal static class BackingServiceConfigAudit
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, Declared> Declarations = new();

    /// <summary>
    /// Records that <paramref name="name"/> was declared on <paramref name="builder"/>, and arranges
    /// for the audit to run once the AppHost is composed.
    /// </summary>
    public static void Record(IDistributedApplicationBuilder builder, string name)
    {
        // The factory stays free of side effects: ConditionalWeakTable.GetValue may run it
        // concurrently for the same key and keep only one of the results, so subscribing in there
        // could leave a discarded instance's subscription behind — auditing against a set of
        // declared names that nothing went on to add to. The same shape ServiceSourcesWarnings
        // uses, for the same reason.
        var declared = Declarations.GetValue(builder, static _ => new Declared());

        declared.Add(name);
        declared.EnsureSubscribed(builder);
    }

    /// <summary>
    /// The declared names for one builder, and its subscription.
    /// </summary>
    private sealed class Declared
    {
        // Plain object rather than System.Threading.Lock: this package still targets net8.0.
        private readonly object _gate = new();

        /// <summary>
        /// Compared the way configuration compares keys, so that an entry written <c>Orders-DB</c>
        /// against <c>AddBackingService("orders-db")</c> is the match it actually is rather than an
        /// orphan. Ordinal here would warn about an entry that is working perfectly.
        /// </summary>
        private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);

        private bool _subscribed;

        public void Add(string name)
        {
            lock (_gate)
            {
                _names.Add(name);
            }
        }

        public void EnsureSubscribed(IDistributedApplicationBuilder builder)
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
                    // ReportNow rather than Flush, and the distinction matters. This handler is
                    // subscribed at the first AddBackingService, which is usually one of an
                    // AppHost's first lines, so it tends to run before every other subscriber —
                    // including UrlSource, which records a dropped wait during this same event and
                    // needs it grouped with that service's earlier skipped Configure calls. A Flush
                    // here would report those calls first and leave the dropped wait to arrive as a
                    // second message, undoing the ordering UrlSource arranges deliberately.
                    // Reporting only what this audit produced leaves everything else outstanding for
                    // whoever owns it.
                    //
                    // ReporterFor rather than For for a related reason: For would subscribe the
                    // warnings class's flush handler from inside the event it handles, which Aspire
                    // has already snapshotted, so the handler would never run. See that method.
                    ServiceSourcesWarnings.ReporterFor(builder).ReportNow(@event.Services, Report(builder, Snapshot()));

                    return Task.CompletedTask;
                });
            }
        }

        private HashSet<string> Snapshot()
        {
            lock (_gate)
            {
                return new HashSet<string>(_names, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// The warnings this AppHost's backing-service configuration has earned, if any.
    /// </summary>
    /// <remarks>
    /// The two checks are independent, and the root-key one is asked unconditionally rather than
    /// gated on the bound section being empty. That section is the <em>merged</em> view across every
    /// configuration layer, so gating on it lets a single environment variable setting one entry
    /// suppress the report that the developer's whole file is going unread. Whether the file's root
    /// key is a typo is a property of the file alone, and no other layer has a root key to answer it
    /// with. The lookup returns nothing when the file has the key, has nothing resembling it, or is
    /// not there, so asking always costs nothing.
    /// <para>
    /// Both are reached only with at least one <c>AddBackingService</c> call behind them, since
    /// nothing else subscribes this handler. An AppHost that adds no backing services never hears
    /// about the section it is not using.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> Report(IDistributedApplicationBuilder builder, HashSet<string> declared)
    {
        var reasons = new List<string>();

        if (DeveloperConfigFileSource.NearMissForBackingServicesKey(builder) is { } nearMiss)
        {
            reasons.Add(MisspelledRootKeyReason(builder, nearMiss));
        }

        // Ordinal ordering so a file with several orphans names them in the same order every run,
        // rather than whichever order the providers merged them in.
        var orphans = ServiceSourcesConfigCache.BackingServicesFor(builder).Keys
            .Where(key => !declared.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        if (orphans.Length > 0)
        {
            reasons.Add(OrphanedEntriesReason(orphans, declared));
        }

        return reasons;
    }

    /// <summary>
    /// Explains entries nobody read, in terms of what they cost: not the absence of a setting, but a
    /// backing service resolved from the AppHost's own factory when the developer was pointing it
    /// somewhere else.
    /// </summary>
    /// <remarks>
    /// One message for all of them rather than one each, which is the rule the warnings channel
    /// already follows for skipped calls: they share a cause and a fix, so they share a line.
    /// <para>
    /// Each orphan is offered the declared name it resembles, which is the whole of what a typo
    /// needs — <c>orders_db</c> against <c>orders-db</c> is one edit, and seeing the two side by side
    /// is what makes it visible. The declared names are listed as well, since an orphan resembling
    /// none of them is just as likely to be an entry for a backing service this AppHost does not add.
    /// </para>
    /// </remarks>
    private static string OrphanedEntriesReason(IReadOnlyList<string> orphans, HashSet<string> declared)
    {
        var candidates = declared.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        var described = orphans.Select(orphan =>
        {
            var closest = NearMiss.Nearest(orphan, candidates, spelling: name => name).FirstOrDefault();

            return closest is null ? $"'{orphan}'" : $"'{orphan}' (did you mean '{closest}'?)";
        });

        return $"Backing service configuration that nothing read: {string.Join(", ", described)}. "
            + $"No AddBackingService() call names {(orphans.Count == 1 ? "it" : "them")}, so "
            + $"{(orphans.Count == 1 ? "the entry was" : "the entries were")} never looked up, and each backing "
            + "service they were meant to configure resolved to its 'local' source instead — running from the "
            + "factory this AppHost supplies rather than from the entry. This AppHost adds: "
            + $"{string.Join(", ", candidates.Select(name => $"'{name}'"))}. Correct the key under "
            + $"\"{DeveloperConfigFileSource.FileBackingServicesKey}\" in '{DeveloperConfiguration.FileName}', or "
            + "wherever a higher layer set it, or remove the entry if it is deliberately unused.";
    }

    /// <summary>
    /// Explains a root key that resembles <c>backingServices</c> closely enough to be a misspelling
    /// of it.
    /// </summary>
    /// <remarks>
    /// Says the file rather than the configuration key, because a root key is a property of the file
    /// alone: no other configuration layer has one to misspell.
    /// </remarks>
    private static string MisspelledRootKeyReason(IDistributedApplicationBuilder builder, string nearMiss) =>
        $"'{Path.Combine(builder.AppHostDirectory, DeveloperConfiguration.FileName)}' has a top-level key "
        + $"'{nearMiss}', and no '{DeveloperConfigFileSource.FileBackingServicesKey}' key. Did you mean "
        + $"'{DeveloperConfigFileSource.FileBackingServicesKey}'? Nothing read it, so nothing in it configured "
        + "anything — every backing service this AppHost adds takes its source from elsewhere, and any that has "
        + "no source elsewhere resolves to 'local', running from the factory this AppHost supplies.";
}
