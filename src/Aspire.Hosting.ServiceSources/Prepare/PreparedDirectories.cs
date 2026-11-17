using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ServiceSources.Prepare;

/// <summary>
/// The prepare steps this composition has already settled in a directory, so a second <c>path</c>
/// service pointed at the same directory does not run the same command again.
/// </summary>
/// <remarks>
/// Identical means the same normalized directory, the same command (<see cref="PrepareStep.CommandHash"/>)
/// and the same mode: a member asking for <c>always</c> next to one asking for <c>once</c> wants
/// different things, so both run. Per builder and in memory only — a later start decides from each
/// service's own marker, as before.
/// </remarks>
internal sealed class PreparedDirectories
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, PreparedDirectories> Cache = new();

    private readonly ConcurrentDictionary<(string Directory, string CommandHash, PrepareMode Mode), byte> _settled = new();

    public static PreparedDirectories For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static _ => new PreparedDirectories());

    /// <remarks>
    /// Check-then-mark is atomic only per directory: callers hold <see cref="Sources.CheckoutNameLock"/>
    /// for <paramref name="directory"/> across <see cref="IsSettled"/> and <see cref="MarkSettled"/>.
    /// Different directories hold different locks, hence the concurrent set.
    /// </remarks>
    public bool IsSettled(string directory, PrepareStep step) =>
        _settled.ContainsKey((directory, step.CommandHash, step.Mode));

    public void MarkSettled(string directory, PrepareStep step) =>
        _settled[(directory, step.CommandHash, step.Mode)] = 0;
}
