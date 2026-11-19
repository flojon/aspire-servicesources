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
    /// The longest working-tree root kept with a margin. git for Windows ran cleanly with a short separate git
    /// directory up to 254 characters; at 255 the clone worked but every later command failed to find the repository
    /// (<c>&lt;root&gt;\.git</c> reaching 260), and at 265 the clone itself failed. 235 leaves 19 characters of margin.
    /// </summary>
    internal const int MaxWorkingTreeRootLength = 235;

    /// <summary>A git directory is <c>&lt;root&gt;\&lt;16 hex&gt;-&lt;8 hex&gt;.git</c>, 29 characters below its root.</summary>
    internal const int SeparateGitDirectoryNameLength = 29;

    internal const int MaxGitRootLength = MaxGitDirLength - SeparateGitDirectoryNameLength - 1;

    /// <summary>The git for Windows limit on the <c>.git</c> path itself (<c>PATH_MAX - 1</c>), reached by a working tree at 255.</summary>
    internal const int MaxGitFilePathLength = GitPathMax - 1;

    /// <summary>What a staging directory adds to the AppHost directory: <c>\.servicesources\checkouts\.incoming-&lt;id&gt;</c>.</summary>
    internal static readonly int StagingSuffixLength =
        1 + ToolDirectory.Name.Length
        + 1 + LocalGitCheckout.CheckoutsDirectoryName.Length
        + 1 + ScratchDirectory.Prefix.Length + ScratchDirectory.IdLength;

    /// <summary>Holds while staging is the longer path, that is for checkout names of 18 characters or fewer.</summary>
    internal static readonly int MaxInPlaceAppHostLength = MaxInPlaceRootLength - StagingSuffixLength;

    internal static readonly int MaxSeparateAppHostLength = MaxWorkingTreeRootLength - StagingSuffixLength;

    /// <summary>What a staging directory adds to a checkouts directory: <c>\.incoming-&lt;id&gt;</c>.</summary>
    internal static readonly int StagingInCheckoutsLength = 1 + ScratchDirectory.Prefix.Length + ScratchDirectory.IdLength;

    /// <summary>The longest checkouts directory whose staging directory still fits a root limit.</summary>
    internal static int MaxCheckoutsRoot(int rootLimit) => rootLimit - StagingInCheckoutsLength;

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

    internal static Raw Shorter(int characters) =>
        characters == 1 ? Raw.Literal("1 character") : Raw.Compose($"{characters} characters");

    /// <summary>
    /// How to make a path <paramref name="excess"/> characters too long fit: for the default location
    /// move the AppHost or relocate the checkouts, for an external one shorten the setting.
    /// </summary>
    internal static Raw ShortenAdvice(CheckoutsLocation location, int excess, int rootLimit) =>
        location.IsExternal
            ? Raw.Compose($"Shorten '{Raw.Literal(Config.DeveloperConfiguration.CheckoutsDirectoryKey)}' by at least {Shorter(excess)}: its value, plus the '\\<hash>\\checkouts' the tool appends, gives '{Raw.Display(location.CheckoutsRoot)}'.")
            : Raw.Compose($"Move the AppHost, or the worktree it is in, to a path at least {Shorter(excess)} shorter, or set '{Raw.Literal(Config.DeveloperConfiguration.CheckoutsDirectoryKey)}' (environment variable '{Raw.Literal(CheckoutsLocation.EnvironmentVariable)}') to a short directory such as '{Raw.Literal(CheckoutsLocation.SuggestedDirectory)}'.");

    /// <summary>What a too-deep checkout kept in place needs before it can be cloned again with a short git directory.</summary>
    internal static Raw ExistingCheckoutRemedy(CheckoutsLocation location) =>
        location.IsExternal
            ? Raw.Compose($"This needs a checkouts directory short enough for the working tree itself (at most {MaxCheckoutsRoot(MaxWorkingTreeRootLength)} characters, less for a long checkout name); shorten '{Raw.Literal(Config.DeveloperConfiguration.CheckoutsDirectoryKey)}'.")
            : Raw.Compose($"This needs an AppHost path short enough for the working tree itself (about {MaxSeparateAppHostLength} characters, less for a long checkout name); beyond that, move the AppHost to a shorter path, or set '{Raw.Literal(Config.DeveloperConfiguration.CheckoutsDirectoryKey)}' (environment variable '{Raw.Literal(CheckoutsLocation.EnvironmentVariable)}') to a short directory such as '{Raw.Literal(CheckoutsLocation.SuggestedDirectory)}'.");

    internal static ServiceSourcesConfigurationException GitRootRefusal(
        Raw label, string checkoutPath, string longestRoot, int gitDirLimit, string? gitRoot, CheckoutsLocation location)
    {
        var inPlace = MaxInPlaceRoot(gitDirLimit);
        var why = string.IsNullOrEmpty(gitRoot)
            ? Raw.Literal("The local application data folder is not set, so there is nowhere for it.")
            : Raw.Compose($"'{Raw.Display(gitRoot)}' is {gitRoot.Length} characters, over the {MaxGitRootLength} a git directory under it can have.");
        return ServiceSourcesConfigurationException.For(
            $"{label}: cannot clone into '{Raw.Display(Path.GetFullPath(checkoutPath))}'. git for Windows refuses to run in a repository whose '.git' directory path passes {gitDirLimit} characters (\"'$GIT_DIR' too big\"), whatever core.longpaths or LongPathsEnabled say, so a clone under '{Raw.Display(longestRoot)}' ({longestRoot.Length} characters; at most {inPlace} fit) needs its git directory kept elsewhere. The short directory for that cannot be used here. {why} {ShortenAdvice(location, longestRoot.Length - inPlace, inPlace)}");
    }
}
