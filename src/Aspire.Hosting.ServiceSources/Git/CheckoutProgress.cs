using System.Threading.Channels;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// The progress stream of one checkout's clone, from the thread running git to whoever is watching
/// the resource(s) it belongs to.
/// </summary>
/// <remarks>
/// <para>
/// A queue rather than a callback because the two ends do not overlap in time. The clone starts
/// during composition — <see cref="Sources.LocalCheckoutPrefetch"/> kicks every <c>"local"</c>
/// checkout off on the first <c>AddService()</c> call — while the resource it reports against only
/// gains a state and a log to write to once the host is up and DCP has created it. Buffering the
/// lines written so far means a watcher that attaches late still sees the clone from its first byte
/// rather than from whenever the dashboard happened to arrive.
/// </para>
/// <para>
/// One instance per checkout (design #291: two grouped services share one), since clones of
/// different checkouts run concurrently: one writer (the thread draining that clone's stderr) and,
/// for a grouped checkout, potentially <em>several concurrent</em> readers — one per grouped service
/// watching it. <see cref="Sources.CheckoutNameLock"/> keeps two grouped services from cloning the
/// same checkout at once, so there is never more than one writer for a given instance, but it does
/// not serialize the readers: two grouped services can both be watching the identical clone at the
/// same time. Each reader gets its own channel, primed with everything reported so far and then fed
/// every subsequent line, rather than the two competing over one shared reader — a bounded
/// single-reader channel is not safe for concurrent reads, and splitting one stream of lines across
/// two readers would show each of them an incomplete log anyway.
/// </para>
/// </remarks>
internal sealed class CheckoutProgress : IGitProgressSink
{
    /// <summary>
    /// How many lines are kept for a reader that has not attached yet, and how many a slow reader is
    /// allowed to fall behind before its oldest lines are dropped.
    /// </summary>
    /// <remarks>
    /// git throttles progress to a line per percentage point per phase, so a clone's whole stream is
    /// a few hundred lines and a reader that attaches at all sees all of it. The bound is for the
    /// one that never does — a service configured <c>"local"</c> that this AppHost turns out not to
    /// add, whose speculative clone still runs — so that its stream cannot be retained in full for
    /// as long as the builder lives.
    /// </remarks>
    private const int RetainedLines = 512;

    // Plain object rather than System.Threading.Lock: this package still targets net8.0.
    private readonly object _gate = new();
    private readonly Queue<string> _retained = new();
    private readonly List<Channel<string>> _readers = [];
    private bool _completed;

    /// <remarks>
    /// Called from the single thread draining this checkout's clone — never concurrently with itself
    /// — so appending to <see cref="_retained"/> and fanning the line out to every reader under one
    /// lock is exactly as sequential as git's own output.
    /// </remarks>
    public void Report(string line)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _retained.Enqueue(line);
            while (_retained.Count > RetainedLines)
            {
                _retained.Dequeue();
            }

            foreach (var reader in _readers)
            {
                reader.Writer.TryWrite(line);
            }
        }
    }

    /// <summary>
    /// Ends the stream, whether the clone succeeded, failed, or never ran because the checkout was
    /// already there. Must happen on every one of those paths: a reader waits for this rather than
    /// polling, so a stream left open is a reader left waiting forever. Idempotent, so a second and
    /// later resolving member of a grouped checkout calling this again is harmless.
    /// </summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;

            foreach (var reader in _readers)
            {
                reader.Writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// Every line written so far and then each as it arrives, ending when the clone does. Each call
    /// gets its own independent channel — this is safe to call more than once on the same instance,
    /// which a grouped checkout (design #291) does whenever more than one member is watching.
    /// </summary>
    public IAsyncEnumerable<string> ReadAllAsync(CancellationToken cancellationToken)
    {
        // DropOldest, matching the retained-lines bound: writing must never block the thread
        // draining git's stderr, and a superseded percentage is the least informative line to keep.
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(RetainedLines)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        lock (_gate)
        {
            foreach (var line in _retained)
            {
                channel.Writer.TryWrite(line);
            }

            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _readers.Add(channel);
            }
        }

        return channel.Reader.ReadAllAsync(cancellationToken);
    }
}
