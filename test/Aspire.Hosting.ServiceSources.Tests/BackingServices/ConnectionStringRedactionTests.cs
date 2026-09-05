using Aspire.Hosting.ServiceSources.BackingServices;

namespace Aspire.Hosting.ServiceSources.Tests.BackingServices;

/// <summary>
/// The redaction applied to the one message that echoes a whole connection string back.
/// </summary>
/// <remarks>
/// Exercised directly rather than through the message, because what needs covering is a matrix of
/// connection-string dialects rather than a matrix of AppHost configurations.
/// <see cref="KubernetesBackingServiceTests"/> keeps the end-to-end cases: that the echo reaches the
/// message, and what the message says about it.
/// </remarks>
public class ConnectionStringRedactionTests
{
    /// <summary>
    /// A value under a key the allowlist does not name is replaced, whatever the key is called.
    /// </summary>
    /// <remarks>
    /// The property this whole type exists for. A blocklist answers "is this key one of the ones we
    /// know hides a secret", which is a question that is never finished being answered; this asks
    /// "is this key one of the few known to hide nothing", and everything else is masked.
    /// </remarks>
    [Theory]
    // The shapes three separate corrections to the previous blocklist were needed to cover.
    [InlineData("Host=db.internal;Port=5432;Username=dev;Password=hunter2",
                "Host=db.internal;Port=5432;Username=dev;Password=***")]
    [InlineData("Host=db.internal;Port=5432;Pwd=hunter2", "Host=db.internal;Port=5432;Pwd=***")]
    [InlineData("redis://:hunter2@db.internal:6379", "redis://***@db.internal:6379")]
    [InlineData("Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=hunter2",
                "Endpoint=***;SharedAccessKeyName=***;SharedAccessKey=***")]
    [InlineData("BlobEndpoint=https://acct.blob.core.windows.net/;SharedAccessSignature=sv=2021&sig=hunter2",
                "BlobEndpoint=***;SharedAccessSignature=***")]
    // RFC 3986 puts ';' in sub-delims, which userinfo admits raw, so a password may carry one.
    [InlineData("redis://user:pa;ss@db.internal:6379", "redis://***@db.internal:6379")]
    [InlineData("mongodb://user:p;w@db.internal:27017", "mongodb://***@db.internal:27017")]
    // The case no blocklist names, and the reason this is an allowlist.
    [InlineData("Host=h;Rotation Key=hunter2", "Host=h;Rotation Key=***")]
    public void AValueUnderAnUnrecognisedKey_IsMasked(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Apply(connectionString));

    /// <summary>
    /// A secret is masked whatever separates the pair it sits in from its neighbours.
    /// </summary>
    /// <remarks>
    /// Redaction by key is only fail-closed if the scan that finds the keys is at least as
    /// permissive as every syntax that could have written the string. Each of these uses a
    /// separator or an authority terminator that a <c>';'</c>-splitting scan does not recognise, and
    /// each printed its password in full when this was written that way.
    /// </remarks>
    [Theory]
    // libpq conninfo: space-separated, and the leading key is one the allowlist passes.
    [InlineData("host=db.internal port=5432 user=dev password=hunter2",
                "host=db.internal port=5432 user=dev password=***")]
    // A URI with no path, so nothing terminates the authority before the keyword tail.
    [InlineData("mongodb://db.internal:27017;Password=hunter2", "mongodb://db.internal:27017;Password=***")]
    [InlineData("sb://ns.servicebus.windows.net;SharedAccessKey=hunter2",
                "sb://ns.servicebus.windows.net;SharedAccessKey=***")]
    // A scheme that is not a bare 'scheme://' at position 0.
    [InlineData("jdbc:postgresql://user:pw@h:5432/db?ssl=true", "jdbc:postgresql://***@h:5432/db?ssl=***")]
    // Sub-delims a URI password may carry raw, each of which ends an authority.
    [InlineData("redis://user:pa#ss@db.internal:6379", "redis://***@db.internal:6379")]
    [InlineData("redis://user:pa?ss@db.internal:6379", "redis://***@db.internal:6379")]
    [InlineData("postgresql://app:8Kx/2Qz+w7A=@db.internal:5432/orders",
                "postgresql://***@db.internal:5432/orders")]
    // An authority with no scheme at all, behind a key the allowlist passes.
    [InlineData("Data Source=user:hunter2@h:1433", "Data Source=***@h:1433")]
    // A query parameter, which the previous blocklist did catch — this must not regress.
    [InlineData("redis://h:6379/0?password=hunter2", "redis://h:6379/0?password=***")]
    public void ASecretBehindAnUnfamiliarSeparator_IsStillMasked(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Apply(connectionString));

    /// <summary>
    /// ADO.NET's <c>==</c> escape does not smuggle a value past the allowlist.
    /// </summary>
    /// <remarks>
    /// <c>Host==x=hunter2</c> parses as the key <c>host=x</c> holding <c>hunter2</c>, so reading the
    /// key as everything before the first <c>=</c> would find the allowlisted <c>Host</c> and print
    /// the rest. A doubled <c>=</c> means the key was not the key, so nothing here is recognised and
    /// nothing is printed.
    /// </remarks>
    [Fact]
    public void ADoubledEqualsSign_DoesNotMakeAnAllowlistedKey()
        => Assert.Equal("***", ConnectionStringRedaction.Apply("Host==x=hunter2"));

