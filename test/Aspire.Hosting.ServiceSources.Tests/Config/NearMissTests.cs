using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

/// <summary>
/// The "did you mean …?" lookup behind the config messages: how far a misspelling can be from a
/// known name, and what happens when it is equally close to two of them.
/// </summary>
public class NearMissTests
{
    [Theory]
    [InlineData("ref", 1)]
    [InlineData("path", 1)]
    [InlineData("scheme", 2)]
    [InlineData("services", 2)]
    public void MaxEdits_ScalesWithTheCandidatesLength(string candidate, int expected) =>
        Assert.Equal(expected, NearMiss.MaxEdits(candidate));

    /// <remarks>
    /// The boundary is worth pinning rather than inferring from the two sides of it: it is the whole
    /// of the rule, and moving it by one letter changes which fields get guessed at.
    /// </remarks>
    [Fact]
    public void MaxEdits_FourLettersIsShortAndFiveIsNot()
    {
        Assert.Equal(1, NearMiss.MaxEdits("abcd"));
        Assert.Equal(2, NearMiss.MaxEdits("abcde"));
    }

    [Fact]
    public void Nearest_ExactSpelling_IsItsOwnNearestMatch() =>
        Assert.Equal(["path"], NearMiss.Nearest("path", ["path", "ref"], name => name));

    [Fact]
    public void Nearest_OneEditFromAShortName_Matches() =>
        Assert.Equal(["path"], NearMiss.Nearest("pth", ["path", "ref", "port"], name => name));

    [Fact]
    public void Nearest_TwoEditsFromAShortName_MatchesNothing() =>
        Assert.Empty(NearMiss.Nearest("rap", ["ref", "tag", "path"], name => name));

    [Fact]
    public void Nearest_TwoEditsFromALongName_Matches() =>
        Assert.Equal(["namespace"], NearMiss.Nearest("namspce", ["namespace", "scheme"], name => name));

    /// <summary>
    /// The tolerance applied is each candidate's own, so a long name two edits away is suggested
    /// while a short name the same distance from a different key is not.
    /// </summary>
    /// <remarks>
    /// What this does <em>not</em> pin is the order of the filter and the minimum inside
    /// <see cref="NearMiss.Nearest"/>: under the two tiers <see cref="NearMiss.MaxEdits"/> has, the
    /// two orders cannot disagree. They differ only when every candidate at the smallest distance
    /// fails its own tolerance while a strictly farther one passes; failing a tolerance takes at
    /// least two edits, so the farther one would have to be at three or more, which no tolerance
    /// admits. It becomes observable only if a tier above two is added, and there is no way to
    /// write a test for it until then.
    /// </remarks>
    [Fact]
    public void Nearest_AppliesEachCandidatesOwnTolerance()
    {
        // Two edits: inside `namespace`'s tolerance, outside a short name's.
        Assert.Equal(["namespace"], NearMiss.Nearest("namspce", ["namespace", "ref"], name => name));
        Assert.Empty(NearMiss.Nearest("rap", ["ref", "namespace"], name => name));
    }

    /// <summary>
    /// A tie returns every candidate, in ordinal order, so a caller that names one names the same
    /// one on every run.
    /// </summary>
    /// <remarks>
    /// Exact matches cannot tie against the config shapes — no two blocks declare a field by the
    /// same name — but near ones can, since a typo can sit one edit from two different names. The
    /// order is what makes the message reproducible rather than dependent on the order the
    /// vocabulary happened to be enumerated in.
    /// </remarks>
    [Fact]
    public void Nearest_EquallyCloseCandidates_AreAllReturnedInOrdinalOrder() =>
        Assert.Equal(["bat", "cat"], NearMiss.Nearest("aat", ["cat", "bat"], name => name));

