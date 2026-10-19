using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Messages;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class PackPathLimitTests
{
    private const int PackSuffixLength = 85;

    private static readonly int StagingNameLength = ".incoming-00000000".Length;

    private static readonly Raw Label = Raw.Literal("Service 'orders'");

    /// <summary>A checkouts root whose full path is exactly <paramref name="length"/> characters.</summary>
    private static string RootOfLength(int length)
    {
        var start = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var padding = length - start.Length - 1;
        return Path.Combine(start, new string('a', padding));
    }

    [Fact]
    public void LongestPackPath_ShortCheckoutName_IsTheStagingDirectoryPlusThePackFile()
    {
        var root = RootOfLength(100);

        var longest = PackPathLimit.LongestPackPath(root, Path.Combine(root, "orders"));

        Assert.Equal(100 + StagingNameLength + 1 + PackSuffixLength, longest.Length);
        Assert.EndsWith(".pack", longest, StringComparison.Ordinal);
        Assert.Contains(@"\pack\.tmp-", longest, StringComparison.Ordinal);
    }

    [Fact]
    public void LongestPackPath_CheckoutNameLongerThanTheStagingName_IsTheCheckoutPlusThePackFile()
    {
        var root = RootOfLength(100);
        var repoRoot = Path.Combine(root, new string('n', 40));

        Assert.Equal(repoRoot.Length + PackSuffixLength, PackPathLimit.LongestPackPath(root, repoRoot).Length);
    }

    [Fact]
    public void EnsureFits_AtTheLimit_DoesNotThrow()
    {
        var root = RootOfLength(PackPathLimit.MaxLength - PackSuffixLength - StagingNameLength - 1);

        PackPathLimit.EnsureFits(Label, root, Path.Combine(root, "orders"), isWindows: true);
    }

    [Fact]
    public void EnsureFits_OneOverTheLimit_ThrowsNamingTheLimitTheLengthAndTheFix()
    {
        var root = RootOfLength(PackPathLimit.MaxLength - PackSuffixLength - StagingNameLength);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => PackPathLimit.EnsureFits(Label, root, Path.Combine(root, "orders"), isWindows: true));

        Assert.Contains("Service 'orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("paths of 260 characters", ex.Message, StringComparison.Ordinal);
        Assert.Contains("limits them to 259", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Move the repository or git worktree that holds the AppHost (the directory that holds '.servicesources')", ex.Message, StringComparison.Ordinal);
        Assert.Contains("at least 1 characters shorter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureFits_OffWindows_NeverThrows()
    {
        var root = RootOfLength(400);

        PackPathLimit.EnsureFits(Label, root, Path.Combine(root, "orders"), isWindows: false);
    }
}
