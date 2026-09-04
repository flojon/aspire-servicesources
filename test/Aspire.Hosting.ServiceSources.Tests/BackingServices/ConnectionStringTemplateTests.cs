using Aspire.Hosting.ServiceSources.BackingServices;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// A backing service's <c>connectionString</c> is split at <c>AddBackingService</c> time, so that a
/// malformed placeholder is a startup failure naming the backing service rather than a connection
/// string that reaches the app with <c>${secret:orders-creds}</c> still in it.
/// </summary>
public class ConnectionStringTemplateTests
{
    private const string Name = "orders-db";

    private const string Key = "ServiceSources:BackingServices:orders-db:Direct:ConnectionString";

    private static ConnectionStringTemplate Parse(string template) =>
        ConnectionStringTemplate.Parse(template, Name, Key);

    private static ServiceSourcesConfigurationException Rejects(string template) =>
        Assert.Throws<ServiceSourcesConfigurationException>(() => Parse(template));

    [Fact]
    public void Parse_NoPlaceholders_IsOneLiteral()
    {
        var literal = Assert.IsType<ConnectionStringTemplate.Literal>(Assert.Single(Parse("Host=localhost").Segments));

        Assert.Equal("Host=localhost", literal.Text);
    }

    [Fact]
    public void Parse_EmptyTemplate_HasNoSegments() => Assert.Empty(Parse("").Segments);

    /// <summary>
    /// A brace-quoted value that happens to be a keyword is text, because braces reserve nothing.
    /// </summary>
    /// <remarks>
    /// The case #207 was filed for. ODBC quotes a value in braces, so <c>PWD={secret}</c> is a
    /// password that happens to be the word — and under the brace syntax it was keyword-shaped,
    /// claimed as a placeholder, and rejected, with no spelling that made it text. Placeholders open
    /// on <c>${</c> instead, which no connection-string dialect uses, so the whole class of
    /// collision is gone rather than escaped.
    /// </remarks>
    [Fact]
    public void Parse_OdbcBraceQuotedKeywordValue_IsText()
    {
        const string template = "Driver={SQL Server};UID=sa;PWD={secret}";

        Assert.Equal(
            template,
            Assert.IsType<ConnectionStringTemplate.Literal>(Assert.Single(Parse(template).Segments)).Text);
    }

    [Fact]
    public void Parse_PortPlaceholder_IsRecognized()
    {
        var segments = Parse("Host=localhost;Port=${port};Database=orders").Segments;

        Assert.Collection(
            segments,
            segment => Assert.Equal("Host=localhost;Port=", Assert.IsType<ConnectionStringTemplate.Literal>(segment).Text),
            segment => Assert.Null(Assert.IsType<ConnectionStringTemplate.Port>(segment).Name),
            segment => Assert.Equal(";Database=orders", Assert.IsType<ConnectionStringTemplate.Literal>(segment).Text));
    }

    /// <remarks>
    /// The named form exists because <c>kubectl port-forward</c> carries several port pairs in one
    /// invocation, so a multi-port backend is one tunnel rather than one per port.
    /// </remarks>
    [Fact]
    public void Parse_NamedPortPlaceholder_KeepsTheName() =>
        Assert.Equal(
            "amqp",
            Assert.IsType<ConnectionStringTemplate.Port>(Parse("${port:amqp}").Segments.Single()).Name);

    [Fact]
    public void Parse_SecretPlaceholder_KeepsTheSecretAndKey()
    {
        var secret = Assert.IsType<ConnectionStringTemplate.Secret>(Parse("${secret:orders-creds:password}").Segments.Single());

        Assert.Equal("orders-creds", secret.Name);
        Assert.Equal("password", secret.Key);
    }

    [Fact]
    public void Parse_SeveralPlaceholders_AreAllFound() =>
        Assert.Equal(
            2,
            Parse("amqp://dev:${secret:rabbit:password}@localhost:${port:amqp}/").Segments
                .Count(segment => segment is not ConnectionStringTemplate.Literal));