    /// <summary>
    /// A connection string of nothing but recognised keys comes back byte-identical.
    /// </summary>
    /// <remarks>
    /// The dominant case, and the reason the caller can tell "nothing was hidden" from "something
    /// was": it compares the result with what it passed in. Any normalisation on the way out — a
    /// trimmed key, a rebuilt separator — would make that comparison lie.
    /// </remarks>
    [Theory]
    [InlineData("Host=db.internal;Port=5432;Database=orders")]
    [InlineData("Host=localhost;Port=;Database=orders")]
    [InlineData("Host=h;Custom Port=")]
    [InlineData("Data Source=tcp://db.internal:1433;UID=a@b.com;Database=orders")]
    [InlineData("tcp://db.internal:1433;UID=a@b.com;Database=orders")]
    [InlineData("Data Source=\"C:\\a;b\\x.mdb\";Database=orders")]
    [InlineData("Host=h;Port=5432;")]
    [InlineData("Server=tcp:db.database.windows.net,1433;Initial Catalog=orders;User ID=dev")]
    // Redis and Kafka address a tunnel with a bare host and port and no keys at all.
    [InlineData("localhost:6379")]
    [InlineData("[::1]:6379")]
    public void AConnectionStringWithNothingToHide_IsReturnedUnchanged(string connectionString)
        => Assert.Equal(connectionString, ConnectionStringRedaction.Apply(connectionString));

    /// <summary>
    /// An empty value is never replaced.
    /// </summary>
    /// <remarks>
    /// An empty string cannot be a secret, and it is the whole diagnosis in the case this message
    /// exists for: a shell that expanded <c>${port}</c> away leaves the key behind with nothing in
    /// it. Masking it would assert that something was hidden where nothing was.
    /// </remarks>
    [Theory]
    [InlineData("Host=localhost;Port=;Database=orders")]
    [InlineData("Host=localhost;Port Number=;Database=orders")]
    public void AnEmptyValue_IsLeftEmptyRatherThanMasked(string connectionString)
        => Assert.DoesNotContain("***", ConnectionStringRedaction.Apply(connectionString));

    /// <summary>
    /// A quoted value carrying the separator is masked without inventing a second pair.
    /// </summary>
    /// <remarks>
    /// A value runs to the next key rather than to the next <c>';'</c>, so the quoting rule that
    /// lets a password contain a separator costs nothing here — there is no split to confuse.
    /// </remarks>
    [Fact]
    public void AQuotedValueContainingASeparator_IsMaskedAsOneValue()
        => Assert.Equal(
            "Host=h;Password=***;Database=orders",
            ConnectionStringRedaction.Apply("Host=h;Password='a;b';Database=orders"));

    /// <summary>
    /// Text that is recognised as nothing at all is shown as nothing at all.
    /// </summary>
    [Theory]
    [InlineData("hunter2", "***")]
    [InlineData("=hunter2", "***")]
    public void UnrecognisableText_IsMaskedWhole(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Apply(connectionString));

    /// <summary>
    /// Redacting an already-redacted string changes nothing further.
    /// </summary>
    /// <remarks>
    /// Not a use the caller has, but a cheap statement that the mask is inert input: a rule that
    /// treated <c>***</c> as content could turn one pass's output into the next pass's key.
    /// </remarks>
    [Theory]
    [InlineData("Host=db.internal;Port=5432;Username=dev;Password=hunter2")]
    [InlineData("postgresql://orders_app:hunter2@db.internal:5432/orders")]
    [InlineData("host=db.internal port=5432 user=dev password=hunter2")]
    public void RedactionIsIdempotent(string connectionString)
    {
        var once = ConnectionStringRedaction.Apply(connectionString);

        Assert.Equal(once, ConnectionStringRedaction.Apply(once));
    }

    /// <summary>
    /// A pathological value is answered rather than survived.
    /// </summary>
    /// <remarks>
    /// The string is the developer's own configuration, so this is self-inflicted — but a
    /// configuration mistake must produce the configuration error, not a stack dump from inside the
    /// code that was building it. Recursion is what makes that a live risk, so this scan has none.
    /// </remarks>
    [Fact]
    public void AVeryLargeConnectionString_IsScannedWithoutExhaustingTheStack()
    {
        var pathological = string.Concat(Enumerable.Repeat("a://b/?host=", 30_000)) + ";Password=hunter2";

        var redacted = ConnectionStringRedaction.Apply(pathological);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
    }

    /// <summary>
    /// The allowlist is matched without asking the current culture what a letter is.
    /// </summary>
    /// <remarks>
    /// Under <c>tr-TR</c> a culture-sensitive lower-casing maps the <c>I</c> of <c>Initial
    /// Catalog</c> and <c>UID</c> to a dotless <c>ı</c>, so the lookup misses and a developer in
    /// Turkey sees their catalog name replaced by <c>***</c>.
    /// </remarks>
    [Fact]
    public void TheAllowlist_IsMatchedIndependentlyOfTheCurrentCulture()
    {
        var original = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");

        try
        {
            Assert.Equal(
                "Initial Catalog=orders;UID=dev",
                ConnectionStringRedaction.Apply("Initial Catalog=orders;UID=dev"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = original;
        }
    }
}
