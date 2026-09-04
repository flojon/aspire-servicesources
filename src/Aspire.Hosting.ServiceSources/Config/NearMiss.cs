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
    /// <c>namespace</c> on two, where a longer word leaves more room to go wrong and one edit is
    /// stingy.
    /// <para>
    /// A transposition is what makes one edit enough for the short names rather than stingy in its
    /// turn: <see cref="EditDistance"/> charges a swapped pair one edit, not two, so <c>paht</c>,
    /// <c>prot</c> and <c>tga</c> are all inside a short name's tolerance. Charging two for it —
    /// which plain Levenshtein does — left the commonest typo of the commonest fields unanswered
    /// while <c>pth</c>, a dropped letter, was answered, and the difference is invisible to whoever
    /// hit it.
    /// </para>
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
    /// qualifying distance is returned, so a caller can name them all — and a caller that wants one
    /// answer takes the first.
    /// <para>
    /// The order is <paramref name="spelling"/>'s, which is a total order only while the spellings
    /// differ. Two candidates that <em>share</em> a spelling — the same field name declared by two
    /// different blocks — are left in the order they were supplied, since nothing here can tell
    /// them apart. A caller taking the first of those has to order by whatever separates them
    /// first, or it is relying on its own enumeration order; see
    /// <see cref="ServiceDeveloperConfigShape.NearMissFieldOf"/>, which orders by block as well for
    /// exactly that reason.
    /// </para>
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
            // Filtered before the minimum is taken, since the tolerance differs per candidate:
            // taking the minimum first would let a candidate outside its own tolerance win and then
            // be discarded, losing a suggestion a longer candidate had earned.
            //
            // Under the two tiers MaxEdits has today the two orders cannot actually disagree — the
            // closest candidate is at distance 0 or 1, which every tolerance admits, and for a
            // farther one to be excluded while a farther one still qualifies needs a tier above
            // two. So this is the order that stays correct if a tier is added, not a difference
            // anything can observe now, and no test pins it because none can.
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
    /// The distance between <paramref name="from"/> and <paramref name="to"/>: how many
    /// single-character inserts, deletes, substitutions and swaps of an adjacent pair separate them.
    /// </summary>
    /// <remarks>
    /// Levenshtein plus the transposition of the Damerau variant, which charges a swapped pair one
    /// edit rather than the two a substitution each way would cost. That is the whole reason the
    /// transposition is here: it is the commonest typo there is, and the fields this matches against
    /// are short enough that <see cref="MaxEdits"/> gives them a single edit — so at two, every
    /// swap in the vocabulary was outside tolerance and got no suggestion at all, while a dropped
    /// letter in the same word got one.
    /// <para>
    /// Three rows rather than the full matrix: a transposition reads the row two above, and nothing
    /// reads further back. The restricted form — no substring is edited more than once, so
    /// <c>ca</c> to <c>abc</c> is three rather than two — because the answer only has to order
    /// candidates by resemblance, and the unrestricted algorithm costs an alphabet-sized table to
    /// change an answer no vocabulary here can produce.
    /// </para>
    /// </remarks>
    public static int EditDistance(string from, string to)
    {
        var beforePrevious = new int[to.Length + 1];
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
                var best = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);

                // The swapped pair, charged as one edit. Guarded on i and j rather than reaching for
                // a row that isn't there: on the first row there is nothing two above, and
                // beforePrevious still holds the zeroes it was allocated with.
                if (i > 1 && j > 1 && from[i - 1] == to[j - 2] && from[i - 2] == to[j - 1])
                {
                    best = Math.Min(best, beforePrevious[j - 2] + 1);
                }

                current[j] = best;
            }

            // Rotated so the row just finished becomes the previous one, the previous becomes the
            // row two above, and the oldest buffer is reused for the next row.
            (beforePrevious, previous, current) = (previous, current, beforePrevious);
        }

        return previous[to.Length];
    }
}
