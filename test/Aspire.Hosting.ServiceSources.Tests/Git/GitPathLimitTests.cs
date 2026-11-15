using Aspire.Hosting.ServiceSources.Git;
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

    [Fact]
    public void AppHostDepths_FollowFromTheStagingSuffix()
    {
        const int stagingSuffix = 46;
        Assert.Equal(169, GitPathLimit.MaxInPlaceRootLength - stagingSuffix);
        Assert.Equal(189, GitPathLimit.MaxWorkingTreeRootLength - stagingSuffix);
    }

    [Fact]
    public void SeparateGitDirectoryName_Is29Characters()
    {
        var name = new string('a', 16) + "-" + new string('b', 8) + ".git";

        Assert.Matches(ManagedGitDirectory.NamePattern, name);
        Assert.Equal(GitPathLimit.SeparateGitDirectoryNameLength, name.Length);
    }
}
