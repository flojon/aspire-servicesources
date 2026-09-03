using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// Progress reporting driven against the real <c>git</c> (#131): that a clone with a sink attached
/// reports git's own phases as it runs, and that those lines stay out of the message a failure is
/// reported with.
/// </summary>
public class CloneProgressTests
{
    /// <summary>
    /// The origin addressed as a URL rather than as the plain path the other tests use. A clone from
    /// a path is the one case git reports nothing for at all — it hardlinks the object store instead
    /// of transferring a pack — so it would leave nothing to observe.
    /// </summary>
    private static string CloneUrl(TestRepository origin) => new Uri(origin.Path).AbsoluteUri;

    [Fact]
    public void Clone_WithASink_ReportsGitsOwnPhases()
    {
        var origin = TestRepository.CreateOrigin();
        var destination = TestRepository.EmptyDestination();
        var sink = new RecordingProgressSink();

        new GitCliClient(TestRepository.IsolatedEnvironment()).Clone(CloneUrl(origin), destination, sink);

        Assert.Equal("main content", TestRepository.At(destination).Read("file.txt"));

        var phases = sink.Lines
            .Select(line => GitProgressLine.TryParse(line, out var progress) ? progress.Phase : null)
            .Where(phase => phase is not null)
            .Distinct()
            .ToArray();

        // Both halves of a clone: what the server does, which git prefixes "remote: ", and what
        // arrives locally.
        Assert.Contains("Counting objects", phases);
        Assert.Contains("Receiving objects", phases);
    }

    [Fact]
    public void Clone_WithASink_ReportsTheRestOfStderrToo()
    {
        var origin = TestRepository.CreateOrigin();
        var sink = new RecordingProgressSink();

        new GitCliClient(TestRepository.IsolatedEnvironment())
            .Clone(CloneUrl(origin), TestRepository.EmptyDestination(), sink);

        // The sink is the resource's log, not just its progress bar: what git says that is not a
        // percentage is the part worth keeping.
        Assert.Contains(sink.Lines, line => line.StartsWith("Cloning into", StringComparison.Ordinal));
    }

    [Fact]
    public void ProgressLines_AreKeptOutOfTheStderrAFailureWouldBeReportedFrom()
    {
        var origin = TestRepository.CreateOrigin();
        var destination = TestRepository.EmptyDestination();

        var result = GitCommand.Run(
            ["clone", "--progress", "--", CloneUrl(origin), destination],
            TestRepository.IsolatedEnvironment(),
            new RecordingProgressSink());

        Assert.True(result.Succeeded, result.StandardError);

        // A clone interrupted mid-transfer has written a line per percentage point by then, and
        // GitCliClient reports failure with git's whole stderr — so leaving them in would bury
        // "fatal: early EOF" under a hundred superseded percentages.
        Assert.DoesNotContain("Receiving objects", result.StandardError);

        // Only the progress is dropped. Everything a developer would want to read is still there,
        // including the phase lines that carry an object count but no percentage.
        Assert.Contains("Cloning into", result.StandardError);
        Assert.Contains("Enumerating objects", result.StandardError);
    }

    [Fact]
    public void Clone_WithoutASink_LeavesStderrExactlyAsGitWroteIt()
    {
        var origin = TestRepository.CreateOrigin();

        var result = GitCommand.Run(
            ["clone", "--", CloneUrl(origin), TestRepository.EmptyDestination()],
            TestRepository.IsolatedEnvironment());

        // No sink means no --progress and no filtering: the path every other git command in this
        // package takes is untouched by any of this.
        Assert.True(result.Succeeded, result.StandardError);
        Assert.Contains("Cloning into", result.StandardError);
    }
}
