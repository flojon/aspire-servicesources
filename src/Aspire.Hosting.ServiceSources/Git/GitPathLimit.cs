using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// The path-length limits behind the in-place or separate git directory decision (see <see cref="CloneLayout"/>).
/// </summary>
/// <remarks>
/// git refuses to start with "'$GIT_DIR' too big" once the git directory path passes
/// <see cref="MaxGitDirLength"/>. That is setup.c's <c>PATH_MAX - 40</c> with Git for Windows' PATH_MAX
/// of 260, fixed when git is compiled, so <c>core.longpaths</c> and <c>LongPathsEnabled</c> do not move it.
/// Measured on git 2.52.0.windows.1 only; other versions are not known to behave the same.
/// </remarks>
internal static class GitPathLimit
{
    internal const int GitPathMax = 260;

    internal const int GitDirReserve = 40;

    internal const int MaxGitDirLength = GitPathMax - GitDirReserve;

    internal const string GitDirectorySuffix = @"\.git";

    /// <summary>The longest checkout root whose own <c>.git</c> directory still passes git's check.</summary>
    internal const int MaxInPlaceRootLength = MaxGitDirLength - 5;

    /// <summary>
    /// The longest working-tree root git for Windows ran cleanly in with a short separate git directory
    /// and without the long-path opt-in: <c>repack</c>, <c>fetch</c> and <c>status</c> were quiet at 195
    /// and 235 characters, warned "Filename too long" at 250, and the clone itself failed at 265.
    /// </summary>
    internal const int MaxWorkingTreeRootLength = 235;

    /// <summary>A git directory is <c>&lt;root&gt;\&lt;16 hex&gt;-&lt;8 hex&gt;.git</c>, 29 characters below its root.</summary>
    internal const int SeparateGitDirectoryNameLength = 29;

    internal const int MaxGitRootLength = MaxGitDirLength - SeparateGitDirectoryNameLength - 1;

    internal static bool IsUsableGitRoot(string? gitRoot) =>
        !string.IsNullOrEmpty(gitRoot) && gitRoot.Length <= MaxGitRootLength;

    internal static int MaxInPlaceRoot(int gitDirLimit) => gitDirLimit - GitDirectorySuffix.Length;

    /// <summary>The longer of the final checkout and its staging directory, since the git directory sits under both in turn.</summary>
    internal static string LongestRoot(string checkoutPath, string stagingPath)
    {
        var root = Path.GetFullPath(checkoutPath);
        var staging = Path.GetFullPath(stagingPath);
        return root.Length >= staging.Length ? root : staging;
    }

    internal static ServiceSourcesConfigurationException GitRootRefusal(Raw label, string checkoutPath, string longestRoot, int gitDirLimit)
    {
        var inPlace = MaxInPlaceRoot(gitDirLimit);
        return ServiceSourcesConfigurationException.For(
            $"{label}: cannot clone into '{Raw.Display(Path.GetFullPath(checkoutPath))}'. git for Windows refuses to run in a repository whose '.git' directory path passes {gitDirLimit} characters (\"'$GIT_DIR' too big\"), whatever core.longpaths or LongPathsEnabled say, so a clone under '{Raw.Display(longestRoot)}' ({longestRoot.Length} characters; at most {inPlace} fit) needs its git directory kept elsewhere. The short directory for that, '%LOCALAPPDATA%\\ServiceSources\\git', cannot be used here. Move the AppHost, or the worktree it is in, to a path at least {longestRoot.Length - inPlace} characters shorter.");
    }
}
