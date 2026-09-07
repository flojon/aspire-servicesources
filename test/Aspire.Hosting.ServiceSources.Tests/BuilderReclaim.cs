namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Bounds how many undisposed test <c>IDistributedApplicationBuilder</c>s a run lets accumulate.
/// </summary>
/// <remarks>
/// <c>DistributedApplicationBuilder</c> is not itself disposable, so nothing under <c>using var</c>
/// releases it. Its <c>Configuration</c> is a disposable <c>ConfigurationManager</c> that registers
/// the <c>reloadOnChange</c> file provider consuming one inotify instance per builder on Linux — but
/// this class does not call that <c>Dispose()</c>: a batch large enough to be worth reclaiming
/// spans many tests, and xUnit runs test classes in parallel, so by the time a batch's threshold is
/// crossed some of its builders can still be in active use by a test that hasn't returned yet.
/// Disposing those would corrupt a running test rather than a finished one. Forcing a GC pass has no
/// such risk — the collector only reclaims what is genuinely unreachable, which is exactly the
/// builders whose owning test already returned and dropped its last reference. That collection is
/// also what actually releases the inotify handle; disposing <c>Configuration</c> first was tried
/// and measured to not reliably do that on its own (see the PR notes). Tracking every builder at
/// creation time and forcing a collection every <see cref="ReclaimThreshold"/> of them keeps the
/// live set bounded without any test having to opt in. Linked into the Java and JavaScript test
/// projects rather than duplicated — see their <c>.csproj</c> files.
/// </remarks>
internal static class BuilderReclaim
{
    /// <summary>
    /// How many tracked builders accumulate before a GC pass runs. <c>fs.inotify.max_user_instances</c>
    /// is a per-user ceiling (its default is 128), not a per-process one, so several test project
    /// processes running at once share the same budget. Bounding each process's own live set to a
    /// small multiple of this keeps that shared total far under the ceiling even alongside a
    /// developer's own tooling, without forcing a GC pass on every single test.
    /// </summary>
    internal const int ReclaimThreshold = 20;

    private static readonly object Gate = new();
    private static int _pending;

    public static void Track()
    {
        bool shouldReclaim;

        lock (Gate)
        {
            _pending++;
            shouldReclaim = _pending >= ReclaimThreshold;

            if (shouldReclaim)
            {
                _pending = 0;
            }
        }

        if (shouldReclaim)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }
}
