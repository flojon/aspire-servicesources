using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Worktrees;

/// <summary>What git says about the worktree a directory is in.</summary>
/// <param name="Top">The directory's worktree root, in git's spelling.</param>
/// <param name="Prefix">The directory relative to its worktree's root, '/'-separated, as <c>--show-prefix</c> prints it.</param>
internal readonly record struct WorktreeLayout(
    string GitDir, string CommonDir, string Top, string Prefix, string MainTop, bool MainIsBare);

/// <summary>
/// Asks git about a directory's worktree.
/// </summary>
/// <remarks>
/// Separate from <see cref="IGitClient"/> because config seeding runs before the source registry,
/// where the git clients live, exists.
/// </remarks>
internal interface IWorktreeProbe
{
    /// <summary>The layout, or <see langword="null"/> when git cannot say.</summary>
    WorktreeLayout? Probe(string directory);
}

internal sealed class GitWorktreeProbe(IReadOnlyDictionary<string, string?>? environmentOverrides = null) : IWorktreeProbe
{
    public WorktreeLayout? Probe(string directory)
    {
        try
        {
            var revParse = GitCommand.Run(
                ["-C", directory, "rev-parse", "--path-format=absolute",
                    "--git-dir", "--git-common-dir", "--show-toplevel", "--show-prefix"],
                environmentOverrides);
            if (!revParse.Succeeded)
            {
                return null;
            }

            // Not trimmed as a whole: at the worktree root the prefix is an empty last line.
            var lines = revParse.StandardOutput.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

            // Git older than 2.31 echoes the unknown --path-format back as output rather than failing.
            if (lines.Length < 4 || lines[0].StartsWith('-'))
            {
                return null;
            }

            var list = GitCommand.Run(["-C", directory, "worktree", "list", "--porcelain"], environmentOverrides);
            if (!list.Succeeded || ParseMainWorktree(list.StandardOutput) is not { } main)
            {
                return null;
            }

            return new WorktreeLayout(lines[0], lines[1], lines[2], lines[3], main.Top, main.IsBare);
        }
        catch (GitUnavailableException)
        {
            return null;
        }
    }

    /// <summary>The first record of <c>git worktree list --porcelain</c>, which is always the main worktree.</summary>
    internal static (string Top, bool IsBare)? ParseMainWorktree(string porcelain)
    {
        const string WorktreePrefix = "worktree ";
        string? top = null;
        var bare = false;

        foreach (var raw in porcelain.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (top is null)
            {
                if (!line.StartsWith(WorktreePrefix, StringComparison.Ordinal))
                {
                    return null;
                }

                top = line[WorktreePrefix.Length..];
            }
            else if (line.Length == 0)
            {
                break;
            }
            else if (line == "bare")
            {
                bare = true;
            }
        }

        return top is null ? null : (top, bare);
    }
}
