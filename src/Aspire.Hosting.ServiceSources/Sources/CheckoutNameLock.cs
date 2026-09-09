using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Serializes the two mutations a managed checkout undergoes after its clone lands —
/// <see cref="Git.LocalGitCheckout.ReconcileRepoRoot"/>'s fetch-and-checkout onto the configured ref,
/// and <see cref="Prepare.CheckoutPreparation.Run"/>'s bootstrap command — across every service
/// sharing that checkout, keyed on
/// <see cref="Config.Catalog.RepositoryDefinition.CheckoutName"/>.
/// </summary>
/// <remarks>
/// <para>
/// Grouping (design #291) is what makes this load-bearing rather than defensive. Before it, one
/// <c>CheckoutName</c> meant one service, and each of the two callers below — the eager path in
/// <c>LocalProjectSource.Resolve</c> and the deferred one in
/// <c>DeferredCheckout.StartDeferredAsync</c> — only ever touched a working tree nothing else was
/// touching at the same time. A shared repository breaks that: two grouped services' background
/// tasks (or one background task racing the eager path's own composition-thread call) can now reach
/// <c>ReconcileRepoRoot</c> or <c>CheckoutPreparation.Run</c> for the identical directory at once —
/// two fetches racing, or a bootstrap command starting while the working tree is mid-checkout under
/// it. Held across both calls, keyed on the checkout the two services share rather than on either
/// service's own name, so only one of them is ever inside that span for a given repository at a time.
/// </para>
/// <para>
/// Taken by the eager path too, even though nothing races there today — <c>AddService()</c> calls run
/// one at a time on the composition thread. Cheap, and it keeps the invariant true independent of
/// call order rather than true only by the current shape of the two callers.
/// </para>
/// <para>
/// One semaphore per <c>CheckoutName</c>, created on first use and never removed — the set of
/// distinct checkouts an AppHost run touches is bounded by its own catalog, the same reasoning
/// <see cref="LocalCheckoutPrefetch"/>'s own dictionaries rely on.
/// </para>
/// </remarks>
internal sealed class CheckoutNameLock
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, CheckoutNameLock> Cache = new();

    private readonly Dictionary<string, SemaphoreSlim> _semaphores = new(StringComparer.Ordinal);

    // Plain object rather than System.Threading.Lock: this package still targets net8.0.
    private readonly object _gate = new();

    public static CheckoutNameLock For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static _ => new CheckoutNameLock());

    /// <summary>
    /// Acquires the lock for <paramref name="checkoutName"/>, released by disposing the result — for
    /// the deferred path, which already runs on a background task and can afford to wait.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string checkoutName, CancellationToken cancellationToken)
    {
        var semaphore = SemaphoreFor(checkoutName);
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Release(semaphore);
    }

    /// <summary>
    /// The synchronous counterpart, for the eager path — composition's own thread, which has nothing
    /// to overlap the wait with anyway.
    /// </summary>
    public IDisposable Acquire(string checkoutName)
    {
        var semaphore = SemaphoreFor(checkoutName);
        semaphore.Wait();
        return new Release(semaphore);
    }

    private SemaphoreSlim SemaphoreFor(string checkoutName)
    {
        lock (_gate)
        {
            if (!_semaphores.TryGetValue(checkoutName, out var semaphore))
            {
                _semaphores[checkoutName] = semaphore = new SemaphoreSlim(1, 1);
            }

            return semaphore;
        }
    }

    private sealed class Release(SemaphoreSlim semaphore) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            // Guarded: a caller that disposed twice would otherwise over-release the semaphore,
            // letting a second holder in alongside the first.
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                semaphore.Release();
            }
        }
    }
}
