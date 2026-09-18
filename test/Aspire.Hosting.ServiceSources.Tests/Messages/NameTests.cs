using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Tests.Messages;

public class NameTests
{
    private static string Render(string? value) => new Name(value).ToString();

    [Fact]
    public void SingleQuote_IsNeutralised() =>
        Assert.Equal("ord\\'ers", Render("ord'ers"));

    [Fact]
    public void DoubleQuote_IsNeutralised() =>
        Assert.Equal("ord\\\"ers", Render("ord\"ers"));

    [Fact]
    public void Backslash_IsDoubledBeforeTheQuoteEscape() =>
        Assert.Equal("ord\\\\\\'ers", Render("ord\\'ers"));

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

    // Pinned against what Label returned before the rewrite, so the 12 existing Label call sites
    // keep their rendering. Asserting Label == Name would be tautological once Label delegates,
    // and would leave exactly that behaviour unguarded.
    [Theory]
    [InlineData("orders", "orders")]
    [InlineData("ord'ers", "ord\\'ers")]
    [InlineData("ord\\ers", "ord\\\\ers")]
    [InlineData("ord\ners", "ord\\ners")]
    [InlineData("ord\ters", "ord\\ters")]
    [InlineData("ord ers", "ord ers")]
    [InlineData("", "")]
    // The one deliberate change: Label left the double quote alone, Name neutralises it.
    [InlineData("ord\"ers", "ord\\\"ers")]
    public void Label_RendersAsItDidBeforeExceptForTheDoubleQuote(string input, string expected)
    {
        Assert.Equal(expected, ServiceSourcesWarnings.Label(input));
        Assert.Equal(expected, Render(input));
    }
}
