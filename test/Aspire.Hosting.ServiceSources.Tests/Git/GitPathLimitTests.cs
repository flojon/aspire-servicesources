using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Tests.Worktrees;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class GitPathLimitTests
{
    [Fact]
    public void Limits_AreDerivedFromGitsGitDirCheck()
    {
        Assert.Equal(220, GitPathLimit.MaxGitDirLength);
        Assert.Equal(215, GitPathLimit.MaxInPlaceRootLength);
        Assert.Equal(215, GitPathLimit.MaxInPlaceRoot(GitPathLimit.MaxGitDirLength));
        Assert.Equal(235, GitPathLimit.MaxWorkingTreeRootLength);
        Assert.Equal(190, GitPathLimit.MaxGitRootLength);
    }

    [Theory]
    [InlineData(1, "1 character")]
    [InlineData(2, "2 characters")]
    public void Shorter_IsGrammaticalForOne(int count, string expected) =>
        Assert.Equal(expected, GitPathLimit.Shorter(count).ToString());

    [Fact]
    public void SeparateGitDirectoryName_Is29Characters()
    {
        var name = new string('a', 16) + "-" + new string('b', 8) + ".git";

        Assert.Matches(ManagedGitDirectory.NamePattern, name);
        Assert.Equal(GitPathLimit.SeparateGitDirectoryNameLength, name.Length);
    }
}

[Trait("IO", "true")]
public class GitPathLimitStagingTests
{
    [Fact]
    public void AppHostDepths_FollowFromTheStagingSuffix()
    {
        var appHost = TempDirectories.CreateSubdirectory().FullName;
        var checkoutsRoot = Path.GetDirectoryName(LocalGitCheckout.ManagedRepoRoot(appHost, "orders"))!;
        Directory.CreateDirectory(checkoutsRoot);
        using var scratch = ScratchDirectory.Create(checkoutsRoot);

        var stagingSuffix = scratch.Path.Length - appHost.Length;

        Assert.Equal(45, stagingSuffix);
        Assert.Equal(stagingSuffix, GitPathLimit.StagingSuffixLength);
        Assert.Equal(170, GitPathLimit.MaxInPlaceAppHostLength);
        Assert.Equal(190, GitPathLimit.MaxSeparateAppHostLength);
    }
}
