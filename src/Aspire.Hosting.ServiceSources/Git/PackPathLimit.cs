using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// The path-length limits behind the in-place or separate git directory decision (see <see cref="CloneLayout"/>).
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

    /// <summary>The same temporary pack, measured from a git directory instead of from a checkout.</summary>
    internal static readonly string LongestPackFileInGitDir = LongestPackFile[@"\.git".Length..];

    /// <summary>
    /// The longest working-tree root git for Windows ran cleanly in without the long-path opt-in:
    /// <c>repack</c>, <c>fetch</c> and <c>status</c> were quiet at 195 and 235 characters, warned
    /// "Filename too long" at 250, and the clone itself failed at 265.
    /// </summary>
    internal const int MaxWorkingTreeRootLength = 235;

    /// <summary>
    /// A git directory is <c>&lt;root&gt;\&lt;16 hex&gt;-&lt;8 hex&gt;.git</c> (29 characters), and its longest pack file
    /// sits <see cref="LongestPackFileInGitDir"/> below that.
    /// </summary>
    internal static readonly int MaxGitRootLength = MaxPathLength - 29 - LongestPackFileInGitDir.Length - 1;

    internal static bool IsUsableGitRoot(string? gitRoot) =>
        !string.IsNullOrEmpty(gitRoot) && gitRoot.Length <= MaxGitRootLength;

    internal static int Required(string checkoutPath, string stagingPath, out string longestRoot)
    {
        // Packs are written under the staging directory and later under the checkout, so both count.
        var root = Path.GetFullPath(checkoutPath);
        var staging = Path.GetFullPath(stagingPath);
        longestRoot = root.Length >= staging.Length ? root : staging;
        return longestRoot.Length + LongestPackSuffixLength;
    }

    internal static ServiceSourcesConfigurationException PackPathRefusal(Raw label, string checkoutPath, string longestRoot)
    {
        var root = Path.GetFullPath(checkoutPath);
        var required = longestRoot.Length + LongestPackSuffixLength;
        var excess = required - MaxPathLength;
        return ServiceSourcesConfigurationException.For(
            $"{label}: cannot clone into '{Raw.Display(root)}'. git for Windows cannot write a pack file past {MaxPathLength} characters, whatever core.longpaths or LongPathsEnabled say, and the temporary pack git writes when it repacks (to borrow objects while cloning, and during later fetches) would need {required} characters (under '{Raw.Display(longestRoot)}', plus {LongestPackSuffixLength} for '\\.git\\objects\\pack\\.tmp-<pid>-pack-<hash>.pack'). Move the AppHost, or the worktree it is in, to a path at least {excess} characters shorter.");
    }
}
