using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// The buffer between a running clone and whoever is watching it (#131): what it keeps for a
/// watcher that has not attached yet, and what it does when the two ends disagree about when the
/// clone is over.
/// </summary>
public class CheckoutProgressTests
{
    /// <summary>
    /// Everything the stream holds, once it has ended. Fails rather than hangs if it never does.
    /// </summary>
    private static async Task<IReadOnlyList<string>> DrainAsync(CheckoutProgress progress)
    {
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var lines = new List<string>();
        await foreach (var line in progress.ReadAllAsync(giveUp.Token))
        {
            lines.Add(line);
        }

        return lines;
    }

    [Fact]
    public async Task LinesWrittenBeforeAnyoneAttached_AreStillDelivered()
    {
        var progress = new CheckoutProgress();

        // The order the real thing runs in: the clone starts during composition and the resource
        // that reports it only exists once the host is up.
        progress.Report("Cloning into 'orders'...");
        progress.Report("Receiving objects: 100% (3/3), done.");
        progress.Complete();

        Assert.Equal(
            ["Cloning into 'orders'...", "Receiving objects: 100% (3/3), done."],
            await DrainAsync(progress));
    }

    [Fact]
    public async Task MoreLinesThanAreRetained_KeepsTheMostRecentOnes()
    {
        var progress = new CheckoutProgress();

        // Past the bound, which only a watcher that never attaches can reach — a clone nobody asked
        // for, whose stream would otherwise be retained in full for as long as the builder lives.
        for (var i = 0; i < 600; i++)
        {
            progress.Report($"Receiving objects: {i}");
        }

        progress.Complete();

        var lines = await DrainAsync(progress);

        // The oldest are dropped rather than the newest: git overwrites these lines in a terminal,
        // so a superseded percentage is the least informative thing in the stream — and a watcher
        // that does attach wants to know where the clone is now.
        Assert.Equal(512, lines.Count);
        Assert.Equal("Receiving objects: 88", lines[0]);
        Assert.Equal("Receiving objects: 599", lines[^1]);
    }

    [Fact]
    public async Task ALineWrittenAfterTheStreamEnded_IsDroppedRatherThanThrown()
    {
        var progress = new CheckoutProgress();
        progress.Complete();

        // Reachable: the stream is ended by whoever waited for the checkout, and a clone started
        // somewhere else can still be writing when that happens. Reporting progress must never be
        // the thing that fails a checkout, so a late line is dropped where it is written.
        progress.Report("Receiving objects:  48% (30/62)");

        Assert.Empty(await DrainAsync(progress));
    }

    [Fact]
    public async Task EndingTheStreamTwice_IsAllowed()
    {
        var progress = new CheckoutProgress();

        // Two closes is the normal case, not a defect: the clone ends its own stream as soon as it
        // finishes, and GetRepoRoot ends it again as the guarantee that it always ends.
        progress.Complete();
        progress.Complete();

        Assert.Empty(await DrainAsync(progress));
    }
}
