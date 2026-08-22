using System.Collections.Concurrent;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Caches <c>git credential fill</c> results per "protocol://host". An AppHost resolves its services
/// in parallel and re-fetches them on every run, so without this every service sharing a host would
/// run the helper subprocess of its own — and a helper that talks to a keychain or a browser is not
/// cheap.
/// </summary>
/// <remarks>
/// The cache is only as good as its invalidation: an entry outlives the operation that filled it, so
/// a credential the server has since refused — or one the developer has rotated — must be dropped
/// via <see cref="Forget"/> rather than replayed for the lifetime of the AppHost process.
/// </remarks>
internal sealed class GitCredentialHelperCache(Func<string, string, HelperCredentials?> fill)
{
    private readonly ConcurrentDictionary<string, Lazy<HelperCredentials?>> _entries = new(StringComparer.Ordinal);

    /// <summary>
    /// The credential for the URL's host, filling it on first use. The URL must name a host and a
    /// scheme — there is nothing for <c>git credential</c> to look up otherwise.
    /// </summary>
    public HelperCredentials? Get(GitUrl url) =>
        _entries.GetOrAdd(
            Key(url),
            _ => new Lazy<HelperCredentials?>(
                () => fill(url.Scheme!, url.Host!),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    /// <summary>
    /// Drops the cached credential for the URL's host, so the next <see cref="Get"/> re-reads
    /// whatever the developer's credential helper holds now.
    /// </summary>
    public void Forget(GitUrl url) => _entries.TryRemove(Key(url), out _);

    /// <summary>
    /// The identity <c>git credential</c> itself keys on. <see cref="GitUrl.Host"/> keeps any
    /// explicit port, so two ports on one machine stay separate entries, as git treats them.
    /// </summary>
    private static string Key(GitUrl url) => $"{url.Scheme}://{url.Host}";
}
