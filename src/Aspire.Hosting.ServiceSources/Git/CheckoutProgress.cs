using System.Threading.Channels;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// The progress stream of one service's clone, from the thread running git to whoever is watching
/// the resource it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// A queue rather than a callback because the two ends do not overlap in time. The clone starts
/// during composition — <see cref="Sources.LocalCheckoutPrefetch"/> kicks every <c>"local"</c>
/// checkout off on the first <c>AddService()</c> call — while the resource it reports against only
/// gains a state and a log to write to once the host is up and DCP has created it. Buffering the
/// lines written in between means the logs show the clone from its first byte rather than from
/// whenever the dashboard happened to arrive.
/// </para>
/// <para>
/// One instance per service, since the clones run concurrently: one writer (the thread draining
/// that clone's stderr) and one reader (the background task that starts that service).
/// </para>
/// </remarks>
internal sealed class CheckoutProgress : IGitProgressSink
{
    /// <summary>
    /// How many lines are kept for a reader that has not attached yet.
    /// </summary>
    /// <remarks>
    /// git throttles progress to a line per percentage point per phase, so a clone's whole stream is
    /// a few hundred lines and a reader that attaches at all sees all of it. The bound is for the
    /// one that never does — a service configured <c>"local"</c> that this AppHost turns out not to
    /// add, whose speculative clone still runs — so that its stream cannot be retained in full for
    /// as long as the builder lives.
    /// </remarks>
    private const int RetainedLines = 512;

    /// <remarks>
    /// <see cref="BoundedChannelFullMode.DropOldest"/> so that writing never blocks the thread
    /// draining git's stderr — filling that pipe would stall the clone itself — and so that what
    /// survives the bound is the most recent progress. Dropping from the front is the right end to
    /// lose: in a terminal git overwrites these lines, and a superseded percentage is the least
    /// informative thing in the stream.
    /// </remarks>
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(
        new BoundedChannelOptions(RetainedLines)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    public void Report(string line) => _lines.Writer.TryWrite(line);

    /// <summary>
    /// Ends the stream, whether the clone succeeded, failed, or never ran because the checkout was
    /// already there. Must happen on every one of those paths: a reader waits for this rather than
    /// polling, so a stream left open is a reader left waiting forever.
    /// </summary>
    public void Complete() => _lines.Writer.TryComplete();

    /// <summary>
    /// Every line written so far and then each as it arrives, ending when the clone does.
    /// </summary>
    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken) =>
        _lines.Reader.ReadAllAsync(cancellationToken);
}
