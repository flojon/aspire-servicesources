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
                return _skips
                    .GroupBy(skip => (skip.ServiceName, skip.Source))
                    .Select(group => SkipReason(
                        group.Key.ServiceName, group.Key.Source, [.. group.Select(skip => skip.Capability)]))
                    .ToArray();
            }
        }
    }

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

    private void Flush(IServiceProvider services)
    {
        var messages = Messages;

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
               "configuration to apply.";
    }

    /// <summary>
    /// A single call reads as itself; several read as a count plus a per-capability tally, so the
    /// message stays one line however many calls it stands for.
    /// </summary>
    private static string DescribeCalls(IReadOnlyList<string> capabilities)
    {
        if (capabilities.Count == 1)
        {
            return capabilities[0];
        }

        var tally = capabilities
            .GroupBy(capability => capability, StringComparer.Ordinal)
            .Select(group => group.Count() == 1 ? group.Key : $"{group.Key} ×{group.Count()}");

        return $"{capabilities.Count} Configure calls ({string.Join(", ", tally)})";
    }
}
