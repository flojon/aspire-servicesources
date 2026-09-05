using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// The helper's own contract, rather than one caller's use of it. Everything here is reached
/// through <c>project</c>, <c>prepare.command</c> and the <c>java</c> paths as well — what this
/// file adds is the part no caller can reach any more, now that each rejects an empty value of its
/// own before asking.
/// </summary>
public class CheckoutRelativePathTests
{
    [Theory]
    [InlineData("")]
    [InlineData("src/Orders.csproj")]
    [InlineData(@"src\Orders.csproj")]
    [InlineData("../sibling/Orders.csproj")]
    public void IsAbsolute_RelativeOrEmpty_IsFalse(string path) =>
        Assert.False(CheckoutRelativePath.IsAbsolute(path));

    [Theory]
    [InlineData("/srv/Orders.csproj")]
    [InlineData(@"C:\repos\Orders.csproj")]
    [InlineData(@"\\server\share\Orders.csproj")]
    [InlineData("C:relative.csproj")]
    public void IsAbsolute_RootedOnAnyPlatform_IsTrue(string path) =>
        Assert.True(CheckoutRelativePath.IsAbsolute(path));
}
