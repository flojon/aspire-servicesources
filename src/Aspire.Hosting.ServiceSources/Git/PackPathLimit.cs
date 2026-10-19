using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Refuses, before cloning, a managed checkout whose pack files Windows cannot write.
/// </summary>
/// <remarks>
/// Git for Windows' <c>core.longpaths</c> does not cover writing or renaming pack files, so past
/// MAX_PATH every clone fails with git's raw "unable to write file" error whatever the setting says.
/// </remarks>
internal static class PackPathLimit
{
    /// <summary>MAX_PATH less the terminating NUL.</summary>
    public const int MaxLength = 259;

    /// <summary>The longest pack file git writes; <c>.idx</c>, <c>.rev</c> and <c>.keep</c> are no longer.</summary>
    private static readonly string LongestPackFile =
        @"\.git\objects\pack\pack-" + new string('0', 40) + ".pack";

    /// <summary>
    /// The longest pack-file path a clone into <paramref name="repoRoot"/> needs: the clone is staged
    /// under <paramref name="checkoutsRoot"/> and later fetches write into <paramref name="repoRoot"/>.
    /// </summary>
    public static string LongestPackPath(string checkoutsRoot, string repoRoot)
    {
        var staging = ScratchDirectory.PlaceholderPath(checkoutsRoot);
        var directory = Path.GetFullPath(staging.Length > repoRoot.Length ? staging : repoRoot);
        return directory.TrimEnd('\\', '/') + LongestPackFile;
    }

    /// <exception cref="ServiceSourcesConfigurationException">
    /// <paramref name="isWindows"/> and the longest pack path exceeds <see cref="MaxLength"/>.
    /// </exception>
    public static void EnsureFits(Raw label, string checkoutsRoot, string repoRoot, bool isWindows)
    {
        if (!isWindows)
        {
            return;
        }

        var longest = LongestPackPath(checkoutsRoot, repoRoot);
        if (longest.Length <= MaxLength)
        {
            return;
        }

        throw ServiceSourcesConfigurationException.For(
            $"{label}: cannot clone into '{Raw.Escaped(repoRoot)}': git's pack files there need paths of {longest.Length} characters ('{Raw.Escaped(longest)}'), and Windows limits them to {MaxLength} even with core.longpaths or LongPathsEnabled. Move the AppHost, or the git worktree it is in, to a directory at least {longest.Length - MaxLength} characters shorter.");
    }
}
