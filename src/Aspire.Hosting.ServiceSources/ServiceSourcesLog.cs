using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The one way this package writes reader-facing text to an <see cref="ILogger"/> — mirrors
/// <c>ServiceSourcesConfigurationException.For</c>'s seam, but for the logging sink rather than the
/// exception-message one.
/// </summary>
/// <remarks>
/// <c>LoggerExtensions</c>'s <c>Log*</c> overloads this package actually uses are banned everywhere
/// else (<c>BannedSymbols.txt</c>), so this class is the only place they may still be called — under
/// the same pragma-scoped exemption <c>ServiceSourcesConfigurationException.For</c> uses for its own
/// banned constructor. Every hole is a <see cref="Name"/>, a <see cref="Raw"/>, or a primitive —
/// <see cref="ServiceTextHandler"/> refuses anything else — so a caller-controlled value cannot reach
/// a log line unescaped.
/// </remarks>
internal static class ServiceSourcesLog
{
#pragma warning disable RS0030 // The one legitimate caller of the LoggerExtensions.Log* overloads this package bans.
    internal static void Information(ILogger logger, ServiceTextHandler message) =>
        logger.LogInformation("{ServiceSourcesMessage}", message.Text);

    internal static void Warning(ILogger logger, ServiceTextHandler message) =>
        logger.LogWarning("{ServiceSourcesMessage}", message.Text);

    internal static void Warning(ILogger logger, Exception exception, ServiceTextHandler message) =>
        logger.LogWarning(exception, "{ServiceSourcesMessage}", message.Text);

    internal static void Error(ILogger logger, ServiceTextHandler message) =>
        logger.LogError("{ServiceSourcesMessage}", message.Text);

    internal static void Error(ILogger logger, Exception exception, ServiceTextHandler message) =>
        logger.LogError(exception, "{ServiceSourcesMessage}", message.Text);

    internal static void Debug(ILogger logger, Exception exception, ServiceTextHandler message) =>
        logger.LogDebug(exception, "{ServiceSourcesMessage}", message.Text);

    /// <summary>
    /// The one exemption from "every hole is a <see cref="Name"/>/<see cref="Raw"/>":
    /// <c>ServiceSourcesWarnings</c> composes its skip and notice sentences across several files this
    /// task does not touch (<c>BackingServiceConfigAudit</c>, <c>ServiceConfigAudit</c>,
    /// <c>LocalProjectSource</c>, <c>ServiceSourcesConfigCache</c>), each already escaping its own
    /// caller-controlled fragments with <see cref="Name"/> by hand rather than through this seam.
    /// Forcing an already-finished sentence back through <see cref="ServiceTextHandler"/> would mean
    /// escaping it a second time — <see cref="Raw.Escaped"/> mangles the quoting those callers already
    /// applied — so this takes the finished text directly instead. It must never be handed a value
    /// that has not already been through that hand escaping.
    /// </summary>
    internal static void Warning(ILogger logger, string preEscapedMessage) =>
        logger.LogWarning("{ServiceSourcesMessage}", preEscapedMessage);
#pragma warning restore RS0030
}
