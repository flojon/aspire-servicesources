namespace Aspire.Hosting.ServiceSources.Tests;

public class BuilderReclaimTests
{
    /// <remarks>
    /// This only exercises that crossing the threshold doesn't throw and doesn't deadlock — it is
    /// deliberately not an assertion on the process's real inotify count. That count is a
    /// process-wide, shared resource, and the whole suite's other test classes call
    /// <c>BuilderReclaim.Track()</c> concurrently (xUnit parallelizes across classes), so a
    /// before/after delta measured from inside one test is confounded by everything else running at
    /// the same time — not a reliable pass/fail signal. The actual fix was verified externally: a
    /// throwaway reflection probe against the pinned Aspire floor confirmed dropping references and
    /// forcing a GC pass reliably releases the watcher (disposing <c>Configuration</c> alone does
    /// not), and monitoring <c>/proc/&lt;pid&gt;/fd</c> across all three TFMs' testhost processes
    /// during a real full-suite run showed peak live inotify instances topping out around 83 — see
    /// the PR notes.
    /// </remarks>
    [Fact]
    public void Track_CrossingTheThresholdManyTimesDoesNotThrow()
    {
        for (var i = 0; i < BuilderReclaim.ReclaimThreshold * 3; i++)
        {
            BuilderReclaim.Track();
        }
    }
}
