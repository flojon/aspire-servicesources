using System.Runtime.CompilerServices;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Worktrees;

/// <summary>The main worktree's copy of an AppHost directory that runs from a linked worktree.</summary>
/// <param name="AppHostDirectory">The AppHost directory inside the main worktree.</param>
/// <param name="MainTop">The main worktree's root.</param>
/// <param name="WorktreeTop">The linked worktree's root.</param>
/// <param name="WorktreeAppHostDirectory">This AppHost directory, spelled the way git spells the other three.</param>
internal sealed record HomeWorktree(
    string AppHostDirectory, string MainTop, string WorktreeTop, string WorktreeAppHostDirectory);

/// <summary>
/// Finds the main worktree's copy of this AppHost directory, once per builder.
/// </summary>
internal static class WorktreeHome
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, Slot> Slots = new();

    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static HomeWorktree? TryResolve(IDistributedApplicationBuilder builder) =>
        SlotFor(builder).Resolve(builder.AppHostDirectory);

    /// <summary>Replaces git for one builder; must happen before the builder's first entry point.</summary>
    public static void UseProbe(IDistributedApplicationBuilder builder, IWorktreeProbe probe) =>
        SlotFor(builder).UseProbe(probe);

    internal static HomeWorktree? Resolve(string appHostDirectory, IWorktreeProbe probe)
    {
        var appHost = Normalize(appHostDirectory);

        // Only a '.git' file can be a linked worktree, so the main worktree and non-git AppHosts never spawn git.
        var root = PathSource.ConfinementRootOf(appHost, static () => null);
        if (root.Kind != PathSource.ConfinementRootKind.Git || !File.Exists(Path.Combine(root.Directory, ".git")))
        {
            return null;
        }

        // A submodule or --separate-git-dir clone also has a '.git' file, but its git dir is its common dir.
        if (probe.Probe(appHost) is not { } layout || layout.MainIsBare || SamePath(layout.GitDir, layout.CommonDir))
        {
            return null;
        }

        // Located through git's prefix rather than a relative path, because git reports real paths and
        // the AppHost directory may be spelled through a symlink or a short name.
        var segments = layout.Prefix.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var worktreeTop = Normalize(layout.Top);
        var worktreeAppHost = Normalize(Path.Combine([worktreeTop, .. segments]));
        var mainTop = Normalize(layout.MainTop);
        var home = Normalize(Path.Combine([mainTop, .. segments]));

        return !SamePath(home, worktreeAppHost) && Directory.Exists(home)
            ? new HomeWorktree(home, mainTop, worktreeTop, worktreeAppHost)
            : null;
    }

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    internal static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), PathComparison);

    // Compared by prefix under PathComparison: Path.GetRelativePath is case-sensitive on every Unix,
    // including macOS, whose default filesystems are not.
    internal static bool IsInside(string path, string root)
    {
        var (p, r) = (Normalize(path), Normalize(root));
        return string.Equals(p, r, PathComparison)
            || p.StartsWith(r.EndsWith(Path.DirectorySeparatorChar) ? r : r + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>The part of <paramref name="path"/> below <paramref name="root"/>; the caller has checked <see cref="IsInside"/>.</summary>
    internal static string RelativeTo(string path, string root) =>
        Normalize(path)[Normalize(root).Length..].TrimStart(Path.DirectorySeparatorChar);

    private static Slot SlotFor(IDistributedApplicationBuilder builder) =>
        Slots.GetValue(builder, static _ => new Slot());

    private sealed class Slot
    {
        private readonly Lock _gate = new();
        private IWorktreeProbe _probe = new GitWorktreeProbe();
        private bool _resolved;
        private HomeWorktree? _home;

        public void UseProbe(IWorktreeProbe probe)
        {
            lock (_gate)
            {
                if (_resolved)
                {
                    throw new InvalidOperationException(
                        "A worktree probe must be installed before the builder's first ServiceSources call.");
                }

                _probe = probe;
            }
        }

        public HomeWorktree? Resolve(string appHostDirectory)
        {
            lock (_gate)
            {
                if (!_resolved)
                {
                    _home = WorktreeHome.Resolve(appHostDirectory, _probe);
                    _resolved = true;
                }

                return _home;
            }
        }
    }
}
