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
    /// A doubled brace is that brace as text, which is how a connection string carries the one
    /// string it could not otherwise contain.
    /// </summary>
    /// <remarks>
    /// Unhandled, <c>{{port}}</c> parsed the <c>{port}</c> inside it as a placeholder and failed
    /// with an error about a port that cannot be substituted — for a value never meant as a
    /// substitution, and with nothing the reader could write instead. Doubling is also the spelling
    /// anyone reaches for first, having met it in every format string.
    /// </remarks>
    [Theory]
    [InlineData("{{port}}", "{port}")]
    [InlineData("{{secret:a:b}}", "{secret:a:b}")]
    [InlineData("Port={{port}};Database=orders", "Port={port};Database=orders")]
    [InlineData("{{}}", "{}")]
    public void Parse_DoubledBraces_AreOneBraceOfText(string template, string expected) =>
        Assert.Equal(expected, Assert.IsType<ConnectionStringTemplate.Literal>(Parse(template).Segments.Single()).Text);

    /// <summary>
    /// Escaping one placeholder leaves a real one beside it alone.
    /// </summary>
    [Fact]
    public void Parse_EscapedAndRealPlaceholderTogether_KeepsBoth()
    {
        var segments = Parse("Note={{port}};Port={port}").Segments;

        Assert.Collection(
            segments,
            segment => Assert.Equal("Note={port};Port=", Assert.IsType<ConnectionStringTemplate.Literal>(segment).Text),
            segment => Assert.Null(Assert.IsType<ConnectionStringTemplate.Port>(segment).Name));
    }

    /// <remarks>
    /// A single brace is already literal wherever it does not open a placeholder, so an ODBC string
    /// needs no escaping and gains none: doubling collapses, and everything else is untouched.
    /// </remarks>
    [Fact]
    public void Parse_SingleBracesInAnOdbcString_AreNotAltered() =>
        Assert.Equal(
            "Driver={PostgreSQL};Server={host}\\instance",
            Assert.IsType<ConnectionStringTemplate.Literal>(
                Parse("Driver={PostgreSQL};Server={host}\\instance").Segments.Single()).Text);

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
