using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Refuses, before any download, a clone whose pack file git for Windows cannot write.
/// </summary>
/// <remarks>
/// <c>core.longpaths</c> does not cover writing or renaming pack files, so a clone root that leaves
/// less than the longest pack path in MAX_PATH fails with git's unhelpful "unable to rename temporary
/// '*.pack' file" instead.
/// </remarks>
internal static class PackPathLimit
{
    /// <summary>The longest path Windows accepts without the long-path opt-in, minus the terminator.</summary>
    internal const int MaxPathLength = 259;

    /// <summary>
    /// The temporary pack a repack writes, longer than any plain pack, <c>.idx</c>, <c>.rev</c> or
    /// <c>.keep</c>. Both <c>clone --dissociate</c> and <c>gc --auto</c> repack, so a checkout must fit
    /// it to borrow objects and to be maintained. Ten digits is the widest a Windows process id can be,
    /// which also covers the two extra characters of a cruft pack's <c>.mtimes</c> for any real pid.
    /// </summary>
    internal static readonly string LongestPackFile =
        @"\.git\objects\pack\.tmp-" + new string('0', 10) + "-pack-" + new string('0', 40) + ".pack";

    internal static readonly int LongestPackSuffixLength = LongestPackFile.Length;

    internal static readonly int MaxCloneRootLength = MaxPathLength - LongestPackSuffixLength;

    /// <summary>
    /// Throws when a clone staged at <paramref name="stagingPath"/> and renamed to
    /// <paramref name="checkoutPath"/> would need a pack path past the limit.
    /// </summary>
    /// <param name="isWindows">Defaults to the running OS; injectable so the check is testable anywhere.</param>
    public static void EnsureFits(Raw label, string checkoutPath, string stagingPath, bool? isWindows = null)
    {
        if (!(isWindows ?? OperatingSystem.IsWindows()))
        {
            return;
        }

        // Packs are written under the staging directory and later under the checkout, so both count.
        var root = Path.GetFullPath(checkoutPath);
        var staging = Path.GetFullPath(stagingPath);
        var longestRoot = root.Length >= staging.Length ? root : staging;
        var required = longestRoot.Length + LongestPackSuffixLength;
        if (required <= MaxPathLength)
        {
            return;
        }

        var excess = required - MaxPathLength;
        throw ServiceSourcesConfigurationException.For(
            $"{label}: cannot clone into '{Raw.Display(root)}'. git for Windows cannot write a pack file past {MaxPathLength} characters, whatever core.longpaths or LongPathsEnabled say, and the temporary pack git writes when it repacks (to borrow objects while cloning, and during later fetches) would need {required} characters (under '{Raw.Display(longestRoot)}', plus {LongestPackSuffixLength} for '\\.git\\objects\\pack\\.tmp-<pid>-pack-<hash>.pack'). Move the AppHost, or the worktree it is in, to a path at least {excess} characters shorter.");
    }
}