    /// <summary>
    /// Candidates that share a spelling are all returned, and their order between themselves is not
    /// something this method decides.
    /// </summary>
    /// <remarks>
    /// The reason a caller that wants one answer cannot simply take the first: ordering by the
    /// spelling cannot separate two candidates spelled the same way — one field name declared by
    /// two blocks — so they keep the order they arrived in, which for the config shapes is
    /// <c>Type.GetProperties()</c>'s and therefore not guaranteed. Returning both is what forces the
    /// caller to order by whatever does separate them; see
    /// <see cref="DeveloperConfigShape.NearMissFieldsOf"/>.
    /// </remarks>
    [Fact]
    public void Nearest_CandidatesSharingASpelling_AreAllReturned()
    {
        (string Field, string Block)[] candidates =
            [("connectionString", "kubernetes"), ("connectionString", "direct")];

        var nearest = NearMiss.Nearest("conectionString", candidates, candidate => candidate.Field);

        Assert.Equal(2, nearest.Count);
        Assert.All(nearest, candidate => Assert.Equal("connectionString", candidate.Field));
        Assert.Equal(["direct", "kubernetes"], nearest.Select(candidate => candidate.Block).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Nearest_NoCandidateResembles_ReturnsEmpty() =>
        Assert.Empty(NearMiss.Nearest("nonsense", ["path", "ref", "url", "namespace"], name => name));

    /// <remarks>
    /// Configuration keys are case-insensitive, so a key differing only in case is not a
    /// misspelling of a name — it is that name, and would have matched exactly before any of this
    /// was asked.
    /// </remarks>
    [Fact]
    public void Nearest_DiffersOnlyByCase_IsAnExactMatch() =>
        Assert.Equal(["path"], NearMiss.Nearest("PATH", ["path", "port"], name => name));

    /// <summary>
    /// <see cref="NearMiss.MisspellingOf"/> asks the question the other way round: one known name,
    /// and a list of words the developer wrote.
    /// </summary>
    [Fact]
    public void MisspellingOf_OneWrittenNameResembles_IsReturned() =>
        Assert.Equal("order", NearMiss.MisspellingOf("orders", ["billing", "order"]));

    [Fact]
    public void MisspellingOf_NothingResembles_ReturnsNull() =>
        Assert.Null(NearMiss.MisspellingOf("orders", ["billing", "shipping"]));

    [Fact]
    public void MisspellingOf_NoCandidatesAtAll_ReturnsNull() =>
        Assert.Null(NearMiss.MisspellingOf("orders", []));

    /// <remarks>
    /// The tolerance is the known name's, not each candidate's, which is what separates this from
    /// <see cref="NearMiss.Nearest{T}"/>. <c>carted</c> is two edits from <c>cart</c> and long
    /// enough to allow two of its own, so scaling by the written word would offer it; <c>cart</c>
    /// is four letters and allows one, and two edits from four letters reaches far enough that the
    /// answer would be a guess.
    /// <para>
    /// The same candidate at the same distance, against a known name one letter longer, so the
    /// only thing that moved between the two lines is the length the tolerance is taken from.
    /// Pairing it with a candidate one edit away would prove nothing — one edit is inside every
    /// tolerance there is.
    /// </para>
    /// </remarks>
    [Fact]
    public void MisspellingOf_ToleranceComesFromTheKnownNameNotTheCandidate()
    {
        Assert.Null(NearMiss.MisspellingOf("cart", ["carted"]));
        Assert.Equal("carted", NearMiss.MisspellingOf("carts", ["carted"]));
    }

    /// <remarks>
    /// Ordinally <c>orderr</c> comes first: the two agree on <c>ord</c> and then it has <c>e</c>
    /// where the other has <c>r</c>. Enumeration order is the reverse, so a search that returned
    /// whichever it met first would answer differently.
    /// </remarks>
    [Fact]
    public void MisspellingOf_TwoEquallyCloseCandidates_TakesTheOrdinallyFirst() =>
        Assert.Equal("orderr", NearMiss.MisspellingOf("orders", ["ordrs", "orderr"]));

    /// <remarks>
    /// <c>aorder</c> is two edits from <c>orders</c> and ordinally first; <c>orderz</c> is one and
    /// ordinally second. Distance decides, and the spelling only breaks a tie.
    /// </remarks>
    [Fact]
    public void MisspellingOf_CloserCandidateWinsOverTheOrdinallyFirst() =>
        Assert.Equal("orderz", NearMiss.MisspellingOf("orders", ["aorder", "orderz"]));

    /// <remarks>
    /// A candidate differing from the known name only by case is that name — configuration keys are
    /// case-insensitive — so it is dropped rather than offered as a correction of itself. The
    /// root-key caller is the one that needs it: a file carrying an empty <c>services</c> beside a
    /// populated <c>service</c> is still searching, with <c>services</c> among the candidates.
    /// (The service-name caller cannot reach it, since its candidates are the names the catalog
    /// does <em>not</em> declare and the name being looked for always is one it does — but that is
    /// its own restriction rather than something this may drop.)
    /// </remarks>
    [Fact]
    public void MisspellingOf_CandidateDiffersOnlyByCase_IsNotAMisspelling() =>
        Assert.Null(NearMiss.MisspellingOf("orders", ["ORDERS"]));

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("path", "path", 0)]
    [InlineData("pth", "path", 1)]
    [InlineData("paht", "path", 1)]
    [InlineData("", "path", 4)]
    public void EditDistance_CountsInsertsDeletesSubstitutionsAndSwaps(string from, string to, int expected) =>
        Assert.Equal(expected, NearMiss.EditDistance(from, to));

    /// <summary>
    /// The restricted form: no substring is edited twice, so <c>ca</c> to <c>abc</c> is three rather
    /// than the two the unrestricted algorithm would give.
    /// </summary>
    /// <remarks>
    /// Recorded rather than fixed. The unrestricted variant costs an alphabet-sized table to change
    /// an answer no vocabulary here can produce, and the answer only has to order candidates by
    /// resemblance — nothing depends on the distance being metric.
    /// </remarks>
    [Fact]
    public void EditDistance_SwapWithAnEditInsideIt_IsNotCountedAsOne() =>
        Assert.Equal(3, NearMiss.EditDistance("ca", "abc"));

    /// <summary>
    /// A swapped adjacent pair costs one edit, not the two a substitution each way would.
    /// </summary>
    /// <remarks>
    /// The reason it has to: the fields this matches against are short, so they get a single edit,
    /// and at two every transposition in the vocabulary fell outside tolerance. See
    /// <see cref="Nearest_TransposedPairInAShortField_IsRecognized"/> for what that cost.
    /// </remarks>
    [Fact]
    public void EditDistance_TransposedPair_CostsOne() =>
        Assert.Equal(1, NearMiss.EditDistance("paht", "path"));

    /// <summary>
    /// Every short field in the real vocabulary is reachable by swapping a pair of its letters.
    /// </summary>
    /// <remarks>
    /// Transposition is the commonest typo there is, and these are the most-typed fields in the
    /// file, so this is the case the feature exists for. Plain Levenshtein charged the swap two
    /// edits and a short name's tolerance is one, so every one of these printed the bare
    /// "Valid keys are …" list — while <c>pth</c>, a dropped letter in the same word, was answered.
    /// A reader hitting that saw no rule, only an arbitrary difference.
    /// </remarks>
    [Theory]
    [InlineData("paht", "path")]
    [InlineData("prot", "port")]
    [InlineData("tga", "tag")]
    [InlineData("erf", "ref")]
    [InlineData("rul", "url")]
    public void Nearest_TransposedPairInAShortField_IsRecognized(string written, string field) =>
        Assert.Equal(
            [field],
            NearMiss.Nearest(written, ["path", "ref", "url", "port", "tag", "scheme", "context", "namespace"], name => name));

    /// <summary>
    /// The transposition does not widen what a substitution reaches: two substitutions in a short
    /// name still get no suggestion.
    /// </summary>
    /// <remarks>
    /// The length rule is there to stop the guessing being confident and wrong, and charging a swap
    /// one edit was meant to answer a typo class rather than to relax that. <c>rap</c> is two
    /// substitutions from both <c>ref</c> and <c>tag</c> and a swap of neither.
    /// </remarks>
    [Fact]
    public void Nearest_TwoSubstitutionsInAShortField_IsStillNotRecognized() =>
        Assert.Empty(NearMiss.Nearest(
            "rap", ["path", "ref", "url", "port", "tag", "scheme", "context", "namespace"], name => name));
}
