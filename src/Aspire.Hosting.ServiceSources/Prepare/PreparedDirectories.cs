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

    private readonly HashSet<(string Directory, string CommandHash, PrepareMode Mode)> _settled = [];

    private readonly Lock _gate = new();

    public static PreparedDirectories For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static _ => new PreparedDirectories());

    public bool IsSettled(string directory, PrepareStep step)
    {
        lock (_gate)
        {
            return _settled.Contains((directory, step.CommandHash, step.Mode));
        }
    }

    public void MarkSettled(string directory, PrepareStep step)
    {
        lock (_gate)
        {
            _settled.Add((directory, step.CommandHash, step.Mode));
        }
    }
}
