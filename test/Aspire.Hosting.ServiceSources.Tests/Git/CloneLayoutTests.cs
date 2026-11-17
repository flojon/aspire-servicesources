using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Messages;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class CloneLayoutTests
{
    private static readonly Raw Label = Raw.Literal("Service 'orders'");

    private const string UsableRoot = "gitroot";

    private static string RootOfLength(int length)
    {
        var prefix = OperatingSystem.IsWindows() ? @"C:\" : "/";
        return prefix + new string('a', length - prefix.Length);
    }

    private static CloneLayoutOptions Windows(string? gitRoot = UsableRoot) => new(gitRoot, IsWindows: true);

    [Theory]
    [InlineData(215, true)]
    [InlineData(216, false)]
    [InlineData(235, false)]
    public void Windows_DecidesByRootLength(int length, bool inPlace)
    {
        var root = RootOfLength(length);

        Assert.Equal(
            inPlace ? CloneLayoutKind.InPlace : CloneLayoutKind.Separate,
            CloneLayout.Decide(Label, root, root, Windows()));
    }

    [Fact]
    public void Windows_236_IsRefused()
    {
        var root = RootOfLength(236);

        Assert.Throws<ServiceSourcesConfigurationException>(() => CloneLayout.Decide(Label, root, root, Windows()));
    }

    [Fact]
    public void Windows_InjectedGitDirLimit_MovesTheInPlaceBoundary()
    {
        var root = RootOfLength(100);
        var options = new CloneLayoutOptions(UsableRoot, IsWindows: true, GitDirLimit: 104);

        Assert.Equal(CloneLayoutKind.Separate, CloneLayout.Decide(Label, root, root, options));
        Assert.Equal(CloneLayoutKind.InPlace, CloneLayout.Decide(Label, root, root, options with { GitDirLimit = 105 }));
    }

    [Fact]
    public void Windows_AtInPlaceLimit_IsInPlace()
    {
        var root = RootOfLength(GitPathLimit.MaxInPlaceRootLength);

        Assert.Equal(CloneLayoutKind.InPlace, CloneLayout.Decide(Label, root, root, Windows()));
    }

    [Fact]
    public void Windows_OneOverInPlaceLimit_IsSeparate()
    {
        var root = RootOfLength(GitPathLimit.MaxInPlaceRootLength + 1);

        Assert.Equal(CloneLayoutKind.Separate, CloneLayout.Decide(Label, root, root, Windows()));
    }

    [Fact]
    public void Windows_AtCeiling_IsSeparate()
    {
        var root = RootOfLength(GitPathLimit.MaxWorkingTreeRootLength);

        Assert.Equal(CloneLayoutKind.Separate, CloneLayout.Decide(Label, root, root, Windows()));
    }

    [Fact]
    public void Windows_OneOverCeiling_Throws()
    {
        var root = RootOfLength(GitPathLimit.MaxWorkingTreeRootLength + 1);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, root, root, Windows()));

        Assert.Contains("Service 'orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains((GitPathLimit.MaxWorkingTreeRootLength + 1).ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("at least 1 character shorter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Move the AppHost", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(".tmp-<pid>-pack", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_UsesTheLongerOfFinalAndStaging()
    {
        var final = RootOfLength(50);
        var staging = RootOfLength(GitPathLimit.MaxWorkingTreeRootLength + 5);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, final, staging, Windows()));

        Assert.Contains("at least 5 characters shorter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_StagingLongerThanInPlaceLimit_IsSeparateEvenWhenFinalFits()
    {
        var final = RootOfLength(50);
        var staging = RootOfLength(GitPathLimit.MaxInPlaceRootLength + 1);

        Assert.Equal(CloneLayoutKind.Separate, CloneLayout.Decide(Label, final, staging, Windows()));
    }

    [Fact]
    public void Windows_GitRootNull_OverInPlaceLimit_ThrowsWithGitDirNumbers()
    {
        var root = RootOfLength(GitPathLimit.MaxInPlaceRootLength + 1);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, root, root, Windows(gitRoot: null)));

        Assert.Contains("'$GIT_DIR' too big", ex.Message, StringComparison.Ordinal);
        Assert.Contains("220", ex.Message, StringComparison.Ordinal);
        Assert.Contains("at least 1 character shorter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("local application data folder is not set", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("%LOCALAPPDATA%", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_GitRootTooLong_NamesItsPathAndLength()
    {
        var root = RootOfLength(GitPathLimit.MaxInPlaceRootLength + 1);
        var longGitRoot = RootOfLength(GitPathLimit.MaxGitRootLength + 1);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, root, root, Windows(longGitRoot)));

        Assert.Contains(longGitRoot, ex.Message, StringComparison.Ordinal);
        Assert.Contains($"is {longGitRoot.Length} characters, over the {GitPathLimit.MaxGitRootLength}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_GitRootOver190_IsUnusable()
    {
        var root = RootOfLength(GitPathLimit.MaxInPlaceRootLength + 1);
        var longGitRoot = RootOfLength(GitPathLimit.MaxGitRootLength + 1);

        Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, root, root, Windows(longGitRoot)));

        var fittingGitRoot = RootOfLength(GitPathLimit.MaxGitRootLength);
        Assert.Equal(CloneLayoutKind.Separate, CloneLayout.Decide(Label, root, root, Windows(fittingGitRoot)));
    }

    [Fact]
    public void Windows_GitRootNull_WhenInPlaceFits_IsStillInPlace()
    {
        var root = RootOfLength(60);

        Assert.Equal(CloneLayoutKind.InPlace, CloneLayout.Decide(Label, root, root, Windows(gitRoot: null)));
    }

    [Fact]
    public void NotWindows_IsInPlaceWhateverTheLength()
    {
        var root = RootOfLength(GitPathLimit.MaxWorkingTreeRootLength + 100);

        Assert.Equal(
            CloneLayoutKind.InPlace,
            CloneLayout.Decide(Label, root, root, new CloneLayoutOptions(UsableRoot, IsWindows: false)));
    }

    [Fact]
    public void ForceSeparate_BeyondCeiling_StillThrows()
    {
        var root = RootOfLength(GitPathLimit.MaxWorkingTreeRootLength + 1);

        Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, root, root, new CloneLayoutOptions(UsableRoot, IsWindows: true, ForceSeparate: true)));
    }

    [Fact]
    public void ForceSeparate_OnNonWindows_IsSeparate()
    {
        var root = RootOfLength(60);

        Assert.Equal(
            CloneLayoutKind.Separate,
            CloneLayout.Decide(Label, root, root, new CloneLayoutOptions(UsableRoot, IsWindows: false, ForceSeparate: true)));
    }
}
