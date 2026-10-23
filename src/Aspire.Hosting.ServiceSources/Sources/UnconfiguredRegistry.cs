using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The placeholders one builder registered, and the startup prompt that asks about them.
/// </summary>
internal sealed class UnconfiguredRegistry
{
    private const string LogCategory = "Aspire.Hosting.ServiceSources";

    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, UnconfiguredRegistry> Cache = new();

    private readonly Lock _gate = new();

    private readonly List<Unconfigured> _services = [];

    private readonly List<Task> _promptTasks = [];

    private bool _subscribed;

    // Side-effect free: ConditionalWeakTable may run the factory twice and keep one result.
    public static UnconfiguredRegistry For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static _ => new UnconfiguredRegistry());

    public IReadOnlyList<Unconfigured> Services
    {
        get
        {
            lock (_gate)
            {
                return [.. _services];
            }
        }
    }

    /// <summary>The prompt tasks started at startup. Exposed for tests; nothing awaits them in a real run.</summary>
    public IReadOnlyList<Task> PromptTasks
    {
        get
        {
            lock (_gate)
            {
                return [.. _promptTasks];
            }
        }
    }

    public void Record(IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition)
    {
        lock (_gate)
        {
            _services.Add(new Unconfigured(serviceName, definition));

            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
        }

        // BeforeStartEvent, not AfterResourcesCreatedEvent: the latter waits behind every WaitFor,
        // including a consumer's wait on the placeholder this prompt exists to resolve.
        builder.Eventing.Subscribe<BeforeStartEvent>((@event, _) =>
        {
            Start(@event.Services, builder.AppHostDirectory);
            return Task.CompletedTask;
        });
    }

    public static IDeveloperConfigWriter CreateWriter(string appHostDirectory) =>
        new JsonFileDeveloperConfigWriter(appHostDirectory);

    private void Start(IServiceProvider services, string appHostDirectory)
    {
        var snapshot = Services;
        if (snapshot.Count == 0)
        {
            return;
        }

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(LogCategory) ?? NullLogger.Instance;

        if (InteractivePrompts.DisabledReason(services.GetService<IConfiguration>()) is { } disabled)
        {
            UnconfiguredPrompt.WarnNotAsked(
                logger, snapshot, Raw.Compose($"nobody is expected to answer ({Raw.Escaped(disabled)})"));
            return;
        }

        var stopping = services.GetService<IHostApplicationLifetime>()?.ApplicationStopping ?? CancellationToken.None;
        var writer = CreateWriter(appHostDirectory);

        // Not awaited, and no deadline: startup must not wait on a person, and the prompt has
        // nothing to fall back to if it gives up.
        var task = Task.Run(() => RunGuardedAsync(services, snapshot, writer, logger, stopping), CancellationToken.None);

        lock (_gate)
        {
            _promptTasks.Add(task);
        }
    }

    /// <summary>Never throws: it runs on a task nobody awaits.</summary>
    private static async Task RunGuardedAsync(
        IServiceProvider services, IReadOnlyList<Unconfigured> snapshot, IDeveloperConfigWriter writer,
        ILogger logger, CancellationToken stopping)
    {
        try
        {
            await UnconfiguredPrompt.RunAsync(services, snapshot, writer, logger, stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            try
            {
                ServiceSourcesLog.Warning(
                    logger, $"The source dialog for unconfigured services failed: {Raw.Cause(ex)} Nothing more was saved.");
            }
            catch (Exception)
            {
                // Logging is already gone; the host is going down.
            }
        }
    }
}
