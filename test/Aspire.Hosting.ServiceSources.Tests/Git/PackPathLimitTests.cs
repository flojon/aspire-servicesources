using Aspire.Hosting.ServiceSources.Git;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class PackPathLimitTests
{
    [Fact]
    public void Limit_IsSizedForTheRepacksTemporaryPack()
    {
        Assert.Equal(85, PackPathLimit.LongestPackSuffixLength);
        Assert.Equal(174, PackPathLimit.MaxCloneRootLength);
    }

    [Fact]
    public void GitDirectoryBudget_IsSizedForTheRepacksTemporaryPack()
    {
        Assert.Equal(80, PackPathLimit.LongestPackFileInGitDir.Length);
        Assert.Equal(149, PackPathLimit.MaxGitRootLength);
        Assert.Equal(235, PackPathLimit.MaxWorkingTreeRootLength);
    }
}
