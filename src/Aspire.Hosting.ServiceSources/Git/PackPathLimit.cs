using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Refuses, before any download, a clone whose pack file git for Windows cannot write.
/// </summary>
/// <remarks>
/// <c>core.longpaths</c> does not cover writing or renaming pack files, so a clone root that leaves
/// less than the pack's own path in MAX_PATH fails with git's unhelpful "unable to rename temporary
/// '*.pack' file" instead.
/// </remarks>
internal static class PackPathLimit
{
    /// <summary>The longest path Windows accepts without the long-path opt-in, minus the terminator.</summary>
    internal const int MaxPathLength = 259;

    /// <summary>
    /// <c>\.git\objects\pack\pack-&lt;40 hex&gt;.pack</c>; the <c>.idx</c>, <c>.rev</c> and
    /// <c>.keep</c> siblings are no longer.
    /// </summary>
    internal const int LongestPackSuffixLength = 69;

    internal const int MaxCloneRootLength = MaxPathLength - LongestPackSuffixLength;

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

        // The pack is written under the staging directory and renamed with the checkout, so both count.
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
            $"{label}: cannot clone into '{Raw.Display(root)}'. git for Windows cannot write a pack file past {MaxPathLength} characters, whatever core.longpaths or LongPathsEnabled say, and this clone's longest path would be {required} characters ('{Raw.Display(longestRoot)}' plus {LongestPackSuffixLength} for '\\.git\\objects\\pack\\pack-<hash>.pack'). Move the AppHost, or the worktree it is in, to a path at least {excess} characters shorter.");
    }
}
