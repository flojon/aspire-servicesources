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
/// </remarks>
internal sealed class ServiceConfigurationWarnings
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, ServiceConfigurationWarnings> Cache = new();

    private readonly List<string> _messages = [];

    /// <summary>Everything reported so far, for tests — the log itself isn't observable in-process.</summary>
    public IReadOnlyList<string> Messages => _messages;

    public static ServiceConfigurationWarnings For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static b =>
        {
            var warnings = new ServiceConfigurationWarnings();
            b.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
            {
                warnings.Flush(@event.Services);
                return Task.CompletedTask;
            });
            return warnings;
        });

    public void Add(string message) => _messages.Add(message);

    private void Flush(IServiceProvider services)
    {
        if (_messages.Count == 0)
        {
            return;
        }

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger("Aspire.Hosting.ServiceSources");

        foreach (var message in _messages)
        {
            logger?.LogWarning("{ServiceSourcesWarning}", message);
        }
    }
}
