using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

public class NameTests
{
    private static string Render(string? value) => new Name(value).ToString();

    // The unicode escape rather than a backslashed apostrophe: the reader pastes this name back into
    // servicesources.local.json, and a backslashed apostrophe is not a legal JSON escape. Asserted
    // against the destination rather than against the spelling alone, because the spelling is only
    // right for as long as JSON accepts it.
    [Fact]
    public void SingleQuote_IsNeutralisedAsAJsonAndCSharpEscape()
    {
        Assert.Equal("ord\\u0027ers", Render("ord'ers"));

        using var json = System.Text.Json.JsonDocument.Parse($$"""{"{{Render("ord'ers")}}": 1}""");
        Assert.Equal("ord'ers", json.RootElement.EnumerateObject().First().Name);
    }

    [Fact]
    public void SingleQuote_EscapeIsRefusedByJsonInItsOldSpelling()
    {
        // The regression guard for the change above: the old spelling took the whole configuration
        // file down. ThrowsAny, because the reader throws the derived JsonReaderException.
        Assert.ThrowsAny<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonDocument.Parse("{\"ord\\'ers\": 1}"));
    }

    [Fact]
    public void DoubleQuote_IsNeutralised() =>
        Assert.Equal("ord\\\"ers", Render("ord\"ers"));

    [Fact]
    public void Backslash_IsDoubledBeforeTheQuoteEscape() =>
        Assert.Equal("ord\\\\\\u0027ers", Render("ord\\'ers"));

    [Fact]
    public void Newline_IsSpelledOutRatherThanEmitted()
    {
        var rendered = Render("orders\nFATAL: forged");
        Assert.DoesNotContain("\n", rendered, StringComparison.Ordinal);
        Assert.Contains("\\n", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_RendersEmpty() => Assert.Equal(string.Empty, Render(null));

    [Fact]
    public void ShortName_IsUnchanged() => Assert.Equal("orders", Render("orders"));

    [Fact]
    public void LongName_IsCappedWithAnEllipsis()
    {
        var rendered = Render(new string('a', 200));
        Assert.EndsWith("…", rendered, StringComparison.Ordinal);
        Assert.Equal(Name.MaxLength + 1, rendered.Length);
    }

    [Fact]
    public void Cap_DoesNotSplitASurrogatePair()
    {
        // 32 astral characters = 64 UTF-16 units, so the cap lands exactly on a pair boundary+1.
        var rendered = Render(string.Concat(Enumerable.Repeat("\U0001F600", 40)));

        Assert.All(rendered[..^1].Chunk(2), pair =>
        {
            Assert.True(char.IsHighSurrogate(pair[0]));
            Assert.True(char.IsLowSurrogate(pair[1]));
        });
    }

    [Fact]
    public void Cap_DoesNotLandInsideAnEscapeUnit()
    {
        // Each \n costs two rendered characters; a cut between them would emit a lone backslash.
        var rendered = Render(new string('\n', 40));

        Assert.DoesNotContain("\\…", rendered, StringComparison.Ordinal);
        Assert.EndsWith("n…", rendered, StringComparison.Ordinal);
    }

    // A lone high surrogate no longer reaches the walk verbatim: Bare spells it out upstream, so it
    // arrives as the six characters of \ud83d. What this still sweeps is the cap neighbourhood - the
    // run length decides where a desynchronised walk would land, so the whole range is swept rather
    // than one value. Both assertions above held at 63 while CutAt was broken.
    [Theory]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    public void Cap_SurvivesALoneHighSurrogate(int run)
    {
        var lone = new string('\uD83D', run);

        AssertCutIsAtAUnitBoundary(lone + new string('\n', 40));
        AssertCutIsAtAUnitBoundary(lone + string.Concat(Enumerable.Repeat("\U0001F600", 40)));
    }

    /// <summary>
    /// The cut must land between whole units of the escaped text. Compared against the uncapped
    /// escaping rather than against a shape, because a lone high surrogate the caller supplied may
    /// legitimately sit at the end — what must not happen is a genuine pair or an escape being split.
    /// </summary>
    private static void AssertCutIsAtAUnitBoundary(string value)
    {
        var escaped = Name.Escape(value);
        var body = Render(value)[..^1];

        Assert.StartsWith(body, escaped, StringComparison.Ordinal);
        Assert.NotEqual('\\', body[^1]);
        Assert.False(char.IsHighSurrogate(body[^1]) && char.IsLowSurrogate(escaped[body.Length]));
    }

    // An unpaired surrogate is category Surrogate, not Format, so IsInvisible says no and it reached
    // the reader raw - where UTF-8 collapses every one of them to the same U+FFFD and two different
    // names render identically. Escape rather than Render, so the cap plays no part in what is asserted.
    [Fact]
    public void LoneSurrogate_IsSpelledOutRatherThanReachingTheReaderRaw()
    {
        const char loneHigh = (char)0xD83D;
        const char loneLow = (char)0xDE00;
        var pair = char.ConvertFromUtf32(0x1F600);

        Assert.Equal("orders\\ud83d", Name.Escape("orders" + loneHigh));
        Assert.Equal("\\ude00orders", Name.Escape(loneLow + "orders"));
        Assert.Equal("\\ud83d\\ud83d", Name.Escape(new string(loneHigh, 2)));

        // The valid pair after a lone one must still render as itself: a printable pair is not an
        // invisible, and the lone surrogate before it must not consume its high half.
        Assert.Equal("\\ud83d" + pair, Name.Escape(loneHigh + pair));
    }

    // Pinned against what the now-deleted Label wrapper returned, so its former call sites (now
    // calling Name directly) keep their rendering. The double-quote arm records the one deliberate
    // change from Label's behaviour: Label left it alone, Name neutralises it.
    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("ord'ers", "ord\\u0027ers")]
    [InlineData("ord\\ers", "ord\\\\ers")]
    [InlineData("ord\ners", "ord\\ners")]
    [InlineData("ord\ters", "ord\\ters")]
    [InlineData("ord ers", "ord ers")]
    [InlineData("", "")]
    [InlineData("ord\"ers", "ord\\\"ers")]
    public void Label_RendersAsItDidBeforeExceptForTheDoubleQuote(string input, string expected) =>
        Assert.Equal(expected, Render(input));
}
