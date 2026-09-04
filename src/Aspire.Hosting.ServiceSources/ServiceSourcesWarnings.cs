using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Collects what the developer should be told but should not be stopped for, and reports it once the
/// app host has a logger.
/// </summary>
/// <remarks>
/// Two things arrive here. Configuration skipped because a service resolved to an out-of-band
/// source: skipping rather than throwing is what keeps a shared <c>Program.cs</c> working when one
/// developer flips a service to <c>"kubernetes"</c> or <c>"url"</c> in their own
/// <c>servicesources.local.json</c> — the whole point of this package. But silently dropping
/// configuration is the failure mode issue #53 was filed about, so every skip is reported. And
/// backing-service configuration that nothing read (#206), which cannot be an error either, because
/// a shared file may legitimately carry entries for backing services only some configurations add.
/// <para>
/// Buffered rather than written immediately because <c>AddService()</c> runs while the AppHost is
/// still being composed, before there is an <see cref="ILogger"/> to write to. <c>BeforeStartEvent</c>
/// is the first point that has one, and still runs before DCP starts anything.
/// </para>
/// <para>
/// Reported per service and source rather than per call. A service configured through a couple of
/// dozen <c>Configure</c> calls — the shape this package is built for — would otherwise produce a
/// couple of dozen near-identical warnings the moment someone switched it to <c>"kubernetes"</c>,
/// which reads as noise and buries the one fact that matters.
/// </para>
/// </remarks>
internal sealed class ServiceSourcesWarnings
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, ServiceSourcesWarnings> Cache = new();

    private readonly object _gate = new();

    private readonly List<Entry> _entries = [];

    private bool _subscribed;

    /// <summary>One thing worth telling the developer about.</summary>
    /// <remarks>
    /// Two shapes rather than one, in one list. A <see cref="Skip"/> is a record that is described
    /// on the way out, because several of them collapse into a single message; a
    /// <see cref="Message"/> is already the sentence.
    /// </remarks>
    private abstract record Entry
    {
        /// <summary>
        /// Whether this entry has been written to the log, which is what makes reporting
        /// exactly-once.
        /// </summary>
        /// <remarks>
        /// A flag per entry rather than a count of how far <see cref="Flush"/> has got. The count
        /// was equivalent while flushing meant "everything outstanding", and stopped being so once
        /// <see cref="ReportNow"/> existed: reporting one entry out of order has to leave the
        /// entries around it outstanding, and an index cannot express that. See that method for why
        /// it is needed.
        /// </remarks>
        public bool Reported { get; set; }
    }

    private sealed record Skip(string ServiceName, string Source, string Capability) : Entry;

    private sealed record Message(string Text) : Entry;

    /// <summary>
    /// Everything reported so far, for tests — the log itself isn't observable in-process.
    /// </summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return Describe(_entries);
            }
        }
    }

    /// <summary>
    /// One message per (service, source) over the skips in <paramref name="entries"/>, followed by
    /// the ready-made messages in the order they were added.
    /// </summary>
    /// <remarks>
    /// Skips first rather than interleaved, so that the grouping does not depend on what else
    /// happened to be recorded between two of them.
    /// </remarks>
    private static IReadOnlyList<string> Describe(IEnumerable<Entry> entries)
    {
        var materialized = entries.ToArray();

        var skips = materialized
            .OfType<Skip>()
            .GroupBy(skip => (skip.ServiceName, skip.Source))
            .Select(group => SkipReason(
                group.Key.ServiceName, group.Key.Source, [.. group.Select(skip => skip.Capability)]));

        return [.. skips, .. materialized.OfType<Message>().Select(message => message.Text)];
    }

    public static ServiceSourcesWarnings For(IDistributedApplicationBuilder builder)
    {
        // The factory stays free of side effects: ConditionalWeakTable.GetValue may run it
        // concurrently for the same key and keep only one of the results, so subscribing in there
        // could leave a discarded instance's subscription behind — flushing warnings nobody added.
        var warnings = Cache.GetValue(builder, static _ => new ServiceSourcesWarnings());

        warnings.EnsureSubscribed(builder);

        return warnings;
    }

    private void EnsureSubscribed(IDistributedApplicationBuilder builder)
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
                Flush(@event.Services);
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>
    /// Records that <paramref name="capability"/> was skipped for <paramref name="serviceName"/>.
    /// </summary>
    public void AddSkip(string serviceName, string source, string capability)
    {
        lock (_gate)
        {
            _entries.Add(new Skip(serviceName, source, capability));
        }
    }

    /// <summary>
    /// Records a warning that is already a sentence, for something that is not a skipped call.
    /// </summary>
    /// <remarks>
    /// The route for configuration nothing read (#206), which has no service and no capability to
    /// group by — its subject is an entry in the file rather than a call on a resource. Written by
    /// its caller rather than assembled here, because the callers that need this each know
    /// something about their own subject that a shared formatter would have to be told anyway.
    /// </remarks>
    public void AddWarning(string message)
    {
        lock (_gate)
        {
            _entries.Add(new Message(message));
        }
    }

    /// <summary>
    /// Reports every skip added since the last call, and is safe to call more than once.
    /// </summary>
    /// <remarks>
    /// Report-once rather than report-all because a skip can be recorded <i>during</i>
    /// <c>BeforeStartEvent</c> — <see cref="Sources.UrlSource"/> drops a consumer's wait on a
    /// <c>"url"</c> service there, once the whole model is visible — and Aspire dispatches handlers
    /// for one event in subscription order with no way to ask to go last. Whichever of the two
    /// handlers runs second reports what the first had not seen yet, so a late skip is logged
    /// exactly once whatever the order turns out to be.
    /// </remarks>
    public void Flush(IServiceProvider services)
    {
        IReadOnlyList<string> messages;

        lock (_gate)
        {
            var pending = _entries.Where(entry => !entry.Reported).ToArray();

            foreach (var entry in pending)
            {
                entry.Reported = true;
            }

            messages = Describe(pending);
        }

        Write(services, messages);
    }

    /// <summary>
    /// Reports <paramref name="messages"/> immediately, leaving everything else buffered for
    /// whoever flushes next.
    /// </summary>
    /// <remarks>
    /// For a caller that has something to say during <c>BeforeStartEvent</c> but has no claim on
    /// anything else outstanding. <see cref="Flush"/> would report the lot, and that is destructive
    /// this early in the event: a skip recorded <i>later</i> in the same event — <see
    /// cref="Sources.UrlSource"/> drops a consumer's wait on a <c>"url"</c> service once the whole
    /// model is visible — belongs in the same grouped message as that service's skipped
    /// <c>Configure</c> calls, and a flush in between splits it into two. <see cref="Sources.UrlSource"/>
    /// takes care to subscribe ahead of this class's own flush handler for exactly that reason, and
    /// a handler that flushed everything before it ran would undo the arrangement.
    /// <para>
    /// The messages are recorded as already reported rather than not recorded at all, so that
    /// <see cref="Messages"/> still shows them and a later <see cref="Flush"/> does not repeat them.
    /// </para>
    /// </remarks>
    public void ReportNow(IServiceProvider services, IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var message in messages)
            {
                _entries.Add(new Message(message) { Reported = true });
            }
        }

        Write(services, messages);
    }

    private static void Write(IServiceProvider services, IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Aspire.Hosting.ServiceSources");

        foreach (var message in messages)
        {
            logger?.LogWarning("{ServiceSourcesWarning}", message);
        }
    }

    /// <summary>
    /// Explains a skip in terms of what the reader can act on: which service, which source, what was
    /// dropped, and where the source is chosen.
    /// </summary>
    private static string SkipReason(string serviceName, string source, IReadOnlyList<string> capabilities)
    {
        var detail = source switch
        {
            "url" =>
                "it resolves to a fixed, already-running URL with no local process to configure",
            "kubernetes" =>
                "it resolves to a 'kubectl port-forward' in front of an already-running service, so the " +
                "configuration would reach kubectl rather than the service",
            _ => "it runs out of band",
        };

        return $"Service '{serviceName}': skipped {DescribeCalls(capabilities)} because its source is " +
               $"'{source}' — {detail}. The service is expected to be configured wherever it actually " +
               "runs. Set its source to 'local' or 'container' in servicesources.local.json for this AppHost's " +
               "configuration and start ordering to apply.";
    }

    /// <summary>
    /// A single call reads as itself; several read as a count plus a per-capability tally, so the
    /// message stays one line however many calls it stands for.
    /// </summary>
    /// <remarks>
    /// The count is of "calls" rather than "Configure calls" because a service can accumulate skips
    /// of two different shapes: its own <c>Configure</c> calls, and a consumer's <c>WaitFor</c> on
    /// it that <see cref="Sources.UrlSource"/> drops. The tally names each one, so the summary does
    /// not have to guess which shape it is standing for.
    /// </remarks>
    private static string DescribeCalls(IReadOnlyList<string> capabilities)
    {
        if (capabilities.Count == 1)
        {
            return capabilities[0];
        }

        var tally = capabilities
            .GroupBy(capability => capability, StringComparer.Ordinal)
            .Select(group => group.Count() == 1 ? group.Key : $"{group.Key} ×{group.Count()}");

        return $"{capabilities.Count} calls ({string.Join(", ", tally)})";
    }
}