    /// <summary>
    /// A brace reserves nothing at all — the keywords included, since a placeholder opens on
    /// <c>${</c>.
    /// </summary>
    /// <remarks>
    /// <c>Driver={PostgreSQL}</c> is an ordinary ODBC connection string and <c>{host}</c> is a
    /// perfectly good literal in one. The last two rows are the ones that changed in #207: under
    /// the brace syntax they were keyword-shaped and rejected, with no spelling that made them
    /// text.
    /// </remarks>
    [Theory]
    [InlineData("Driver={PostgreSQL};Database=orders")]
    [InlineData("Server={host}\\instance")]
    [InlineData("Password={p0rt}")]
    [InlineData("Port={port}")]
    [InlineData("PWD={secret:a}")]
    public void Parse_BraceThatIsNotAPlaceholder_StaysLiteral(string template) =>
        Assert.Equal(template, Assert.IsType<ConnectionStringTemplate.Literal>(Parse(template).Segments.Single()).Text);

    /// <summary>
    /// A <c>${…}</c> whose first word is not one of our keywords is text, so an AppHost whose own
    /// tooling expands <c>${…}</c> keeps working.
    /// </summary>
    /// <remarks>
    /// The reason unknown <c>${…}</c> is not rejected as a misspelled placeholder, which would
    /// otherwise read well and would give near-miss suggestions somewhere to go: a connection string
    /// carrying <c>${DB_PASS}</c> for something else to substitute is a connection string this
    /// package has no business failing.
    /// </remarks>
    [Theory]
    [InlineData("Password=${DB_PASS}")]
    [InlineData("Host=${host};Port=5432")]
    public void Parse_ForeignInterpolation_StaysLiteral(string template) =>
        Assert.Equal(template, Assert.IsType<ConnectionStringTemplate.Literal>(Parse(template).Segments.Single()).Text);

    /// <remarks>
    /// An unterminated token is only a mistake once the keyword says a placeholder was meant:
    /// <c>Server=${host</c> is text that happens to end mid-brace, while <c>Port=${port</c> is a
    /// placeholder someone forgot to close.
    /// </remarks>
    [Theory]
    [InlineData("Server={host")]
    [InlineData("Server=${host")]
    [InlineData("Password=$")]
    public void Parse_UnterminatedNonPlaceholder_StaysLiteral(string template) =>
        Assert.Equal(template, Assert.IsType<ConnectionStringTemplate.Literal>(Parse(template).Segments.Single()).Text);

    /// <summary>
    /// Braces are never rewritten — doubled ones included — because a connection string uses them
    /// with a doubling rule of its own.
    /// </summary>
    /// <remarks>
    /// A brace-doubling escape was added here and withdrawn. ODBC quotes a value in braces and
    /// doubles an embedded <c>}</c>, so <c>PWD={pa}}ss}</c> is the password <c>pa}ss</c>; collapsing
    /// that <c>}}</c> gave <c>PWD={pa}ss}</c>, which the driver reads as ending at the brace, and
    /// the app connected with <c>pa</c> and trailing rubbish. It does not require doubling
    /// <c>{</c>, so <c>PWD={{abc}</c> is the password <c>{abc</c>, and collapsing that <c>{{</c>
    /// dropped its first character. Both were silent. Being unable to write a literal
    /// <c>{port}</c> is a limitation; rewriting a working connection string is a bug, so the
    /// limitation is what ships.
    /// </remarks>
    [Theory]
    [InlineData("PWD={pa}}ss}")]
    [InlineData("PWD={{abc}")]
    [InlineData("Driver={PostgreSQL};Server={host}\\instance")]
    [InlineData("PWD={a{{b}}c}")]
    [InlineData("{{}}")]
    public void Parse_BracesInAConnectionString_AreNeverRewritten(string template) =>
        Assert.Equal(
            template,
            Assert.IsType<ConnectionStringTemplate.Literal>(Parse(template).Segments.Single()).Text);

