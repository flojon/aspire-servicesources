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

    [Fact]
    public void LongestPackFileInGitDir_Is80Characters()
    {
        Assert.Equal(80, PackPathLimit.LongestPackFileInGitDir.Length);
        Assert.Equal(235, PackPathLimit.MaxWorkingTreeRootLength);
        Assert.Equal(149, PackPathLimit.MaxGitRootLength);
    }

    [Fact]
    public void Windows_AtInPlaceLimit_IsInPlace()
    {
        var root = RootOfLength(PackPathLimit.MaxCloneRootLength);

        Assert.Equal(CloneLayoutKind.InPlace, CloneLayout.Decide(Label, root, root, Windows()));
    }

    [Fact]
    public void Windows_OneOverInPlaceLimit_IsSeparate()
    {
        var root = RootOfLength(PackPathLimit.MaxCloneRootLength + 1);

        Assert.Equal(CloneLayoutKind.Separate, CloneLayout.Decide(Label, root, root, Windows()));
    }

    [Fact]
    public void Windows_AtCeiling_IsSeparate()
    {
        var root = RootOfLength(PackPathLimit.MaxWorkingTreeRootLength);

        Assert.Equal(CloneLayoutKind.Separate, CloneLayout.Decide(Label, root, root, Windows()));
    }

    [Fact]
    public void Windows_OneOverCeiling_Throws()
    {
        var root = RootOfLength(PackPathLimit.MaxWorkingTreeRootLength + 1);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, root, root, Windows()));

        Assert.Contains("Service 'orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains((PackPathLimit.MaxWorkingTreeRootLength + 1).ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("at least 1 characters shorter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Move the AppHost", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(".tmp-<pid>-pack", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_UsesTheLongerOfFinalAndStaging()
    {
        var final = RootOfLength(50);
        var staging = RootOfLength(PackPathLimit.MaxWorkingTreeRootLength + 5);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, final, staging, Windows()));

        Assert.Contains("at least 5 characters shorter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_StagingLongerThanInPlaceLimit_IsSeparateEvenWhenFinalFits()
    {
        var final = RootOfLength(50);
        var staging = RootOfLength(PackPathLimit.MaxCloneRootLength + 1);

        Assert.Equal(CloneLayoutKind.Separate, CloneLayout.Decide(Label, final, staging, Windows()));
    }

    [Fact]
    public void Windows_GitRootNull_OverInPlaceLimit_ThrowsWithPackPathNumbers()
    {
        var root = RootOfLength(PackPathLimit.MaxCloneRootLength + 1);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, root, root, Windows(gitRoot: null)));

        Assert.Contains("85", ex.Message, StringComparison.Ordinal);
        Assert.Contains("259", ex.Message, StringComparison.Ordinal);
        Assert.Contains("at least 1 characters shorter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_GitRootOver149_IsUnusable()
    {
        var root = RootOfLength(PackPathLimit.MaxCloneRootLength + 1);
        var longGitRoot = RootOfLength(PackPathLimit.MaxGitRootLength + 1);

        Assert.Throws<ServiceSourcesConfigurationException>(
            () => CloneLayout.Decide(Label, root, root, Windows(longGitRoot)));

        var fittingGitRoot = RootOfLength(PackPathLimit.MaxGitRootLength);
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
        var root = RootOfLength(PackPathLimit.MaxWorkingTreeRootLength + 100);

        Assert.Equal(
            CloneLayoutKind.InPlace,
            CloneLayout.Decide(Label, root, root, new CloneLayoutOptions(UsableRoot, IsWindows: false)));
    }

    [Fact]
    public void ForceSeparate_BeyondCeiling_StillThrows()
    {
        var root = RootOfLength(PackPathLimit.MaxWorkingTreeRootLength + 1);

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
