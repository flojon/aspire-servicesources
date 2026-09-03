using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

/// <summary>
/// Collects what a running <c>git</c> reports, standing in for the resource a real checkout writes
/// its progress to.
/// </summary>
/// <remarks>
/// Locked because it is written from the thread draining git's stderr and read from the test's own.
/// </remarks>
internal sealed class RecordingProgressSink : IGitProgressSink
{
    private readonly List<string> _lines = [];

    public void Report(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
        }
    }

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return [.. _lines];
            }
        }
    }

    /// <summary>
    /// How many lines start with <paramref name="prefix"/> — how the number of attempts behind one
    /// stream is counted, since each <c>git clone</c> invocation announces itself once.
    /// </summary>
    public int CountStartingWith(string prefix) =>
        Lines.Count(line => line.StartsWith(prefix, StringComparison.Ordinal));
}
