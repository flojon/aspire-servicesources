using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.Configuration;

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
    // repository.path and its deprecated local.path alias still resolve a directory the same way.
    // Each is read only under the source that uses it, so a leftover block for another source is inert.
    private static readonly (string Section, string Key, string Source)[] Overrides =
    [
        ("path", "path.path", "path"),
        ("repository", "repository.path", "repository"),
        ("local", "local.path", "repository"),
    ];

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
                Path.GetRelativePath(mainTop, fromHome),
                Path.GetRelativePath(worktreeTop, fromWorktree),
                WorktreeHome.PathComparison))
        {
            return PathOverrideVerdict.MirrorsMainWorktree;
        }

        return PathOverrideVerdict.ResolvesDifferently;
    }

    /// <summary>One notice per override that does not resolve the way it did in the main worktree.</summary>
    /// <param name="configured">Reads the builder's configuration, which holds every layer above the file.</param>
    public static IReadOnlyList<Raw> Inspect(string configFile, HomeWorktree home, Func<string, string?> configured)
    {
        try
        {
            var file = new ConfigurationBuilder().AddJsonFile(configFile, optional: true).Build();
            using (file as IDisposable)
            {
                var notices = new List<Raw>();

                foreach (var service in file.GetSection(DeveloperConfigFileSource.FileServicesKey).GetChildren())
                {
                    var effective = configured($"{DeveloperConfiguration.ServicesKey}:{service.Key}:source");
                    effective = string.IsNullOrWhiteSpace(effective) ? service["source"] : effective;

                    foreach (var (section, key, source) in Overrides)
                    {
                        // With no source named here, the catalog's default decides, so every block may be read.
                        if ((!string.IsNullOrWhiteSpace(effective)
                                && !string.Equals(effective, source, StringComparison.OrdinalIgnoreCase))
                            || service[$"{section}:path"] is not { } value
                            || string.IsNullOrWhiteSpace(value))
                        {
                            continue;
                        }

                        var fromHome = Path.GetFullPath(value, home.AppHostDirectory);
                        var fromWorktree = Path.GetFullPath(value, home.WorktreeAppHostDirectory);
                        var verdict = Classify(fromHome, fromWorktree, home.MainTop, home.WorktreeTop);

                        if (verdict is PathOverrideVerdict.IntoMainWorktree or PathOverrideVerdict.ResolvesDifferently)
                        {
                            notices.Add(Notice(service.Key, key, value, fromHome, fromWorktree, configFile, verdict));
                        }
                    }
                }

                return notices;
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentException)
        {
            // A malformed file is reported by registration, which reads it next.
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The copy already happened; escaping here would lose the "copied" notice and the marker for good.
            return [];
        }
    }

    private static Raw Notice(
        string serviceName, string key, string value, string fromHome, string fromWorktree, string configFile,
        PathOverrideVerdict verdict)
    {
        var consequence = verdict == PathOverrideVerdict.IntoMainWorktree
            ? Raw.Literal("which is inside the main worktree, so this worktree would build and edit the main worktree's files")
            : Raw.Literal("which is a different directory");

        return Raw.Compose(
            $"Service '{new Name(serviceName)}': '{Raw.Escaped(key)}' is '{Raw.Escaped(value)}' in the {Raw.Literal(DeveloperConfiguration.FileName)} copied from the main worktree. " +
            $"From the main worktree's AppHost it resolves to '{Raw.Escaped(fromHome)}', but from this worktree's it resolves to '{Raw.Escaped(fromWorktree)}', {consequence}. " +
            $"If that is not what you want, edit '{Raw.Escaped(configFile)}'.");
    }
}
