using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Collects configuration that was skipped because the service resolved to an out-of-band source,
/// and reports it once the app host has a logger.
/// </summary>
/// <remarks>
/// Skipping rather than throwing is what keeps a shared <c>Program.cs</c> working when one developer
/// flips a service to <c>"kubernetes"</c> or <c>"url"</c> in their own
/// <c>servicesources.local.json</c> — the whole point of this package. But silently dropping
/// configuration is the failure mode issue #53 was filed about, so every skip is reported.
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
internal sealed class ServiceConfigurationWarnings
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, ServiceConfigurationWarnings> Cache = new();

    private readonly object _gate = new();

    private readonly List<Skip> _skips = [];

    /// <summary>
    /// Notices that are already whole sentences, buffered for the same reason a skip is: there is no
    /// <see cref="ILogger"/> during <c>AddService()</c>.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="_skips"/> because those are grouped and rephrased per (service,
    /// source) — the shape a couple of dozen <c>Configure</c> calls needs — and a notice about
    /// something else entirely has nothing to group with. The <c>prepare</c> step's is the first:
    /// a <c>path</c> service does not inherit its catalog's step, and the notice says which command
    /// was not run so it can be copied into the developer's own file.
    /// </remarks>
    private readonly List<string> _notices = [];

    /// <summary>
    /// How much of <see cref="_skips"/> <see cref="Flush"/> has already reported. A count rather
    /// than a per-skip flag because the list is only ever appended to.
    /// </summary>
    private int _reported;

    /// <summary>The same for <see cref="_notices"/>.</summary>
    private int _noticesReported;

    private bool _subscribed;

    private sealed record Skip(string ServiceName, string Source, string Capability);

    /// <summary>
    /// Everything reported so far, one message per (service, source), for tests — the log itself
    /// isn't observable in-process.
    /// </summary>
    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_gate)
            {
                return [.. Describe(_skips), .. _notices];
            }
        }
    }

    /// <summary>
    /// One message per (service, source) over <paramref name="skips"/>.
    /// </summary>
    private static IReadOnlyList<string> Describe(IEnumerable<Skip> skips) =>
        skips
            .GroupBy(skip => (skip.ServiceName, skip.Source))
            .Select(group => SkipReason(
                group.Key.ServiceName, group.Key.Source, [.. group.Select(skip => skip.Capability)]))
            .ToArray();

    public static ServiceConfigurationWarnings For(IDistributedApplicationBuilder builder)
    {
        // The factory stays free of side effects: ConditionalWeakTable.GetValue may run it
        // concurrently for the same key and keep only one of the results, so subscribing in there
        // could leave a discarded instance's subscription behind — flushing warnings nobody added.
        var warnings = Cache.GetValue(builder, static _ => new ServiceConfigurationWarnings());

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
            _skips.Add(new Skip(serviceName, source, capability));
        }
    }

    /// <summary>
    /// Records <paramref name="notice"/> to be written verbatim once there is a logger.
    /// </summary>
    /// <remarks>
    /// For a notice that already names its own service and its own remedy, so there is nothing here
    /// to group it with or rephrase it into.
    /// </remarks>
    public void AddNotice(string notice)
    {
        lock (_gate)
        {
            // Once per AppHost, however many times it is recorded. The prepare notice is settled per
            // service from configuration, so a service resolved twice would produce the identical
            // sentence twice, which reads as two problems.
            if (!_notices.Contains(notice, StringComparer.Ordinal))
            {
                _notices.Add(notice);
            }
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
            messages = [.. Describe(_skips.Skip(_reported)), .. _notices.Skip(_noticesReported)];
            _reported = _skips.Count;
            _noticesReported = _notices.Count;
        }

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
