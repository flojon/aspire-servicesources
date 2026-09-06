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

    [Theory]
    [InlineData("src/Orders.csproj")]
    [InlineData(@"src\Orders.csproj")]
    [InlineData("./Orders.csproj")]
    [InlineData("src/../Orders.csproj")]
    [InlineData("src/./nested/../Orders.csproj")]
    [InlineData("Orders.csproj")]
    [InlineData("orders./../Orders.csproj")]
    public void EscapesRoot_StaysInsideTheCheckout_IsFalse(string relativePath) =>
        Assert.False(CheckoutRelativePath.EscapesRoot(relativePath));

    [Theory]
    [InlineData("../Orders.csproj")]
    [InlineData(@"..\sibling\Orders.csproj")]
    [InlineData("src/../../Orders.csproj")]
    public void EscapesRoot_ClimbsAboveTheCheckout_IsTrue(string relativePath) =>
        Assert.True(CheckoutRelativePath.EscapesRoot(relativePath));

    /// <summary>
    /// A segment made only of dots and spaces names no directory on Windows, which strips trailing
    /// dots and spaces from a path component. Counting it as one — which an exact test against
    /// <c>"."</c> and <c>".."</c> does, since it matches neither — lets it pay for a later
    /// <c>".."</c> that then climbs out of the checkout.
    /// </summary>
    [Theory]
    [InlineData("...")]
    [InlineData("  .. ")]
    [InlineData(".. ")]
    [InlineData(". ")]
    [InlineData(" ..")]
    [InlineData("  ")]
    [InlineData(".../Orders.csproj")]
    [InlineData(@"...\Orders.csproj")]
    [InlineData("src/.../Orders.csproj")]
    [InlineData(".../../Orders.csproj")]
    public void EscapesRoot_SegmentOfOnlyDotsAndSpaces_IsTrue(string relativePath) =>
        Assert.True(CheckoutRelativePath.EscapesRoot(relativePath));

    /// <summary>
    /// The trailing dots and spaces only decide the verdict when nothing else is in the segment.
    /// A name with text left after them is a directory under the checkout either way — the same
    /// line <c>LocalGitCheckout.IsContainedCheckoutDirectoryName</c> draws for a service name,
    /// where <c>"orders."</c> is contained too.
    /// </summary>
    [Theory]
    [InlineData("orders./Orders.csproj")]
    [InlineData("orders /Orders.csproj")]
    [InlineData("...orders/Orders.csproj")]
    public void EscapesRoot_SegmentWithTextLeftAfterTrimming_IsFalse(string relativePath) =>
        Assert.False(CheckoutRelativePath.EscapesRoot(relativePath));

    /// <summary>
    /// The segment is returned rather than a bool so a caller can quote it back: the spelling this
    /// matters most for is <c>'.. '</c>, whose trailing space is invisible in a terminal.
    /// </summary>
    /// <summary>
    /// A real climb and a dots-and-spaces segment can both be present; the one a developer reading
    /// left to right hits first is the one reported, because <see cref="EscapesRoot"/> and this
    /// share a single scan (#241 round 2) rather than each finding a different segment.
    /// </summary>
    [Theory]
    [InlineData("../.../Orders.csproj")]
    [InlineData("a/../../.../Orders.csproj")]
    public void UnusableSegment_ClimbHappensBeforeADotsAndSpacesSegment_ReportsNeither(string relativePath)
    {
        Assert.True(CheckoutRelativePath.EscapesRoot(relativePath));
        Assert.Null(CheckoutRelativePath.UnusableSegment(relativePath));
    }

    [Theory]
    [InlineData(".../Orders.csproj", "...")]
    [InlineData("src/.. /Orders.csproj", ".. ")]
    [InlineData("src/  /Orders.csproj", "  ")]
    [InlineData("a/.../b/....", "...")]
    public void UnusableSegment_NamesTheOffendingSegment(string relativePath, string expected) =>
        Assert.Equal(expected, CheckoutRelativePath.UnusableSegment(relativePath));

    /// <summary>
    /// '.' and '..' are made only of dots, but they are navigation rather than a segment nobody can
    /// have meant — the one place this rule and the service-name rule are allowed to differ.
    /// </summary>
    [Theory]
    [InlineData("src/../Orders.csproj")]
    [InlineData("./Orders.csproj")]
    [InlineData("orders./Orders.csproj")]
    [InlineData("Orders.csproj")]
    public void UnusableSegment_NothingUnusable_IsNull(string relativePath) =>
        Assert.Null(CheckoutRelativePath.UnusableSegment(relativePath));

    [Theory]
    [InlineData("...", true)]
    [InlineData(".. ", true)]
    [InlineData("  ", true)]
    [InlineData(".", true)]
    [InlineData("..", true)]
    [InlineData("orders.", false)]
    [InlineData("orders", false)]
    [InlineData("...orders", false)]
    public void IsOnlyDotsAndSpaces_MatchesWhatWindowsWouldBeLeftWith(string segment, bool expected) =>
        Assert.Equal(expected, CheckoutRelativePath.IsOnlyDotsAndSpaces(segment));

    /// <summary>
    /// The primitive the service-name rule (#224) and the path rule share, asked of both: the two
    /// rules stay separate, but a spelling one calls dots-and-spaces the other must not call a
    /// directory. '.' and '..' are the sanctioned difference — refused as a name, navigation in a
    /// path — so they are asked of the name rule only.
    /// </summary>
    [Theory]
    [InlineData("...")]
    [InlineData("....")]
    [InlineData(".. ")]
    [InlineData(". ")]
    [InlineData(" ..")]
    [InlineData("  .. ")]
    [InlineData("  ")]
    public void TheNameRuleAndThePathRuleAgreeOnDotsAndSpaces(string spelling)
    {
        Assert.False(global::Aspire.Hosting.ServiceSources.Git.LocalGitCheckout.IsContainedCheckoutDirectoryName(spelling));
        Assert.Equal(spelling, CheckoutRelativePath.UnusableSegment(spelling));
    }

    [Theory]
    [InlineData("orders.")]
    [InlineData("orders ")]
    [InlineData("...orders")]
    public void TheNameRuleAndThePathRuleAgreeOnASegmentWithTextLeft(string spelling)
    {
        Assert.True(global::Aspire.Hosting.ServiceSources.Git.LocalGitCheckout.IsContainedCheckoutDirectoryName(spelling));
        Assert.Null(CheckoutRelativePath.UnusableSegment(spelling));
    }
}
