using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Messages;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class PackPathLimitTests
{
    private static readonly Raw Label = Raw.Literal("Service 'orders'");

    private static string RootOfLength(int length)
    {
        var prefix = OperatingSystem.IsWindows() ? @"C:\" : "/";
        return prefix + new string('a', length - prefix.Length);
    }

    private static string StagingFor(string root) => RootOfLength(Math.Min(root.Length, PackPathLimit.MaxCloneRootLength));

    [Fact]
    public void RootAtTheLimit_Passes()
    {
        var root = RootOfLength(PackPathLimit.MaxCloneRootLength);

        PackPathLimit.EnsureFits(Label, root, root, isWindows: true);
    }

    [Fact]
    public void RootOneOverTheLimit_NamesTheLimitTheLengthAndTheFix()
    {
        var root = RootOfLength(PackPathLimit.MaxCloneRootLength + 1);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => PackPathLimit.EnsureFits(Label, root, StagingFor(root), isWindows: true));

        Assert.Contains("Service 'orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("259", ex.Message, StringComparison.Ordinal);
        Assert.Contains((PackPathLimit.MaxPathLength + 1).ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Contains("at least 1 characters shorter", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Move the AppHost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LongerStagingPathThanCheckout_IsTheOneMeasured()
    {
        var root = RootOfLength(50);
        var staging = RootOfLength(PackPathLimit.MaxCloneRootLength + 5);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => PackPathLimit.EnsureFits(Label, root, staging, isWindows: true));

        Assert.Contains("at least 5 characters shorter", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NotWindows_NeverRefuses()
    {
        var root = RootOfLength(PackPathLimit.MaxCloneRootLength + 100);

        PackPathLimit.EnsureFits(Label, root, root, isWindows: false);
    }
}
