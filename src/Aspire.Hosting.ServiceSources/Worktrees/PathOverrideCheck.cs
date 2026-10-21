using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Worktrees;

internal enum PathOverrideVerdict
{
    /// <summary>The same directory from both, and not main's files: the developer's own external checkout.</summary>
    Unchanged,

    /// <summary>Each worktree's own directory at the same position: the monorepo case.</summary>
    MirrorsMainWorktree,

    /// <summary>The worktree would build and edit the main worktree's files.</summary>
    IntoMainWorktree,

    /// <summary>A missing directory or, worse, a different real one.</summary>
    ResolvesDifferently,
}

/// <summary>
/// Inspects directory overrides in a just-copied <c>servicesources.local.json</c>, once.
/// </summary>
/// <remarks>
/// Reports rather than rewrites: the worktree's file stays the truth, and a rewrite would lose its comments.
/// </remarks>
internal static class PathOverrideCheck
{
    // Every block that carries a directory: path.path, repository.path and its deprecated local.path alias.
    // Each is read only under the source that uses it, so once a source is known a leftover block for
    // another source is inert. See Inspect for the case where none is known yet.
    private static readonly (string Block, string Source)[] Overrides =
    [
        .. DeveloperConfigShape.Service.BlockFields
            .Where(block => block.Value.ContainsKey(PathField))
            .Select(block => (
                block.Key,
                DeveloperConfigShape.Service.DeprecatedBlockNames.Contains(block.Key) ? RepositorySource : block.Key)),
    ];

    private const string PathField = "path";
    private const string RepositorySource = "repository";
    private const string RetiredLocalSource = "local";

    public static PathOverrideVerdict Classify(string fromHome, string fromWorktree, string mainTop, string worktreeTop)
    {
        var same = WorktreeHome.SamePath(fromHome, fromWorktree);

        // "Outside worktreeTop" matters because a worktree nested in main has its own files inside mainTop too.
        if (same && WorktreeHome.IsInside(fromWorktree, mainTop) && !WorktreeHome.IsInside(fromWorktree, worktreeTop))
        {
            return PathOverrideVerdict.IntoMainWorktree;
        }

        if (same)
        {
            return PathOverrideVerdict.Unchanged;
        }

        if (WorktreeHome.IsInside(fromHome, mainTop)
            && WorktreeHome.IsInside(fromWorktree, worktreeTop)
            && string.Equals(
                WorktreeHome.RelativeTo(fromHome, mainTop),
                WorktreeHome.RelativeTo(fromWorktree, worktreeTop),
                WorktreeHome.PathComparison))
        {
            return PathOverrideVerdict.MirrorsMainWorktree;
        }

        return PathOverrideVerdict.ResolvesDifferently;
    }

    /// <summary>One notice per override that does not resolve the way it did in the main worktree.</summary>
    /// <param name="configFile">The copied file, named in each notice.</param>
    /// <param name="fileEntries">What the copied file contributed, keyed as the builder's configuration is.</param>
    /// <param name="configured">Reads the builder's configuration, which holds every layer.</param>
    public static IReadOnlyList<Raw> Inspect(
        string configFile,
        HomeWorktree home,
        IEnumerable<KeyValuePair<string, string?>> fileEntries,
        Func<string, string?> configured)
    {
        var fromFile = fileEntries.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        var prefix = $"{DeveloperConfiguration.ServicesKey}:";
        var notices = new List<Raw>();

        var services = fromFile.Keys
            .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(key => key[prefix.Length..].Split(':')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var service in services)
        {
            var effective = configured($"{prefix}{service}:source");
            if (string.Equals(effective, RetiredLocalSource, StringComparison.OrdinalIgnoreCase))
            {
                effective = RepositorySource;
            }

            foreach (var (block, source) in Overrides)
            {
                var key = $"{prefix}{service}:{block}:{PathField}";

                // With no source named in any layer, the catalog's default decides, and it is not visible yet:
                // it lands as a configuration layer only after the file is registered. Every block is read
                // rather than guessed at, which can cost one spurious notice for a leftover block but never
                // misses a copied path that would make this worktree edit the main worktree's files.
                if ((!string.IsNullOrWhiteSpace(effective)
                        && !string.Equals(effective, source, StringComparison.OrdinalIgnoreCase))
                    || !fromFile.TryGetValue(key, out var value)
                    || string.IsNullOrWhiteSpace(value)

                    // A higher layer wins, so the copied value is not the one in effect.
                    || !string.Equals(configured(key), value, StringComparison.Ordinal))
                {
                    continue;
                }

                var fromHome = Path.GetFullPath(value, home.AppHostDirectory);
                var fromWorktree = Path.GetFullPath(value, home.WorktreeAppHostDirectory);
                var verdict = Classify(fromHome, fromWorktree, home.MainTop, home.WorktreeTop);

                if (verdict is PathOverrideVerdict.IntoMainWorktree or PathOverrideVerdict.ResolvesDifferently)
                {
                    notices.Add(Notice(service, $"{block.ToLowerInvariant()}.{PathField}", value, fromHome, fromWorktree, configFile, verdict));
                }
            }
        }

        return notices;
    }

    private static Raw Notice(
        string serviceName, string key, string value, string fromHome, string fromWorktree, string configFile,
        PathOverrideVerdict verdict)
    {
        var consequence = verdict == PathOverrideVerdict.IntoMainWorktree
            ? Raw.Literal("which is inside the main worktree, so this worktree would build and edit the main worktree's files")
            : Raw.Literal("which is a different directory");

        return Raw.Compose(
            $"Service '{new Name(serviceName)}': '{Raw.Escaped(key)}' is '{Raw.Display(value)}' in the {Raw.Literal(DeveloperConfiguration.FileName)} copied from the main worktree. " +
            $"From the main worktree's AppHost it resolves to '{Raw.Display(fromHome)}', but from this worktree's it resolves to '{Raw.Display(fromWorktree)}', {consequence}. " +
            $"If that is not what you want, edit '{Raw.Display(configFile)}'.");
    }
}
