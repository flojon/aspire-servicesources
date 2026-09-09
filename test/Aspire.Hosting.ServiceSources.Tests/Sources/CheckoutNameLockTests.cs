using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// The keyed lock that keeps two grouped services (#291) from reconciling or preparing the same
/// managed checkout at once — <see cref="LocalProjectSource"/> and <see cref="DeferredCheckout"/> are
/// its two real callers, exercised in isolation here.
/// </summary>
public class CheckoutNameLockTests
{
    private static readonly TimeSpan Rendezvous = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Acquire_SameCheckoutName_BlocksASecondCallerUntilTheFirstReleases()
    {
        var checkoutLock = new CheckoutNameLock();
        var holder = checkoutLock.Acquire("monorepo");

        var second = Task.Run(() => checkoutLock.Acquire("monorepo"));

        // Nothing to observe but "still waiting" — Acquire blocks the calling thread, so a second
        // caller for the identical key must not have returned yet.
        await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.False(second.IsCompleted, "a second caller for the same CheckoutName acquired before the first released");

        holder.Dispose();

        var secondHolder = await second.WaitAsync(Rendezvous);
        secondHolder.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_SameCheckoutName_SerializesTheSameWay()
    {
        var checkoutLock = new CheckoutNameLock();
        var holder = await checkoutLock.AcquireAsync("monorepo", CancellationToken.None);

        var second = checkoutLock.AcquireAsync("monorepo", CancellationToken.None);

        await Task.WhenAny(second, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.False(second.IsCompleted, "a second waiter for the same CheckoutName acquired before the first released");

        holder.Dispose();

        var secondHolder = await second.WaitAsync(Rendezvous);
        secondHolder.Dispose();
    }

    [Fact]
    public void Acquire_DifferentCheckoutNames_DoNotBlockEachOther()
    {
        var checkoutLock = new CheckoutNameLock();

        using var monorepo = checkoutLock.Acquire("monorepo");
        using var other = checkoutLock.Acquire("other-repo");

        // Reaching here at all is the assertion: a second Acquire for a different key must not wait
        // on the first key's holder.
    }

    [Fact]
    public void Dispose_CalledTwice_ReleasesOnlyOnce()
    {
        var checkoutLock = new CheckoutNameLock();
        var holder = checkoutLock.Acquire("monorepo");

        holder.Dispose();
        holder.Dispose();

        // A third Acquire must still see exactly one release's worth of availability — a double
        // release would have let two callers in at once, which nothing here would observe directly,
        // so what this proves is only that a second Acquire is not left permanently blocked.
        using var acquired = checkoutLock.Acquire("monorepo");
    }
}
