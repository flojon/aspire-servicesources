namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// "Did you mean …?" over a fixed vocabulary, asked in either direction: which of a known set of
/// names a developer's misspelled one is close enough to be a typo of — <see cref="Nearest{T}"/> —
/// and which of the words a developer wrote is close enough to be a typo of one known name —
/// <see cref="MisspellingOf"/>. The vocabulary is the fixed side in both, and it is the side the
/// tolerance is scaled by — which side that is is what a caller picks between. The two differ in
/// what they answer with as well, and each says so where it is defined.
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
    /// <para>
    /// The vocabulary is no longer only this package's own names. It still is for every caller that
    /// matches against a fixed list — the entry fields above, and the file's root keys, where
    /// <see cref="MisspellingOf"/> is asked about <c>services</c> and <c>backingServices</c> and the
    /// developer's own keys are the candidates. The one that widened it is the service-name caller,
    /// where the vocabulary is a name from <c>servicesources.yaml</c> and so is as long as whoever
    /// wrote the catalog made it. The boundary holds there by being the stingier of the two tiers
    /// where it applies at all: a four-letter service name gets the single edit <c>path</c> gets,
    /// for the same reason, and a longer one gets the two <c>namespace</c> gets.
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
    /// <see cref="DeveloperConfigShape.NearMissFieldsOf"/>, which orders by block as well for
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
            // Under the two tiers MaxEdits has today the two orders cannot actually disagree: they
            // differ only when every candidate at the smallest distance fails its own tolerance
            // while a strictly farther one passes; failing a tolerance takes at least two edits,
            // so the farther one would have to be at three or more — further than any tolerance
            // admits. A tier above two would make it observable. So this is the order that stays
            // correct if a tier is added, not a difference anything can observe now, and no test
            // pins it because none can.
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
    /// The one of <paramref name="written"/> that reads as a misspelling of
    /// <paramref name="known"/>, or <see langword="null"/> when none of them does.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="Nearest{T}"/>, and what differs is which side is the fixed
    /// vocabulary. There a caller holds the known list and asks which of it one word the developer
    /// wrote resembles, so each candidate's own length sets the tolerance. Here it is the other way
    /// round — the candidates are the developer's words, the root keys of their file or the service
    /// names they configured, and <paramref name="known"/> is the single word from the vocabulary —
    /// so <paramref name="known"/> is what <see cref="MaxEdits"/> is asked about. Scaling by the
    /// written word instead lets a long typo buy itself room the word it is supposed to be does not
    /// have: with <c>cart</c> looked for, <c>carted</c> is two edits away and would qualify on its
    /// own six letters, while nothing suggests it is a misspelling rather than a second name.
    /// <para>
    /// A candidate that folds to <paramref name="known"/> is not a misspelling of it but the word
    /// itself, so it is dropped rather than offered as a correction of itself. Not because the
    /// caller must already have matched it — the root-key caller reaches here with the key it is
    /// looking for present but configuring nothing, which is precisely why it is still searching —
    /// but because configuration keys are case-insensitive, so the two spellings are one name and
    /// answering "did you mean X?" with X is not an answer.
    /// </para>
    /// <para>
    /// Closest first, then ordinal, so a caller with two candidates the same distance away names
    /// the same one on every run rather than whichever its provider happened to enumerate first.
    /// One answer rather than the list <see cref="Nearest{T}"/> returns, because the messages this
    /// feeds ask a question — <em>did you mean …?</em> — and a question with two answers in it is
    /// one the reader has to resolve themselves.
    /// </para>
    /// </remarks>
    public static string? MisspellingOf(string known, IEnumerable<string> written)
    {
        var folded = known.ToLowerInvariant();
        var tolerance = MaxEdits(known);

        return written
            .Select(name => (Written: name, Folded: name.ToLowerInvariant()))
            // Judged on the folded spellings, the same ones the distance below is measured over,
            // rather than by comparing the written ones case-insensitively. The two are the same
            // question asked twice, and letting them disagree — as they do for the handful of
            // characters whose invariant lower case and whose case-insensitive comparison part
            // company — would let a name through at distance zero to be offered as a correction of
            // itself.
            .Where(name => string.Equals(name.Folded, folded, StringComparison.Ordinal) is false)
            .Select(name => (name.Written, Distance: EditDistance(name.Folded, folded)))
            .Where(name => name.Distance <= tolerance)
            .OrderBy(name => name.Distance)
            .ThenBy(name => name.Written, StringComparer.Ordinal)
            .Select(name => name.Written)
            .FirstOrDefault();
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
