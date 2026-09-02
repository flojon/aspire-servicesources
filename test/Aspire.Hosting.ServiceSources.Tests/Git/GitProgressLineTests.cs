using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// Reading <c>git clone --progress</c>'s own output (#131). Every input here is a line a real clone
/// produced under <c>LC_ALL=C</c>, padding included, so the parser is checked against git's actual
/// wording rather than against a description of it.
/// </summary>
public class GitProgressLineTests
{
    private static GitProgressLine Parse(string line)
    {
        Assert.True(GitProgressLine.TryParse(line, out var progress), $"'{line}' did not parse as progress.");
        return progress;
    }

    [Fact]
    public void ReceivingObjects_CarriesPhasePercentageAndBytes()
    {
        var progress = Parse("Receiving objects:  48% (6864/14091), 18.54 MiB | 18.38 MiB/s");

        Assert.Equal("Receiving objects", progress.Phase);
        Assert.Equal(48, progress.Percent);
        Assert.Equal("18.54 MiB", progress.Transferred);
    }

    [Fact]
    public void ReceivingObjects_Done_IsStillProgress() =>
        Assert.Equal(100, Parse("Receiving objects: 100% (14091/14091), 95.42 MiB | 20.79 MiB/s, done.").Percent);

    [Fact]
    public void RemotePhase_LosesThePrefixItReportsUnder()
    {
        // Counting and compressing happen on the server, which git prefixes "remote: ". The
        // distinction is not something a State column has room to explain.
        var progress = Parse("remote: Counting objects:  48% (6764/14091)        ");

        Assert.Equal("Counting objects", progress.Phase);
        Assert.Equal(48, progress.Percent);
        Assert.Null(progress.Transferred);
    }

    [Fact]
    public void ResolvingDeltas_ReportsNoBytes() => Assert.Null(Parse("Resolving deltas:  97% (9680/9979)").Transferred);

    [Fact]
    public void UpdatingFiles_IsProgress()
    {
        var progress = Parse("Updating files:  63% (100/158)");

        Assert.Equal("Updating files", progress.Phase);
        Assert.Equal(63, progress.Percent);
    }

    [Theory]
    // The first thing a clone writes, and the line the developer most wants left in a failure
    // message.
    [InlineData("Cloning into '/tmp/clone'...")]
    [InlineData("fatal: repository 'https://example.com/x.git' not found")]
    [InlineData("warning: redirecting to https://example.com/x.git/")]
    // A phase with a count but no percentage. git cannot say how far through it is, so neither can
    // the State column — and leaving it unparsed keeps its object count in the failure message.
    [InlineData("remote: Enumerating objects: 14091, done.")]
    [InlineData("remote: Total 62 (delta 0), reused 0 (delta 0), pack-reused 0 (from 0)")]
    [InlineData("")]
    public void NonProgress_IsNotMistakenForProgress(string line) =>
        Assert.False(GitProgressLine.TryParse(line, out _));

    [Fact]
    public void ImpossiblePercentage_IsRejectedRatherThanShown() =>
        // Not something git produces. Whatever this matched, it was not a progress line, and a State
        // column reading "Receiving objects 999%" is worse than no progress at all.
        Assert.False(GitProgressLine.TryParse("Receiving objects: 999% (1/2)", out _));

    [Fact]
    public void StateText_NamesThePhaseAndItsOwnPercentage() =>
        Assert.Equal(
            "Receiving objects 48% · 18.54 MiB",
            Parse("Receiving objects:  48% (6864/14091), 18.54 MiB | 18.38 MiB/s").StateText);

    [Fact]
    public void StateText_OmitsBytesForAPhaseThatTransfersNone() =>
        Assert.Equal("Resolving deltas 97%", Parse("Resolving deltas:  97% (9680/9979)").StateText);
}
