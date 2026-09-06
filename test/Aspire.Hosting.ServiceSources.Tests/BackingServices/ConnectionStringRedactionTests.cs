using System.Diagnostics;
using System.Globalization;
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
    [InlineData("Endpoint=sb://ns.servicebus.windows.net/;SharedAccessKeyName=root;SharedAccessKey=hunter2",
                "Endpoint=***;SharedAccessKeyName=***;SharedAccessKey=***")]
    [InlineData("BlobEndpoint=https://acct.blob.core.windows.net/;SharedAccessSignature=sv=2021&sig=hunter2",
                "BlobEndpoint=***;SharedAccessSignature=***")]
    // The case no blocklist names, and the reason this is an allowlist.
    [InlineData("Host=h;Rotation Key=hunter2", "Host=h;Rotation Key=***")]
    public void AValueUnderAnUnrecognisedKey_IsMasked(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// A URI authority is reduced to its host, whatever the password inside it contains.
    /// </summary>
    /// <remarks>
    /// The authority is not ended by <c>/</c>, <c>?</c> or <c>#</c>: all three are legal unencoded
    /// in a password people actually write, and a rule that stopped at them printed the password
    /// whole. The last <c>@</c> is what bounds it, and the whole userinfo goes — inside one there is
    /// no telling a username from a password, since <c>redis://:pass@h</c> has only the latter.
    /// </remarks>
    [Theory]
    [InlineData("postgresql://orders_app:hunter2@db.internal:5432/orders",
                "postgresql://***@db.internal:5432/orders")]
    // No username at all, the canonical Redis URL before ACLs.
    [InlineData("redis://:hunter2@db.internal:6379", "redis://***@db.internal:6379")]
    // RFC 3986 puts ';' in sub-delims, which userinfo admits raw, so a password may carry one.
    [InlineData("redis://user:pa;ss@db.internal:6379", "redis://***@db.internal:6379")]
    [InlineData("mongodb://user:p;w@db.internal:27017", "mongodb://***@db.internal:27017")]
    // The other sub-delims, each of which would end an authority under RFC 3986's own rule.
    [InlineData("redis://user:pa#ss@db.internal:6379", "redis://***@db.internal:6379")]
    [InlineData("redis://user:pa?ss@db.internal:6379", "redis://***@db.internal:6379")]
    [InlineData("postgresql://app:8Kx/2Qz+w7A=@db.internal:5432/orders",
                "postgresql://***@db.internal:5432/orders")]
    // No scheme: a bare authority says the same thing, and an allowlisted key may hold one.
    [InlineData("Data Source=user:hunter2@h:1433", "Data Source=***@h:1433")]
    // An '@' before the '://' means there is no scheme in front of it, so this is not a URI and
    // there is nothing here to recognise.
    [InlineData("x:y@a://b", "***")]
    public void AUriAuthority_IsMaskedToItsHost(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

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
    // A query parameter, which the previous blocklist did catch — this must not regress.
    [InlineData("redis://h:6379/0?password=hunter2", "redis://h:6379/0?password=***")]
    // An option list whose head is an address, which is recognised by shape and kept.
    [InlineData("localhost:6379,ssl=false,password=hunter2", "localhost:6379,ssl=***,password=***")]
    // The no-corruption shape without the leading keyword, so the URI branch handles it alone.
    [InlineData("tcp://db.internal:1433;UID=a@b.com;Password=hunter2",
                "tcp://db.internal:1433;UID=a@b.com;Password=***")]
    public void ASecretBehindAnUnfamiliarSeparator_IsStillMasked(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// A pair written inside an unrecognised value does not escape from it.
    /// </summary>
    /// <remarks>
    /// libpq separates its pairs with spaces, so a value under an allowlisted key can carry several
    /// more pairs and one of them can be the password. Reading a space as a separator everywhere
    /// would undo that: the tail of an unrecognised value would be re-read as pairs of its own, and
    /// any of them whose key happened to be allowlisted would be printed. Here <c>def</c> is not a
    /// username — it is the second half of a value nothing recognised.
    /// </remarks>
    [Theory]
    [InlineData("Rotation Key=abc user=def", "Rotation Key=***")]
    [InlineData("Rotation Key=abc host=db.internal port=5432", "Rotation Key=***")]
    public void APairInsideAnUnrecognisedValue_IsMaskedWithIt(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// Space around the <c>=</c> is layout, and does not hide a pair from the scan.
    /// </summary>
    /// <remarks>
    /// Every keyword dialect trims it, and <c>DbConnectionStringBuilder</c> parses it, so a key that
    /// is not read as one because a space sits in front of its <c>=</c> takes its value with it —
    /// into the value of whatever pair came before, where nothing looks at it again. That printed
    /// the password, and printed it with no note attached, since the result matched the input.
    /// </remarks>
    [Theory]
    [InlineData("Host=localhost;RotationKey = hunter2;Database=orders",
                "Host=localhost;RotationKey = ***;Database=orders")]
    [InlineData("Host=h;Rotation Key =hunter2", "Host=h;Rotation Key =***")]
    [InlineData("Host=localhost ; Rotation Key = hunter2", "Host=localhost ; Rotation Key = ***")]
    public void SpaceAroundTheEqualsSign_DoesNotHideAPairFromTheScan(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// The longest key wins, because the shortest one is the one that prints a secret.
    /// </summary>
    /// <remarks>
    /// Reading <c>Custom Port</c> as the allowlisted <c>Port</c> preceded by some other text would
    /// print the value behind it.
    /// </remarks>
    [Fact]
    public void AKeyEndingInAnAllowlistedWord_IsNotReadAsThatWord()
        => Assert.Equal(
            "Host=x Custom Port=***",
            ConnectionStringRedaction.Redact("Host=x Custom Port=5432"));

    /// <summary>
    /// A conventional keyword is masked even where nothing marks it off as a pair.
    /// </summary>
    /// <remarks>
    /// What the retained keyword list is for. Here the password sits inside the value of an
    /// allowlisted key, behind a <c>:</c> that introduces nothing, so the scan has no reason to
    /// treat it as a pair and would print it. Naming the keyword outright is the backstop that
    /// makes it impossible for this rewrite to print something the previous one hid.
    /// </remarks>
    [Fact]
    public void AKeywordBehindNoSeparatorAtAll_IsStillMasked()
        => Assert.Equal(
            "Data Source=file:pwd=***",
            ConnectionStringRedaction.Redact("Data Source=file:pwd=hunter2"));

    /// <summary>
    /// A keyword that merely resembles a credential is masked too, and that is the trade.
    /// </summary>
    /// <remarks>
    /// Under the previous list these were the near misses worth being careful about, since a
    /// blocklist that caught them would have redacted an expiry. An allowlist has the opposite
    /// problem and takes it knowingly: a value nothing recognises is hidden whether or not it was
    /// ever secret. Mildly annoying, never dangerous.
    /// </remarks>
    [Theory]
    [InlineData("Host=db.internal;TokenExpiry=30;Database=orders", "Host=db.internal;TokenExpiry=***;Database=orders")]
    [InlineData("Host=db.internal;PasswordExpiry=30;Database=orders", "Host=db.internal;PasswordExpiry=***;Database=orders")]
    [InlineData("Host=db.internal;Integrated Security=SSPI;Database=orders",
                "Host=db.internal;Integrated Security=***;Database=orders")]
    public void AKeywordThatOnlyLooksLikeACredential_IsMaskedAnyway(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// Text inside a recognised value that is not itself recognised does not ride out on it.
    /// </summary>
    /// <remarks>
    /// The value of an allowlisted key is printed, and it runs to the next key the scan finds — so
    /// anything the scan cannot see as a key travels inside it. A key does not begin with a digit,
    /// or with punctuation, or in a script this does not read, and each of those printed a password
    /// whole. Worse, silently: the result equalled the input, so the message appended no note and
    /// said in effect that nothing had been hidden.
    /// </remarks>
    [Theory]
    // A key the scan cannot see, because a key does not begin with a digit.
    [InlineData("Host=db.internal;2fa=hunter2;Database=orders", "Host=db.internal;***;Database=orders")]
    [InlineData("host=db.internal port=5432 2fa=hunter2", "host=db.internal port=5432 ***")]
    // Nor in a script this does not read, nor behind punctuation outside a key's charset.
    [InlineData("Host=db.internal;Lösenord=hunter2", "Host=db.internal;***")]
    [InlineData("Host=db.internal;auth[token]=hunter2", "Host=db.internal;***")]
    [InlineData("Host=db.internal;my$key=hunter2", "Host=db.internal;***")]
    // Nor text that is not a pair at all.
    [InlineData("Host=h;hunter2", "Host=h;***")]
    [InlineData("Port=5432;hunter2", "Port=5432;***")]
    [InlineData("Host=h hunter2", "Host=h ***")]
    [InlineData("Host=h,hunter2", "Host=h,***")]
    [InlineData("Host=db.internal;Port=;2fa=hunter2", "Host=db.internal;Port=;***")]
    public void UnrecognisedTextInsideARecognisedValue_IsMaskedWithIt(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// A quoted value owns the separators inside it, and the tail of one is not a pair.
    /// </summary>
    /// <remarks>
    /// Reading a quoted password as ending at its first <c>;</c> left the rest to be scanned, and
    /// what followed was read as a key of its own — printing the second half of the password when
    /// that key happened to be one the allowlist names.
    /// </remarks>
    [Theory]
    [InlineData("Host=h;Password='a;Host=hunter2';Database=orders", "Host=h;Password=***;Database=orders")]
    [InlineData("Host=h;Password=\"a;UID=hunter2\"", "Host=h;Password=***")]
    public void AQuotedValueCarryingAKey_IsMaskedAsOneValue(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// Quoting a value does not make its contents printable.
    /// </summary>
    /// <remarks>
    /// A quote spans the separators inside it, which is the only reason to look for one — a Windows
    /// path under <c>Data Source</c> needs that. Spanning them is not vouching for what they
    /// separate: a <c>=</c> inside the quotes is a pair the scan was stopped from reading, and the
    /// quote is precisely what stopped it. Printing the span because it was quoted printed the pair
    /// with it.
    /// </remarks>
    [Theory]
    [InlineData("Host='db.internal;Custom=hunter2';Database=orders", "Host=***;Database=orders")]
    [InlineData("Server=\"db;auth[token]=hunter2\";Database=orders", "Server=***;Database=orders")]
    [InlineData("Port='5432;X-Api-Key=hunter2'", "Port=***")]
    [InlineData("host=db port=5432 user='app;2fa=hunter2'", "host=db port=5432 user=***")]
    public void AQuotedValueUnderARecognisedKey_IsStillReadForPairs(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// A password is found before anything is cut at a separator.
    /// </summary>
    /// <remarks>
    /// RFC 3986 admits the sub-delims raw in a userinfo, so a password legally carries the very
    /// characters a value is ended by. Cutting first left the fragment with no <c>@</c> in it, the
    /// authority rule then had nothing to find, and the password printed — the same mistake the
    /// authority rule was written to avoid, displaced into the step before it.
    /// </remarks>
    [Theory]
    [InlineData("Data Source=postgresql://app:hunter2#1@db.internal:5432/orders",
                "Data Source=postgresql://***@db.internal:5432/orders")]
    [InlineData("Host=redis://default:hunter2?x@cache:6379", "Host=redis://***@cache:6379")]
    [InlineData("Data Source=postgresql://app:hunter2;x@db.internal:5432/orders",
                "Data Source=postgresql://***@db.internal:5432/orders")]
    [InlineData("Data Source=user:hunter2 tail@h:1433", "Data Source=***@h:1433")]
    [InlineData("Host=u:hunter2,w@h,1433", "Host=***@h,1433")]
    [InlineData("Server=user:pa,ss@db.database.windows.net,1433", "Server=***@db.database.windows.net,1433")]
    // One level down, where the value is reached through a nested pair rather than a top-level one.
    [InlineData("Host=q host=app:hunter2#x@db:5432", "Host=q host=***@db:5432")]
    public void AnAuthorityUnderARecognisedKey_IsMaskedBeforeTheValueIsCut(
        string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// What follows the <c>@</c> is a host, and a host holds no pairs.
    /// </summary>
    /// <remarks>
    /// Masking the userinfo says nothing about what comes after it, and a pair written there was
    /// separated from the authority by nothing. This also answers the quoted value whose opening
    /// quote the authority mask has just consumed — with the quote gone the rules that read a quoted
    /// span never run, so <c>Host='x:y@z=hunter2'</c> came back with its password on show.
    /// </remarks>
    [Theory]
    [InlineData("Host='x:y@z=hunter2'", "Host=***")]
    [InlineData("host=db port=5432 user='a:b@c=hunter2'", "host=db port=5432 user=***")]
    [InlineData("Data Source=\"C:\\a@b=hunter2\\x.mdb\"", "Data Source=***")]
    [InlineData("Host='a:b@c'2fa=hunter2", "Host=***")]
    [InlineData("Host=u:p@h:5432/2fa=hunter2", "Host=***")]
    [InlineData("Host=a:b@c=hunter2", "Host=***")]
    [InlineData("Host=h port=a:b@c=hunter2", "Host=h port=***")]
    [InlineData("postgres://u:p@h/db=hunter2", "postgres://***@h/db=***")]
    public void APairWrittenAfterAnAuthority_IsNotVouchedForByIt(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// A comma lists hosts inside a URI's authority, and a host there is recognised by its shape.
    /// </summary>
    /// <remarks>
    /// Letting a comma through wherever no <c>=</c> happened to follow it printed whatever was
    /// written after the last host, and scanning the remainder at every comma made a long replica
    /// set quadratic besides. Past the authority a comma separates nothing this knows about.
    /// </remarks>
    [Theory]
    [InlineData("mongodb://h1:27017,hunter2", "mongodb://h1:27017,***")]
    [InlineData("cassandra://a:9042,hunter2", "cassandra://a:9042,***")]
    [InlineData("mongodb://h1:27017,h2:27017,hunter2", "mongodb://h1:27017,h2:27017,***")]
    [InlineData("mongodb://u:p@h1:27017,hunter2", "mongodb://***@h1:27017,***")]
    [InlineData("postgres://db:5432/orders,hunter2", "postgres://db:5432/orders,***")]
    public void AHostListInAUriAuthority_ShowsOnlyWhatIsShapedLikeAHost(
        string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// Oracle separates the name from the secret with a slash, and that is an authority too.
    /// </summary>
    /// <remarks>
    /// <c>scott/tiger@//host:1521/svc</c> is the spelling sqlplus, the thin JDBC driver and every
    /// Oracle client take. Requiring a <c>:</c> is what leaves <c>UID=a@b.com</c> alone, but on its
    /// own it left this alone too.
    /// </remarks>
    [Theory]
    [InlineData("Data Source=scott/hunter2@//db.internal:1521/orders", "Data Source=***@//db.internal:1521/orders")]
    [InlineData("Data Source=scott/hunter2@orcl", "Data Source=***@orcl")]
    [InlineData("User Id=scott/hunter2@db", "User Id=***@db")]
    public void OraclesSlashFormCredential_IsMaskedLikeAnyOtherAuthority(
        string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// Text glued to a closing quote was delimited by nothing, so it is shown as nothing.
    /// </summary>
    /// <remarks>
    /// The quote ends the value; whatever is stuck to it was never separated from anything, and
    /// reading on printed it. One space is the whole difference — <c>Host='a;b' 2fa=hunter2</c> was
    /// masked all along — which is what made this easy to miss.
    /// </remarks>
    [Theory]
    [InlineData("Host='a;b'2fa=hunter2", "Host=***")]
    [InlineData("Host=''2fa=hunter2", "Host=***")]
    [InlineData("Server=\"db;x\"2fa=hunter2", "Server=***")]
    [InlineData("host=db port=5432 user='a;b'2fa=hunter2", "host=db port=5432 user=***")]
    public void TextGluedToAClosingQuote_IsNotPartOfTheValue(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// A scheme is one unbroken run, not any text with a URL somewhere in it.
    /// </summary>
    /// <remarks>
    /// Treating anything containing <c>://</c> as a URI printed whatever came before the URL on the
    /// strength of the URL.
    /// </remarks>
    [Theory]
    [InlineData("s3cr3t redis://db.internal:6379", "***")]
    [InlineData("apikey-s3cr3t;redis://db.internal:6379", "***")]
    [InlineData("s3cr3t\tredis://db.internal:6379", "***")]
    // No scheme at all in front of the marker.
    [InlineData("://db.internal:6379", "***")]
    public void TextInFrontOfAUrl_IsNotPartOfTheUrl(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// A pair written after an empty value is read, rather than riding out on the space.
    /// </summary>
    /// <remarks>
    /// The space after an empty value is the only thing that would introduce a pair inside it, so
    /// trimming that space off as layout before looking hid the pair completely — at the top,
    /// because whitespace introduces nothing there, and inside, because the space was gone. The
    /// second row is the sharpest: it is a passing row of this file with the port emptied, which is
    /// exactly the shell-expansion case this message exists to diagnose.
    /// </remarks>
    [Theory]
    [InlineData("Host= Custom=hunter2", "Host= Custom=***")]
    [InlineData("host=db.internal port= 2fa=hunter2", "host=db.internal port= ***")]
    [InlineData("Host=localhost;Port= RotationKey=hunter2", "Host=localhost;Port= RotationKey=***")]
    [InlineData("Data Source= Custom=hunter2", "Data Source= Custom=***")]
    public void APairWrittenAfterAnEmptyValue_IsRead(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// An unterminated quote claims nothing beyond itself.
    /// </summary>
    /// <remarks>
    /// A quote with no closing partner delimits nothing, so letting it run to the end of the string
    /// made one stray apostrophe turn redaction off for everything after it — and, because the
    /// result then equalled the input, the message said no values had been hidden.
    /// </remarks>
    [Theory]
    [InlineData("Server='db;Bearer=hunter2;Uid=sa;Auth=s3cr3t", "Server=***;Bearer=***;Uid=sa;Auth=***")]
    [InlineData("Host=\"db;2fa=hunter2;Port=5432", "Host=***;Port=5432")]
    [InlineData("uid='app;otp=hunter2", "uid=***;otp=***")]
    [InlineData("Host=';Custom=hunter2", "Host=***;Custom=***")]
    public void AnUnterminatedQuote_DoesNotClaimTheRestOfTheString(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// What follows a bare URI's path is masked whichever separator introduced it.
    /// </summary>
    /// <remarks>
    /// Cutting at <c>?</c> and <c>#</c> but not at the other marks this treats as separators left
    /// the rest printing, and the keys in these are invisible to the scan — one begins with a digit,
    /// one is not written in ASCII, one is punctuation — so nothing else was going to catch them.
    /// </remarks>
    [Theory]
    [InlineData("redis://cache:6379/0&2fa=hunter2", "redis://cache:6379/0&***")]
    [InlineData("redis://cache:6379/0;Lösenord=hunter2", "redis://cache:6379/0;***")]
    [InlineData("postgres://db:5432/orders,2fa=hunter2", "postgres://db:5432/orders,***")]
    [InlineData("kafka://k:9092/t 2fa=hunter2", "kafka://k:9092/t ***")]
    [InlineData("redis://cache:6379/0 auth[token]=hunter2", "redis://cache:6379/0 ***")]
    public void WhatFollowsABareUrisPath_IsMaskedWhateverIntroducedIt(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// A URI's fragment is vetted by no more than its query is.
    /// </summary>
    [Theory]
    [InlineData("redis://h:6379/0#sig2=hunter2", "redis://h:6379/0#***")]
    [InlineData("redis://h:6379/0#hunter2", "redis://h:6379/0#***")]
    public void WhatFollowsAFragmentMarker_IsNotPrinted(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// A comma inside a value introduces a port, and a port is digits.
    /// </summary>
    /// <remarks>
    /// SQL Server writes <c>Server=localhost,1433</c>, which is the one thing after a comma this can
    /// vouch for — so the comma cannot simply end the value, and anything else after one is a field
    /// nothing looked at.
    /// </remarks>
    [Theory]
    [InlineData("Server=tcp:db.database.windows.net,1433", "Server=tcp:db.database.windows.net,1433")]
    [InlineData("Server=localhost,1433;Database=orders", "Server=localhost,1433;Database=orders")]
    [InlineData("Server=localhost,hunter2", "Server=localhost,***")]
    // A comma inside a quoted value belongs to the value, and an empty field is not a field.
    [InlineData("Server=\"a,b\",1433", "Server=\"a,b\",1433")]
    [InlineData("Host=h,,,1433", "Host=h,,,1433")]
    public void ACommaInsideAValue_ShowsAPortAndNothingElse(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

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
        => Assert.Equal("***", ConnectionStringRedaction.Redact("Host==x=hunter2"));

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
    // Spacing is the developer's own layout, and comes back exactly as they wrote it.
    [InlineData("Host = localhost;Port = 5432;Database = orders")]
    [InlineData("Host=localhost ; Port = 5432")]
    [InlineData("Host = localhost;Port = ;Database = orders")]
    // libpq writes 'user = dev', so the token after an empty value belongs to it. Read as a key of
    // its own it swallowed the pair after it, and the database was hidden under 'dev database'.
    [InlineData("host=db port=5432 user= dev database=orders")]
    [InlineData("Host=h user= dev port=5432")]
    // Space around the '=' after an empty value is layout too, and the value that follows it is a
    // value rather than a pair exactly when it carries no '=' of its own.
    [InlineData("Host= Port = 5432")]
    [InlineData("Host=\tPort = 5432")]
    [InlineData("Host= Port = ")]
    [InlineData("Host= hunter2")]
    // An Oracle descriptor is full of '=' and none of it was written after a space, so all of it is
    // this value.
    [InlineData("Data Source=(DESCRIPTION=(ADDRESS=(PROTOCOL=TCP)(HOST=h)(PORT=1521)))")]
    // Redis and Kafka address a tunnel with a bare host and port and no keys at all.
    [InlineData("localhost:6379")]
    [InlineData("[::1]:6379")]
    // A comma in a URI lists hosts: a replica set and a broker list are ordinary connection strings.
    [InlineData("mongodb://h1:27017,h2:27017/db")]
    // A '/' counts towards an authority only alongside an '@'; a path on its own is a value.
    [InlineData("Data Source=C:/db/x.mdb;Database=orders")]
    [InlineData("Data Source=/var/lib/pgdata")]
    [InlineData("user=DOMAIN/alice")]
    // A scheme with nothing after it, and a bare authority marker, are both still just text.
    [InlineData("a://")]
    [InlineData("cassandra://a:9042,b:9042")]
    // A '#' with nothing after it hides nothing, and is not deleted from the echo.
    [InlineData("Host=h;Port=5432#")]
    [InlineData("Host=h#")]
    public void AConnectionStringWithNothingToHide_IsReturnedUnchanged(string connectionString)
        => Assert.Equal(connectionString, ConnectionStringRedaction.Redact(connectionString));

    /// <summary>
    /// An empty value is never replaced.
    /// </summary>
    /// <remarks>
    /// An empty string cannot be a secret, and it is the whole diagnosis in the case this message
    /// exists for: a shell that expanded <c>${port}</c> away leaves the key behind with nothing in
    /// it. Masking it would assert that something was hidden where nothing was.
    /// </remarks>
    [Theory]
    [InlineData("Host=localhost;Port Number=;Database=orders")]
    [InlineData("Host=localhost;Rotation Key=;Database=orders")]
    public void AnEmptyValue_IsLeftEmptyRatherThanMasked(string connectionString)
        => Assert.Equal(connectionString, ConnectionStringRedaction.Redact(connectionString));

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
            ConnectionStringRedaction.Redact("Host=h;Password='a;b';Database=orders"));

    /// <summary>
    /// Text that is recognised as nothing at all is shown as nothing at all.
    /// </summary>
    [Theory]
    [InlineData("hunter2", "***")]
    [InlineData("=hunter2", "***")]
    // A host with no port is not distinguishable from a token that looks like a word, so it goes
    // the same way. The port is what makes 'localhost:6379' recognisable.
    [InlineData("localhost", "***")]
    public void UnrecognisableText_IsMaskedWhole(string connectionString, string expected)
        => Assert.Equal(expected, ConnectionStringRedaction.Redact(connectionString));

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
        var once = ConnectionStringRedaction.Redact(connectionString);

        Assert.Equal(once, ConnectionStringRedaction.Redact(once));
    }

    /// <summary>
    /// A pathological value is answered rather than survived.
    /// </summary>
    /// <remarks>
    /// The string is the developer's own configuration, so this is self-inflicted — but a
    /// configuration mistake must produce the configuration error, not a stack dump from inside the
    /// code that was building it. Recursion is what makes that a live risk, so this scan has none.
    /// </remarks>
    [Theory]
    [InlineData("a://b/?host=")]
    // A value of nothing but space-separated words asks "is a pair starting here?" at every one of
    // them, so it is the shape that catches a scan re-reading what it has already read.
    [InlineData("Host=a a a a a a a a ")]
    // A long run of whitespace is the shape that asks "may a pair begin here?" at every position in
    // it, so it is the one that catches a scan answering that question by walking backwards.
    [InlineData("Host=h; ")]
    // A replica set with no '=' anywhere: the comma rule must not re-scan the rest at every one.
    [InlineData("mongodb://h1:27017,")]
    public void AVeryLargeConnectionString_IsScannedInTimeAndWithoutExhaustingTheStack(string unit)
    {
        var pathological = string.Concat(Enumerable.Repeat(unit, 30_000)) + ";Password=hunter2";
        var watch = Stopwatch.StartNew();

        var redacted = ConnectionStringRedaction.Redact(pathological);

        // Generous by two orders of magnitude against a linear scan, and still far under what
        // quadratic behaviour costs at this length — the point is the shape of the curve, not the
        // machine this runs on.
        Assert.True(watch.ElapsedMilliseconds < 2_000, $"took {watch.ElapsedMilliseconds} ms");
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
        // On a thread of its own: test classes run in parallel here, and a culture set on a shared
        // one would be visible to whatever else happened to be running.
        var redacted = "";
        var worker = new Thread(() =>
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            redacted = ConnectionStringRedaction.Redact("Initial Catalog=orders;UID=dev");
        });

        worker.Start();
        worker.Join();

        Assert.Equal("Initial Catalog=orders;UID=dev", redacted);
    }
}
