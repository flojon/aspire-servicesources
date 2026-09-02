using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// Cutting git's progress stream into lines while it is still arriving (#131) — the part that makes
/// live progress possible at all, since git separates its progress with <c>\r</c> and a
/// <c>ReadLine()</c> loop would deliver a whole phase at once.
/// </summary>
public class ProgressLineSplitterTests
{
    [Fact]
    public void CarriageReturns_SeparateLines()
    {
        var splitter = new ProgressLineSplitter();

        var lines = splitter.Append("Receiving objects:   1% (1/62)\rReceiving objects:   3% (2/62)\r");

        Assert.Equal(["Receiving objects:   1% (1/62)", "Receiving objects:   3% (2/62)"], lines);
    }

    [Fact]
    public void LineSplitAcrossChunks_IsReportedOnceAndWhole()
    {
        var splitter = new ProgressLineSplitter();

        // Chunks are whatever a read off the pipe happened to return, so a delimiter can land
        // anywhere — including nowhere in a chunk at all.
        Assert.Empty(splitter.Append("Receiving obj"));
        Assert.Empty(splitter.Append("ects:  48% (30/6"));

        Assert.Equal(["Receiving objects:  48% (30/62)"], splitter.Append("2)\r"));
    }

    [Fact]
    public void CarriageReturnNewline_DoesNotProduceABlankLine()
    {
        var splitter = new ProgressLineSplitter();

        Assert.Equal(["Cloning into 'dest'..."], splitter.Append("Cloning into 'dest'...\r\n"));
    }

    [Fact]
    public void TrailingPadding_IsRemoved()
    {
        var splitter = new ProgressLineSplitter();

        // git pads a progress line with spaces so it covers the longer one it is overwriting. In a
        // terminal that is invisible; in a resource log it is not.
        Assert.Equal(
            ["remote: Counting objects: 100% (62/62), done."],
            splitter.Append("remote: Counting objects: 100% (62/62), done.        \n"));
    }

    [Fact]
    public void Flush_ReportsTheLastLineWhenTheStreamEndedWithoutADelimiter()
    {
        var splitter = new ProgressLineSplitter();

        // How every clone ends: the "done." line is terminated by the process exiting.
        Assert.Empty(splitter.Append("Receiving objects: 100% (62/62), 2.36 MiB | 23.19 MiB/s, done."));

        Assert.Equal("Receiving objects: 100% (62/62), 2.36 MiB | 23.19 MiB/s, done.", splitter.Flush());
    }

    [Fact]
    public void Flush_OnAStreamThatEndedCleanly_ReportsNothing()
    {
        var splitter = new ProgressLineSplitter();

        splitter.Append("Cloning into 'dest'...\n");

        Assert.Null(splitter.Flush());
    }
}
