using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources.Tests;

public class ServiceSourcesLogTests
{
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    [Fact]
    public void Information_carries_the_escaped_message_text()
    {
        var logger = new RecordingLogger();
        var name = "bill'ing";

        ServiceSourcesLog.Information(logger, $"Service '{new Name(name)}' started.");

        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);
        Assert.Contains("bill\\u0027ing", logger.Entries[0].Message);
    }

    [Fact]
    public void Warning_carries_the_escaped_message_text()
    {
        var logger = new RecordingLogger();

        ServiceSourcesLog.Warning(logger, $"Service '{new Name("orders'")}' reverted.");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("orders\\u0027", entry.Message);
    }

    [Fact]
    public void Warning_withException_carriesTheExceptionAlongsideTheEscapedMessage()
    {
        var logger = new RecordingLogger();
        var exception = new InvalidOperationException("boom");

        ServiceSourcesLog.Warning(logger, exception, $"Checkout '{new Name("orders'")}' failed.");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Contains("orders\\u0027", entry.Message);
    }

    [Fact]
    public void Error_carries_the_escaped_message_text()
    {
        var logger = new RecordingLogger();

        ServiceSourcesLog.Error(logger, $"Service '{new Name("orders'")}' is not running.");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Contains("orders\\u0027", entry.Message);
    }

    [Fact]
    public void Error_withException_carriesTheExceptionAlongsideTheEscapedMessage()
    {
        var logger = new RecordingLogger();
        var exception = new InvalidOperationException("boom");

        ServiceSourcesLog.Error(logger, exception, $"Service '{new Name("orders'")}' failed to start.");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Contains("orders\\u0027", entry.Message);
    }

    [Fact]
    public void Debug_withException_carriesTheExceptionAlongsideTheEscapedMessage()
    {
        var logger = new RecordingLogger();
        var exception = new InvalidOperationException("boom");

        ServiceSourcesLog.Debug(logger, exception, $"Reporting '{new Name("orders'")}' stopped early: {Raw.Cause(exception)}");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Contains("orders\\u0027", entry.Message);
        Assert.Contains("boom", entry.Message);
    }

}
