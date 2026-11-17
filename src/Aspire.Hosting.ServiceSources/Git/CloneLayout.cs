using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

internal enum CloneLayoutKind
{
    InPlace,
    Separate,
}

/// <param name="GitRoot">Where separate git directories live; <see langword="null"/> when there is no usable one.</param>
/// <param name="IsWindows">Defaults to the running OS; injectable so the decision is testable anywhere.</param>
/// <param name="ForceSeparate">Replaces the Windows gate and the in-place fit, so other platforms can exercise the separate layout.</param>
/// <param name="GitDirLimit">The longest git directory path git accepts; injectable because it is a compile-time constant of the git build.</param>
internal sealed record CloneLayoutOptions(
    string? GitRoot,
    bool? IsWindows = null,
    bool ForceSeparate = false,
    int GitDirLimit = GitPathLimit.MaxGitDirLength)
{
    public static CloneLayoutOptions Default => new(SeparateGitDirectories.DefaultRoot());
}

/// <summary>
/// Decides whether a managed clone keeps its git directory in place or in a short separate one.
/// </summary>
internal static class CloneLayout
{
    public static CloneLayoutKind Decide(Raw label, string checkoutPath, string stagingPath, CloneLayoutOptions options)
    {
        if (!(options.IsWindows ?? OperatingSystem.IsWindows()) && !options.ForceSeparate)
        {
            return CloneLayoutKind.InPlace;
        }

        var longestRoot = GitPathLimit.LongestRoot(checkoutPath, stagingPath);
        if (!options.ForceSeparate && longestRoot.Length <= GitPathLimit.MaxInPlaceRoot(options.GitDirLimit))
        {
            return CloneLayoutKind.InPlace;
        }

        if (longestRoot.Length > GitPathLimit.MaxWorkingTreeRootLength)
        {
            var excess = longestRoot.Length - GitPathLimit.MaxWorkingTreeRootLength;
            throw ServiceSourcesConfigurationException.For(
                $"{label}: cannot clone into '{Raw.Display(Path.GetFullPath(checkoutPath))}'. git for Windows cannot find a repository whose '.git' path passes {GitPathLimit.MaxGitFilePathLength} characters, so a working tree directory path is limited to about {GitPathLimit.MaxWorkingTreeRootLength} characters, and the clone would be staged at '{Raw.Display(longestRoot)}' ({longestRoot.Length} characters). Move the AppHost, or the worktree it is in, to a path at least {GitPathLimit.Shorter(excess)} shorter.");
        }

        if (!GitPathLimit.IsUsableGitRoot(options.GitRoot))
        {
            throw GitPathLimit.GitRootRefusal(label, checkoutPath, longestRoot, options.GitDirLimit, options.GitRoot);
        }

        return CloneLayoutKind.Separate;
    }
}
