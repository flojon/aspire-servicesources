namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// "Did you mean …?" over a fixed vocabulary: which of a known set of names a developer's misspelled
/// one is close enough to be a typo of.
/// </summary>
/// <remarks>
/// Used only to improve a failure that is already being thrown, never to accept a key. A false
/// positive costs a sentence naming the wrong field; a false negative costs the sentence. Neither
/// can make a working file fail or a broken one pass, which is what makes the tolerances below
/// generous rather than exact.
/// </remarks>
internal static class NearMiss
{
    /// <summary>
    /// The longest name for which a single edit is all the tolerance there is.
    /// </summary>
    /// <remarks>
    /// The vocabulary this matches against is mostly short — <c>ref</c>, <c>tag</c>, <c>url</c>,
    /// <c>port</c> — and two edits from a three-letter word reaches a large part of the alphabet, so
    /// a flat tolerance would confidently misname fields. Four is the boundary because it keeps
    /// every one of those on one edit while leaving <c>scheme</c>, <c>context</c> and
    /// <c>namespace</c> — where a doubled or transposed letter is the usual mistake and one edit is
    /// stingy — on two.
    /// </remarks>
    private const int ShortName = 4;

    /// <summary>
    /// How far from <paramref name="candidate"/> a name can be spelled and still be taken for it.
    /// </summary>
    /// <remarks>
    /// Scaled by the candidate's length rather than the misspelling's: the candidate is the fixed
    /// vocabulary, so it is what decides how much of the space two edits would swallow.
    /// </remarks>
    public static int MaxEdits(string candidate) => candidate.Length <= ShortName ? 1 : 2;

    /// <summary>
    /// The candidates <paramref name="written"/> is closest to, or empty when it resembles none of
    /// them.
    /// </summary>
    /// <remarks>
    /// A list rather than the single best answer, because near misses can tie where exact matches
    /// cannot: a typo can sit one edit from two different names. Every candidate at the smallest
    /// qualifying distance is returned, in the order <paramref name="spelling"/> puts them, so a
    /// caller that wants one answer takes the first and gets the same one on every run — and a
    /// caller that would rather name them all can.
    /// <para>
    /// Folded to lower case before measuring, because configuration keys are case-insensitive: a
    /// key written <c>Path</c> is not a misspelling of <c>path</c>, it is <c>path</c>, and would
    /// have matched exactly before reaching here.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<T> Nearest<T>(
        string written, IEnumerable<T> candidates, Func<T, string> spelling)
    {
        var lowered = written.ToLowerInvariant();

        var scored = candidates
            .Select(candidate => (Candidate: candidate, Spelling: spelling(candidate)))
            .Select(entry => (
                entry.Candidate,
                entry.Spelling,
                Distance: EditDistance(lowered, entry.Spelling.ToLowerInvariant())))
            // Filtered before the minimum is taken, since the tolerance differs per candidate: a
            // long name one edit away qualifies where a short name the same distance from a
            // different key does not, and comparing raw distances would let the second win.
            .Where(entry => entry.Distance <= MaxEdits(entry.Spelling))
            .ToArray();

        if (scored.Length == 0)
        {
            return [];
        }

        var closest = scored.Min(entry => entry.Distance);

        return scored
            .Where(entry => entry.Distance == closest)
            .OrderBy(entry => entry.Spelling, StringComparer.Ordinal)
            .Select(entry => entry.Candidate)
            .ToArray();
    }

    /// <summary>
    /// The Levenshtein distance between <paramref name="from"/> and <paramref name="to"/>: how many
    /// single-character inserts, deletes and substitutions separate them.
    /// </summary>
    /// <remarks>
    /// Two rows rather than the full matrix, since only the previous row is ever read. A transposed
    /// pair costs two edits here where the Damerau variant charges one, which is why
    /// <see cref="MaxEdits"/> allows two for anything but a short name.
    /// </remarks>
    public static int EditDistance(string from, string to)
    {
        var previous = new int[to.Length + 1];
        var current = new int[to.Length + 1];

        for (var j = 0; j <= to.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= from.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= to.Length; j++)
            {
                var substitution = previous[j - 1] + (from[i - 1] == to[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[to.Length];
    }
}
