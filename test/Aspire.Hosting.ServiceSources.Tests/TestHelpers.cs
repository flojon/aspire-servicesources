using System.Threading.Channels;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.ServiceSources.Tests;

internal static class TestHelpers
{
    public static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [],
        });

    /// <summary>
    /// A builder in publish mode — the mode <c>aspire publish</c> and manifest generation run in,
    /// where the AppHost composes the model, writes the manifest and exits without ever starting a
    /// resource.
    /// </summary>
    public static IDistributedApplicationBuilder CreatePublishingBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = ["--operation", "publish"],
        });

    /// <summary>
    /// A builder that can actually publish <c>BeforeStartEvent</c>. Aspire's own
    /// <c>InitializeDcpAnnotations</c> handler runs first and validates DCP options, which a plain
    /// test builder doesn't have — harmless in a real AppHost, fatal here.
    /// </summary>
    /// <remarks>
    /// The validation only requires these to be non-empty; it never resolves them and nothing is
    /// launched, so a deliberately non-path sentinel keeps the tests OS-agnostic and stops the value
    /// from reading like a real Linux-only dependency.
    /// </remarks>
    public static IDistributedApplicationBuilder CreateBuilderThatCanStart(string appHostDirectory)
    {
        var builder = CreateBuilder(appHostDirectory);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DcpPublisher:CliPath"] = "unused-dcp-path",
            ["DcpPublisher:DashboardPath"] = "unused-dcp-path",
        });
        return builder;
    }

    public static Task PublishBeforeStartEventAsync(IDistributedApplicationBuilder builder) =>
        builder.Eventing.PublishAsync(new BeforeStartEvent(
            builder.Services.BuildServiceProvider(), new DistributedApplicationModel(builder.Resources)));

    /// <summary>
    /// The package's own log category, which is what <c>ServiceConfigurationWarnings</c> writes
    /// under.
    /// </summary>
    private const string ServiceSourcesCategory = "Aspire.Hosting.ServiceSources";

    /// <summary>
    /// Publishes <c>BeforeStartEvent</c> with a logger attached, and returns the warnings the
    /// package wrote while it ran.
    /// </summary>
    /// <remarks>
    /// Distinct from reading <c>ServiceConfigurationWarnings.Messages</c>, which is the buffer: this
    /// is what a developer would actually see at startup, so it also covers <i>whether</i> the
    /// buffer was flushed. That matters for skips recorded during the event itself, where the flush
    /// depends on which subscriber runs first.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> PublishBeforeStartEventCapturingWarningsAsync(
        IDistributedApplicationBuilder builder)
    {
        var captured = CaptureServiceSourcesWarnings(builder);

        await PublishBeforeStartEventAsync(builder);

        lock (captured)
        {
            return captured.ToArray();
        }
    }

    /// <summary>
    /// Attaches a capturing logger to <paramref name="builder"/>'s services and hands back the list
    /// the package's own warnings land in.
    /// </summary>
    private static List<string> CaptureServiceSourcesWarnings(IDistributedApplicationBuilder builder)
    {
        var captured = new List<string>();

        builder.Services.AddSingleton<ILoggerProvider>(new CapturingLoggerProvider(message =>
        {
            lock (captured)
            {
                captured.Add(message);
            }
        }));

        return captured;
    }

    /// <summary>
    /// The package's own warnings, delivered as they are written rather than collected once
    /// something has finished.
    /// </summary>
    /// <remarks>
    /// For a notice written from a background loop that runs for the life of the host: there is no
    /// task whose completion means the line has been written, so the line itself is what the test
    /// awaits.
    /// </remarks>
    public static ChannelReader<string> StreamServiceSourcesWarnings(IDistributedApplicationBuilder builder)
    {
        var channel = Channel.CreateUnbounded<string>();

        builder.Services.AddSingleton<ILoggerProvider>(
            new CapturingLoggerProvider(message => channel.Writer.TryWrite(message)));

        return channel.Reader;
    }

    private sealed class CapturingLoggerProvider(Action<string> write) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) =>
            categoryName == ServiceSourcesCategory ? new CapturingLogger(write) : NullLogger.Instance;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            write(formatter(state, exception));
        }
    }
}
