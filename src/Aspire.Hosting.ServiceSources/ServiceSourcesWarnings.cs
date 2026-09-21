using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Messages;
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
        /// A flag per entry rather than a count of how far <see cref="Flush"/> has got. A count is
        /// enough only while every report takes everything outstanding; <see cref="ReportNow"/>
        /// reports one caller's entries and leaves the ones around them pending, which an index into
        /// the list cannot express. See that method for why it reports that way.
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
        var warnings = Instance(builder);

        warnings.EnsureSubscribed(builder);

        return warnings;
    }

    /// <summary>
    /// The instance for <paramref name="builder"/>, without the flush handler <see cref="For"/>
    /// subscribes.
    /// </summary>
    /// <remarks>
    /// For a caller that only ever uses <see cref="ReportNow"/>, and specifically for one calling
    /// from inside <c>BeforeStartEvent</c>. Subscribing during that event's own dispatch is not
    /// merely late, it is inert: Aspire snapshots the subscription list before dispatching, so the
    /// handler is registered and never runs. Nothing is lost by that today — the only caller in that
    /// position reports immediately, and anything with entries to flush subscribed while the AppHost
    /// was still being composed — but a subscription that cannot run is a trap laid for whoever
    /// buffers next, and asking for the instance without it means the trap is not there to spring.
    /// </remarks>
    public static ServiceSourcesWarnings ReporterFor(IDistributedApplicationBuilder builder) =>
        Instance(builder);

    // The factory stays free of side effects: ConditionalWeakTable.GetValue may run it concurrently
    // for the same key and keep only one of the results, so subscribing in there could leave a
    // discarded instance's subscription behind — flushing warnings nobody added.
    private static ServiceSourcesWarnings Instance(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static _ => new ServiceSourcesWarnings());

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
    /// Records <paramref name="notice"/> to be written verbatim once there is a logger.
    /// </summary>
    /// <remarks>
    /// For a notice that already names its own service and its own remedy, so there is nothing here
    /// to group it with or rephrase it into. The <c>prepare</c> step's is the first: a <c>path</c>
    /// service does not inherit its catalog's step, and the notice says which command was not run so
    /// it can be copied into the developer's own file.
    /// <para>
    /// Recorded once per AppHost however many times it is offered. The prepare notice is settled per
    /// service from configuration, so a service resolved twice would produce the identical sentence
    /// twice, which reads as two problems.
    /// </para>
    /// </remarks>
    public void AddNotice(string notice)
    {
        lock (_gate)
        {
            if (!_entries.OfType<Message>().Any(entry => string.Equals(entry.Text, notice, StringComparison.Ordinal)))
            {
                _entries.Add(new Message(notice));
            }
        }
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

    /// <summary>
    /// Reports state restored for <paramref name="serviceName"/>, immediately, as one message.
    /// </summary>
    /// <remarks>
    /// Through <see cref="ReportNow"/> rather than <see cref="AddSkip"/> plus <see cref="Flush"/>, for
    /// both of that method's reasons. The line has to reach the log whichever order the
    /// <c>BeforeStartEvent</c> handlers were subscribed in — a revert that is silent is worse than no
    /// detection, because the developer's change is gone and nothing says so — and a flush from here
    /// would report every skip outstanding, splitting another service's grouped message in two.
    /// </remarks>
    public void ReportRevertsNow(
        IServiceProvider services,
        string serviceName,
        string source,
        IReadOnlyList<string> reverts,
        bool everyRevertWasAnAddition,
        IReadOnlyList<string> registeredEndpoints)
    {
        if (reverts.Count == 0)
        {
            return;
        }

        ReportNow(services, [
            RevertReason(serviceName, source, reverts, everyRevertWasAnAddition, registeredEndpoints)]);
    }

    private static void Write(IServiceProvider services, IReadOnlyList<string> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Aspire.Hosting.ServiceSources");

        if (logger is null)
        {
            return;
        }

        // messages are already fully-composed, hand-escaped sentences (see SkipReason/RevertReason)
        // rather than ServiceTextHandler holes; forcing them back through the seam would re-escape
        // text that is already safe. Scoped exemption, not a reusable string-taking overload, so no
        // other call site can silently bypass ServiceSourcesLog's compiler-enforced holes this way.
#pragma warning disable RS0030
        foreach (var message in messages)
        {
            logger.LogWarning("{ServiceSourcesMessage}", message);
        }
#pragma warning restore RS0030
    }

    /// <summary>
    /// Explains a skip in terms of what the reader can act on: which service, which source, what was
    /// dropped, and where the source is chosen.
    /// </summary>
    private static string SkipReason(string serviceName, string source, IReadOnlyList<string> capabilities) =>
        $"Service '{new Name(serviceName)}': skipped {DescribeCalls(capabilities)} because its source is " +
        $"'{source}' — {OutOfBandSourceAdvice.SourceDetail(source)}. The service is expected to be configured wherever it actually " +
        $"runs. {SwitchSourceRemedy}";

    /// <summary>
    /// How to bring the service back under this AppHost's control, for every warning that offers it.
    /// </summary>
    /// <remarks>
    /// Names start ordering as well as configuration because <see cref="Sources.UrlSource"/> records a
    /// consumer's dropped <c>WaitFor</c> through <see cref="SkipReason"/> too, and ordering is the only
    /// half of the offer that reader lost. The exceptions offering the same switch lead into it
    /// differently, which is why only the clause itself is shared.
    /// </remarks>
    private const string SwitchSourceRemedy =
        "To make this AppHost's configuration and start ordering apply instead, " +
        OutOfBandSourceAdvice.SwitchSource + ".";

    /// <summary>
    /// Explains state that was put back: what changed, that it was undone, and what to do instead.
    /// </summary>
    /// <remarks>
    /// Its own sentence rather than <see cref="SkipReason"/>'s, because a revert is not a skip. The
    /// call landed and was undone, so "skipped" misdescribes it; and the entries count endpoints, not
    /// calls, so <see cref="DescribeCalls"/>'s "<c>N</c> calls" would state a number the developer
    /// never wrote. The remedy is shared with it, because the way back under this AppHost's control
    /// does not depend on which of the two messages is reporting.
    /// </remarks>
    // Not migrated onto the Raw/Name seam like the exception-message sites: every fragment here —
    // reverts, SourceDetail, WhereToGoInstead, SwitchSourceRemedy — is already a fully-composed,
    // safe sentence (built through Name by hand in EndpointMutationDetector/OutOfBandSourceAdvice),
    // carrying its own intentional quoting. Re-escaping a finished sentence through Raw.Escaped
    // mangles that quoting instead of protecting anything — Write logs it via a scoped RS0030
    // exemption for exactly that reason, rather than forcing it back through ServiceTextHandler,
    // which has no hole for "already safe, do not touch again".
    private static string RevertReason(
        string serviceName,
        string source,
        IReadOnlyList<string> reverts,
        bool everyRevertWasAnAddition,
        IReadOnlyList<string> registeredEndpoints) =>
        $"Service '{new Name(serviceName)}': {string.Join("; ", reverts)}. Its source is '{source}' — " +
        $"{OutOfBandSourceAdvice.SourceDetail(source)}. An out-of-band service's endpoints are fixed by its source, so " +
        $"configure the service where it actually runs. {WhereToGoInstead(source, everyRevertWasAnAddition, registeredEndpoints)}" +
        $"{SwitchSourceRemedy}";

    /// <summary>
    /// The clause for what the reader can still reach, chosen by what they were doing.
    /// </summary>
    /// <remarks>
    /// A reader who was adding an endpoint cannot be sent to a redirect setting — none of them adds
    /// one — so they are told the names that do exist. With no registered endpoint to name there is
    /// nothing true left to offer, and the clause is dropped rather than guessed at.
    /// </remarks>
    private static string WhereToGoInstead(
        string source, bool everyRevertWasAnAddition, IReadOnlyList<string> registeredEndpoints)
    {
        if (!everyRevertWasAnAddition)
        {
            return OutOfBandSourceAdvice.RedirectTheEndpoint(source) + " ";
        }

        if (registeredEndpoints.Count == 0)
        {
            return string.Empty;
        }

        var named = string.Join(", ", registeredEndpoints.Select(name => $"'{new Name(name)}'"));

        return OutOfBandSourceAdvice.TheEndpointsItHas(named) + " ";
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
