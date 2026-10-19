using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Refuses, before cloning, a managed checkout whose pack files Windows cannot write.
/// </summary>
/// <remarks>
/// Git for Windows' <c>core.longpaths</c> does not cover writing or renaming pack files, so past
/// MAX_PATH every clone fails with git's raw "unable to write file" error whatever the setting says.
/// SHA-1 object names are assumed: a SHA-256 repository's pack names are 24 characters longer.
/// </remarks>
internal static class PackPathLimit
{
    /// <summary>MAX_PATH less the terminating NUL.</summary>
    public const int MaxLength = 259;

    /// <summary>The longest pack file a plain clone writes; <c>.idx</c>, <c>.rev</c> and <c>.keep</c> are no longer.</summary>
    private static readonly string LongestPackFile =
        @"\.git\objects\pack\pack-" + new string('0', 40) + ".pack";

    /// <summary>
    /// The temporary pack a repack writes, which <c>clone --dissociate</c> runs. Ten digits is the
    /// widest a Windows process id can be.
    /// </summary>
    private static readonly string LongestRepackFile =
        @"\.git\objects\pack\.tmp-" + new string('0', 10) + "-pack-" + new string('0', 40) + ".pack";

    /// <summary>
    /// The longest pack-file path a clone into <paramref name="repoRoot"/> needs: the clone is staged
    /// under <paramref name="checkoutsRoot"/> and later fetches write into <paramref name="repoRoot"/>.
    /// </summary>
    public static string LongestPackPath(string checkoutsRoot, string repoRoot)
    {
        var staging = ScratchDirectory.PlaceholderPath(checkoutsRoot);
        return FullDirectory(staging.Length > repoRoot.Length ? staging : repoRoot) + LongestPackFile;
    }

    /// <summary>
    /// Whether a reference clone's repack fits; one that does not falls back to a second, plain
    /// download, so it is better not attempted.
    /// </summary>
    /// <remarks>Only the staging directory counts: the clone, and so its repack, runs there.</remarks>
    public static bool RepackFits(string checkoutsRoot, bool isWindows) =>
        !isWindows || (FullDirectory(ScratchDirectory.PlaceholderPath(checkoutsRoot)) + LongestRepackFile).Length <= MaxLength;

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
            $"{label}: cannot clone into '{Raw.Escaped(repoRoot)}': git's pack files there need paths of {longest.Length} characters ('{Raw.Escaped(longest)}'), and Windows limits them to {MaxLength} even with core.longpaths or LongPathsEnabled. Move the repository or git worktree that holds the AppHost (the directory that holds '.servicesources') to a directory at least {longest.Length - MaxLength} characters shorter.");
    }

    private static string FullDirectory(string directory) =>
        Path.GetFullPath(directory).TrimEnd('\\', '/');
}
