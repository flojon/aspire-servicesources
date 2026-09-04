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
    /// <c>{</c>, so <c>PWD={{abc}</c> is the password <c>{abc}</c>'s cousin <c>{abc</c>, and
    /// collapsing that dropped a character. Both were silent. Being unable to write a literal
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
}