    /// <summary>
    /// <c>$$</c> is not an escape. It is a literal <c>$</c> followed by whatever comes next, which
    /// for <c>$${port}</c> is a placeholder.
    /// </summary>
    /// <remarks>
    /// Pinned so that the claim on <see cref="ConnectionStringTemplate"/> — that <c>$${port}</c> is
    /// <em>available</em> as an escape the day something wants one — cannot quietly become the claim
    /// that it already is one. It also fixes what a password containing <c>$$</c> does: nothing, and
    /// that is the whole reason the collapse a brace escape would have needed was rejected.
    /// </remarks>
    [Fact]
    public void Parse_DoubledDollarBeforeAPlaceholder_IsNotAnEscape()
    {
        var segments = Parse("Port=$${port}").Segments;

        Assert.Collection(
            segments,
            segment => Assert.Equal("Port=$", Assert.IsType<ConnectionStringTemplate.Literal>(segment).Text),
            segment => Assert.Null(Assert.IsType<ConnectionStringTemplate.Port>(segment).Name));
    }

    [Fact]
    public void Parse_DoubledDollarInAPassword_IsLeftAlone() =>
        Assert.Equal(
            "PWD=pa$$word",
            Assert.IsType<ConnectionStringTemplate.Literal>(Assert.Single(Parse("PWD=pa$$word").Segments)).Text);

    /// <summary>
    /// A <c>${</c> that opens no placeholder is text, and the scan resumes one character in, so a
    /// placeholder nested behind one is still found.
    /// </summary>
    [Fact]
    public void Parse_PlaceholderBehindANonPlaceholder_IsStillFound()
    {
        var segments = Parse("${a${port}").Segments;

        Assert.Collection(
            segments,
            segment => Assert.Equal("${a", Assert.IsType<ConnectionStringTemplate.Literal>(segment).Text),
            segment => Assert.Null(Assert.IsType<ConnectionStringTemplate.Port>(segment).Name));
    }

    [Fact]
    public void Parse_UnterminatedPlaceholder_IsRejected() =>
        Assert.Contains("no closing '}'", Rejects("Host=localhost;Port=${port").Message);

    [Theory]
    [InlineData("${secret}")]
    [InlineData("${secret:orders-creds}")]
    public void Parse_SecretMissingItsKey_IsRejected(string template) =>
        Assert.Contains("names a secret and a key", Rejects(template).Message);

    [Theory]
    [InlineData("${secret::password}")]
    [InlineData("${secret:orders-creds:}")]
    public void Parse_SecretWithAnEmptyPart_IsRejected(string template) =>
        Assert.Contains("must both be given", Rejects(template).Message);

    /// <remarks>
    /// Split on every colon rather than the first two, so an extra part is reported instead of being
    /// folded into the key — a key of <c>b:c</c> would fail at fetch time, in a cluster, against a
    /// key nobody wrote.
    /// </remarks>
    [Fact]
    public void Parse_SecretWithTooManyParts_IsRejected() =>
        Assert.Contains("exactly a name and a key", Rejects("${secret:a:b:c}").Message);

    [Fact]
    public void Parse_PortWithAnEmptyName_IsRejected() =>
        Assert.Contains("port name after 'port:' is empty", Rejects("${port:}").Message);

    [Fact]
    public void Parse_PortWithTooManyParts_IsRejected() =>
        Assert.Contains("at most one name", Rejects("${port:a:b}").Message);

    /// <summary>
    /// Every rejection names the backing service and the configuration key, since the file is only
    /// the lowest layer the value can arrive from.
    /// </summary>
    [Fact]
    public void Parse_Rejection_NamesTheBackingServiceAndTheKey()
    {
        var message = Rejects("${secret:orders-creds}").Message;

        Assert.Contains($"Backing service '{Name}'", message);
        Assert.Contains(Key, message);
        Assert.Contains("ServiceSources__BackingServices__orders-db__Direct__ConnectionString", message);
    }

    /// <summary>
    /// A rejection says what is wrong with the token and stops — it no longer has to say that the
    /// text cannot be kept.
    /// </summary>
    /// <remarks>
    /// Under the brace syntax that paragraph was load-bearing, because <c>PWD={secret}</c> — an
    /// ODBC-quoted password that happens to be the word — landed on this path rather than passing
    /// through, and its author had to be told no spelling would help. That string is text now
    /// (<see cref="Parse_OdbcBraceQuotedKeywordValue_IsText"/>), so anything reaching a rejection was
    /// written as a placeholder, and the paragraph would answer a question nobody asked. Pinned so
    /// it is not reintroduced by habit.
    /// </remarks>
    [Fact]
    public void Parse_Rejection_DoesNotExplainAnEscapeThatIsNotNeeded()
    {
        var message = Rejects("${secret:orders-creds}").Message;

        Assert.DoesNotContain("was meant as text", message);
        Assert.DoesNotContain("no escape", message);
    }

