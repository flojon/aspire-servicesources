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

    /// <remarks>
    /// The tolerance is the candidate's, so a long name two edits away beats a short name that is
    /// closer in raw distance but outside its own tolerance. Filtering after taking the minimum
    /// would let the short one win and then be discarded, leaving no suggestion at all.
    /// </remarks>
    [Fact]
    public void Nearest_PrefersACandidateWithinItsOwnTolerance() =>
        Assert.Equal(["context"], NearMiss.Nearest("contxt", ["context", "tag"], name => name));

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

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("path", "path", 0)]
    [InlineData("pth", "path", 1)]
    [InlineData("paht", "path", 2)]
    [InlineData("", "path", 4)]
    public void EditDistance_CountsInsertsDeletesAndSubstitutions(string from, string to, int expected) =>
        Assert.Equal(expected, NearMiss.EditDistance(from, to));

    /// <remarks>
    /// A transposition costs two here, which is the Levenshtein answer rather than the Damerau one,
    /// and is why anything but a short name is allowed two edits.
    /// </remarks>
    [Fact]
    public void EditDistance_TransposedPair_CostsTwo() =>
        Assert.Equal(2, NearMiss.EditDistance("paht", "path"));
}
