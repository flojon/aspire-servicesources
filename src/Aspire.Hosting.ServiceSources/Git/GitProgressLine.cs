using System.Text.RegularExpressions;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// One line of <c>git --progress</c> output that names a phase and how far through it git is.
/// </summary>
/// <param name="Phase">
/// git's own name for what it is doing — "Counting objects", "Receiving objects", "Resolving
/// deltas", "Updating files" — with any <c>remote: </c> prefix dropped.
/// </param>
/// <param name="Percent">How far through that phase git says it is, 0 to 100.</param>
/// <param name="Transferred">
/// Bytes received so far as git formatted them ("18.54 MiB"), or <see langword="null"/> for a phase
/// that transfers nothing. Only <c>Receiving objects</c> reports it, and only once enough has
/// arrived for git to have a figure.
/// </param>
internal readonly partial record struct GitProgressLine(string Phase, int Percent, string? Transferred)
{
    /// <summary>
    /// The phase and its own percentage, for the dashboard's State column.
    /// </summary>
    /// <remarks>
    /// Deliberately not one 0–100 bar across the phases. A clone runs five of them — counting and
    /// compressing on the remote, then receiving, resolving deltas and updating files locally — and
    /// their relative durations depend on the repository: weighting them into an aggregate would
    /// invent numbers that are wrong for any given clone, and produce a bar that stalls or jumps.
    /// Naming the phase says what git is actually doing.
    /// </remarks>
    public string StateText =>
        Transferred is null ? $"{Phase} {Percent}%" : $"{Phase} {Percent}% · {Transferred}";

    /// <summary>
    /// Parses one line of git's progress stream, or returns <see langword="false"/> for anything
    /// else git wrote to stderr — "Cloning into 'x'...", "remote: Total 62 (delta 0)", a warning, a
    /// <c>fatal:</c>. Only a line carrying a percentage parses: a phase without one ("remote:
    /// Enumerating objects: 62, done.") has nothing to report to a State column.
    /// </summary>
    /// <remarks>
    /// The phase names are git's own English, which is why <see cref="GitCommand"/> pins
    /// <c>LC_ALL=C</c>.
    /// </remarks>
    public static bool TryParse(string line, out GitProgressLine progress)
    {
        progress = default;

        var match = PhasePercentage().Match(line);
        if (!match.Success)
        {
            return false;
        }

        var percent = int.Parse(match.Groups["percent"].Value);
        if (percent > 100)
        {
            // git cannot report past 100%, so a number that does means this matched something that
            // only looks like a progress line. Reporting it as progress would put nonsense in the
            // State column.
            return false;
        }

        var rest = match.Groups["rest"].Value;
        var transferred = Throughput().Match(rest);

        progress = new GitProgressLine(
            match.Groups["phase"].Value,
            percent,
            transferred.Success ? transferred.Groups["transferred"].Value : null);

        return true;
    }

    /// <summary>
    /// The head of a progress line: an optional <c>remote: </c>, the phase name, the percentage and
    /// the object counts — "remote: Counting objects:  48% (6764/14091)".
    /// </summary>
    /// <remarks>
    /// The counts are required rather than optional because they are what makes this a progress
    /// line rather than a coincidence. <c>rest</c> carries whatever git appended, which
    /// <see cref="Throughput"/> reads separately: a suffix this doesn't expect then costs the byte
    /// count instead of the whole line.
    /// </remarks>
    [GeneratedRegex(
        @"^(?:remote:\s*)?(?<phase>[A-Za-z][A-Za-z ]*?):\s*(?<percent>\d{1,3})%\s*\(\d+/\d+\)(?<rest>.*)$")]
    private static partial Regex PhasePercentage();

    /// <summary>
    /// The bytes-transferred figure git appends to a receiving phase, ahead of the rate it computes
    /// from it: ", 18.54 MiB | 18.38 MiB/s". Anchored on that separator so the rate — same units,
    /// per second — cannot be read as the total.
    /// </summary>
    [GeneratedRegex(@"^,\s*(?<transferred>\d+(?:\.\d+)?\s*[KMGTP]?i?B)\s*\|")]
    private static partial Regex Throughput();
}