    /// <summary>
    /// The reserved shape did not go away with the braces: a keyword-shaped <c>${…}</c> that cannot
    /// be read still fails rather than passing through as text.
    /// </summary>
    /// <remarks>
    /// What the type's remarks and the README claim, which is the honest half of the #207 change.
    /// Moving to <c>${</c> made the collision vanishingly unlikely; it did not add an escape.
    /// </remarks>
    [Theory]
    [InlineData("PWD=${secret}")]
    [InlineData("PWD=${secret:a}")]
    [InlineData("PWD=${secret:a:b:c}")]
    [InlineData("Port=${port:}")]
    [InlineData("Port=${port:a:b}")]
    public void Parse_KeywordShapedTokenThatCannotBeRead_IsRejectedNotText(string template) =>
        Assert.Contains("which cannot be read", Rejects(template).Message);

    /// <summary>
    /// The keyword is matched case-insensitively, so an upper-case token is a placeholder rather
    /// than text.
    /// </summary>
    /// <remarks>
    /// Asserted as "the single segment is this placeholder" rather than as "no segment is a
    /// literal", which an empty segment list satisfies without the template having been read at
    /// all.
    /// </remarks>
    [Theory]
    [InlineData("${PORT}")]
    [InlineData("${Port}")]
    [InlineData("${PORT:amqp}")]
    public void Parse_PortKeywordInAnyCasing_IsAPlaceholder(string template) =>
        Assert.IsType<ConnectionStringTemplate.Port>(Assert.Single(Parse(template).Segments));

    [Theory]
    [InlineData("${SECRET:a:b}")]
    [InlineData("${Secret:a:b}")]
    public void Parse_SecretKeywordInAnyCasing_IsAPlaceholder(string template) =>
        Assert.IsType<ConnectionStringTemplate.Secret>(Assert.Single(Parse(template).Segments));

    /// <summary>
    /// An upper-case token that is keyword-shaped but unreadable is rejected, not passed through as
    /// text.
    /// </summary>
    /// <remarks>
    /// The claim the README makes when it says <c>${PORT}</c> and <c>${secret}</c> alike are
    /// unavailable: it holds only if the casing reaches the malformed path too, which nothing
    /// pinned.
    /// </remarks>
    [Theory]
    [InlineData("PWD=${SECRET}")]
    [InlineData("Port=${PORT:}")]
    public void Parse_UnreadableKeywordInAnyCasing_IsStillRejected(string template) =>
        Assert.Contains("which cannot be read", Rejects(template).Message);

    /// <summary>
    /// A message quotes the token as the developer spelled it, not as the keyword constants spell
    /// it.
    /// </summary>
    /// <remarks>
    /// Rebuilding the token from the constants normalized the casing, so a message about
    /// <c>${PORT}</c> quoted <c>'${port}'</c> — a spelling nowhere in the file, and silent about the
    /// one that is.
    /// </remarks>
    [Fact]
    public void Parse_Rejection_QuotesTheTokenAsWritten()
    {
        var message = Rejects("PWD=${SECRET:a}").Message;

        Assert.Contains("'${SECRET:a}'", message);
        Assert.DoesNotContain("'${secret:a}'", message);
    }

    /// <summary>
    /// The keyword has to match the whole word, so a token that merely starts with one is text.
    /// </summary>
    /// <remarks>
    /// The rule the messages and the docs state. Claimed as a prefix, they would send someone whose
    /// password is <c>${secretstore}</c> off to rewrite a connection string that works.
    /// </remarks>
    [Theory]
    [InlineData("${portal}")]
    [InlineData("${secretariat}")]
    [InlineData("${secrets:a}")]
    [InlineData("Driver=${secretstore}")]
    public void Parse_TokenMerelyStartingWithAKeyword_IsText(string template) =>
        Assert.Equal(
            template,
            Assert.IsType<ConnectionStringTemplate.Literal>(Assert.Single(Parse(template).Segments)).Text);
}
