using Aspire.Hosting.ServiceSources.BackingServices;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// A backing service's <c>connectionString</c> is split at <c>AddBackingService</c> time, so that a
/// malformed placeholder is a startup failure naming the backing service rather than a connection
/// string that reaches the app with <c>{secret:orders-creds}</c> still in it.
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

    [Fact]
    public void Parse_PortPlaceholder_IsRecognized()
    {
        var segments = Parse("Host=localhost;Port={port};Database=orders").Segments;

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
            Assert.IsType<ConnectionStringTemplate.Port>(Parse("{port:amqp}").Segments.Single()).Name);

    [Fact]
    public void Parse_SecretPlaceholder_KeepsTheSecretAndKey()
    {
        var secret = Assert.IsType<ConnectionStringTemplate.Secret>(Parse("{secret:orders-creds:password}").Segments.Single());

        Assert.Equal("orders-creds", secret.Name);
        Assert.Equal("password", secret.Key);
    }

    [Fact]
    public void Parse_PlaceholderKeywordInAnyCasing_IsRecognized() =>
        Assert.IsType<ConnectionStringTemplate.Port>(Parse("{PORT}").Segments.Single());

    [Fact]
    public void Parse_SeveralPlaceholders_AreAllFound() =>
        Assert.Equal(
            2,
            Parse("amqp://dev:{secret:rabbit:password}@localhost:{port:amqp}/").Segments
                .Count(segment => segment is not ConnectionStringTemplate.Literal));

    /// <summary>
    /// A brace that does not open one of our placeholders is literal text.
    /// </summary>
    /// <remarks>
    /// <c>Driver={PostgreSQL}</c> is an ordinary ODBC connection string and <c>{host}</c> is a
    /// perfectly good literal in one. A parser that claimed every <c>{…}</c> would reject both, so
    /// only a token whose first word is a keyword this package defines is read as a placeholder.
    /// </remarks>
    [Theory]
    [InlineData("Driver={PostgreSQL};Database=orders")]
    [InlineData("Server={host}\\instance")]
    [InlineData("Password={p0rt}")]
    public void Parse_BraceThatIsNotAPlaceholder_StaysLiteral(string template) =>
        Assert.Equal(template, Assert.IsType<ConnectionStringTemplate.Literal>(Parse(template).Segments.Single()).Text);

    /// <remarks>
    /// An unterminated brace is only a mistake once the keyword says a placeholder was meant:
    /// <c>Server={host</c> is a connection string that happens to end mid-brace, while
    /// <c>Port={port</c> is a placeholder someone forgot to close.
    /// </remarks>
    [Fact]
    public void Parse_UnterminatedNonPlaceholder_StaysLiteral() =>
        Assert.Equal("Server={host", Assert.IsType<ConnectionStringTemplate.Literal>(Parse("Server={host").Segments.Single()).Text);

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
    /// Doubling a placeholder's braces does not escape it — it is still read as a placeholder, with
    /// the extra braces as text around it.
    /// </summary>
    /// <remarks>
    /// Pinned because doubling is what a developer reaches for first, so this is the behaviour they
    /// meet. It is why the errors for an unresolvable placeholder say there is no escape rather than
    /// leaving them to infer one.
    /// </remarks>
    [Fact]
    public void Parse_DoubledBracesAroundAPlaceholder_StillReadThePlaceholder()
    {
        var segments = Parse("Port={{port}}").Segments;

        Assert.Collection(
            segments,
            segment => Assert.Equal("Port={", Assert.IsType<ConnectionStringTemplate.Literal>(segment).Text),
            segment => Assert.Null(Assert.IsType<ConnectionStringTemplate.Port>(segment).Name),
            segment => Assert.Equal("}", Assert.IsType<ConnectionStringTemplate.Literal>(segment).Text));
    }

    [Fact]
    public void Parse_UnterminatedPlaceholder_IsRejected() =>
        Assert.Contains("no closing '}'", Rejects("Host=localhost;Port={port").Message);

    [Theory]
    [InlineData("{secret}")]
    [InlineData("{secret:orders-creds}")]
    public void Parse_SecretMissingItsKey_IsRejected(string template) =>
        Assert.Contains("names a secret and a key", Rejects(template).Message);

    [Theory]
    [InlineData("{secret::password}")]
    [InlineData("{secret:orders-creds:}")]
    public void Parse_SecretWithAnEmptyPart_IsRejected(string template) =>
        Assert.Contains("must both be given", Rejects(template).Message);

    /// <remarks>
    /// Split on every colon rather than the first two, so an extra part is reported instead of being
    /// folded into the key — a key of <c>b:c</c> would fail at fetch time, in a cluster, against a
    /// key nobody wrote.
    /// </remarks>
    [Fact]
    public void Parse_SecretWithTooManyParts_IsRejected() =>
        Assert.Contains("exactly a name and a key", Rejects("{secret:a:b:c}").Message);

    [Fact]
    public void Parse_PortWithAnEmptyName_IsRejected() =>
        Assert.Contains("port name after 'port:' is empty", Rejects("{port:}").Message);

    [Fact]
    public void Parse_PortWithTooManyParts_IsRejected() =>
        Assert.Contains("at most one name", Rejects("{port:a:b}").Message);

    /// <summary>
    /// Every rejection names the backing service and the configuration key, since the file is only
    /// the lowest layer the value can arrive from.
    /// </summary>
    [Fact]
    public void Parse_Rejection_NamesTheBackingServiceAndTheKey()
    {
        var message = Rejects("{secret:orders-creds}").Message;

        Assert.Contains($"Backing service '{Name}'", message);
        Assert.Contains(Key, message);
        Assert.Contains("ServiceSources__BackingServices__orders-db__Direct__ConnectionString", message);
    }

    /// <summary>
    /// A keyword-shaped token that was never meant as a placeholder is told that the text cannot be
    /// kept, not only what a well-formed placeholder would look like.
    /// </summary>
    /// <remarks>
    /// <c>PWD={secret}</c> is an ODBC-quoted password that happens to be the word, and it is
    /// keyword-shaped, so it lands on the malformed path rather than passing through as text. Told
    /// only that "a secret placeholder names a secret and a key inside it", its author would go on
    /// trying to write one. The fact they need is that the spelling is unavailable whatever they do
    /// to it — there is no escape, and doubling the braces is not one.
    /// </remarks>
    [Theory]
    [InlineData("PWD={secret}")]
    [InlineData("PWD={secret:a}")]
    [InlineData("PWD={secret:a:b:c}")]
    [InlineData("Port={port:}")]
    [InlineData("Port={port:a:b}")]
    public void Parse_KeywordShapedTextThatIsNotAPlaceholder_SaysTheTextCannotBeKept(string template)
    {
        var message = Rejects(template).Message;

        Assert.Contains("was meant as text, it cannot be", message);
        Assert.Contains("there is no escape for it", message);
    }

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
    [InlineData("{PORT}")]
    [InlineData("{Port}")]
    [InlineData("{PORT:amqp}")]
    public void Parse_PortKeywordInAnyCasing_IsAPlaceholder(string template) =>
        Assert.IsType<ConnectionStringTemplate.Port>(Assert.Single(Parse(template).Segments));

    [Theory]
    [InlineData("{SECRET:a:b}")]
    [InlineData("{Secret:a:b}")]
    public void Parse_SecretKeywordInAnyCasing_IsAPlaceholder(string template) =>
        Assert.IsType<ConnectionStringTemplate.Secret>(Assert.Single(Parse(template).Segments));

    /// <summary>
    /// An upper-case token that is keyword-shaped but unreadable is rejected, not passed through as
    /// text.
    /// </summary>
    /// <remarks>
    /// The claim the README makes when it says `{PORT}` and `{secret}` alike are unavailable: it
    /// holds only if the casing reaches the malformed path too, which nothing pinned.
    /// </remarks>
    [Theory]
    [InlineData("PWD={SECRET}")]
    [InlineData("Port={PORT:}")]
    public void Parse_UnreadableKeywordInAnyCasing_IsStillRejected(string template) =>
        Assert.Contains("there is no escape for it", Rejects(template).Message);

    /// <summary>
    /// A message quotes the token as the developer spelled it, not as the keyword constants spell
    /// it.
    /// </summary>
    /// <remarks>
    /// Rebuilding the token from the constants normalized the casing, so a message about
    /// <c>{PORT}</c> quoted <c>'{port}'</c> — a spelling nowhere in the file, and silent about the
    /// one that is.
    /// </remarks>
    [Fact]
    public void Parse_Rejection_QuotesTheTokenAsWritten()
    {
        var message = Rejects("PWD={SECRET:a}").Message;

        Assert.Contains("'{SECRET:a}'", message);
        Assert.DoesNotContain("'{secret:a}'", message);
    }

    /// <summary>
    /// The keyword has to match the whole word, so a token that merely starts with one is text.
    /// </summary>
    /// <remarks>
    /// The rule the messages and the docs state. Claimed as a prefix, they would send someone whose
    /// password is <c>{secretstore}</c> off to rewrite a connection string that works.
    /// </remarks>
    [Theory]
    [InlineData("{portal}")]
    [InlineData("{secretariat}")]
    [InlineData("{secrets:a}")]
    [InlineData("Driver={secretstore}")]
    public void Parse_TokenMerelyStartingWithAKeyword_IsText(string template) =>
        Assert.Equal(
            template,
            Assert.IsType<ConnectionStringTemplate.Literal>(Assert.Single(Parse(template).Segments)).Text);
}
