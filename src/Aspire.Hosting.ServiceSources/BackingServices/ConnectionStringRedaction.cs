using System.Text;
using System.Text.RegularExpressions;

namespace Aspire.Hosting.ServiceSources.BackingServices;

/// <summary>
/// Hides the credentials in a connection string that is about to be echoed back to the developer.
/// </summary>
/// <remarks>
/// One message in this package quotes a whole, valid connection string — every other value it
/// echoes is malformed, blank or a single token. An AppHost's startup failure is relayed into
/// <c>~/.aspire/logs</c> and routinely pasted into an issue, so that one message is the only place
/// a password could travel.
/// <para>
/// <b>The rule is an allowlist, and the reason is that the alternative is never finished.</b> Naming
/// the keywords a secret is usually written under leaves every keyword nobody thought of printed in
/// full. Here the value of every pair is hidden unless its key is one of the few known to hold
/// nothing — see <see cref="ShapesByKey"/> — and even then only when the <em>whole</em> value
/// matches the shape that key is known to hold. A key nobody anticipated, or a value that does not
/// look like what its key should hold, reads as <c>***</c>, which is mildly annoying rather than
/// dangerous.
/// </para>
/// <para>
/// <b>The invariant, which any change here must be checked against:</b> nothing is printed unless it
/// has been positively recognised as safe to print — a key name; a value under an allowlisted key
/// that matches that key's shape in full; a URI's scheme and its authority after the userinfo; a
/// bare <c>host:port</c>; an empty value; and the separators and spacing between all of those. A
/// value's extent is no longer "text until something ends it" — there is no delimiter to get wrong,
/// no quote to honour, no escape to parse, because a value that fails to match end to end is masked
/// whole rather than partially trusted.
/// </para>
/// <para>
/// The boundary of "recognised" is still a tokenizer, so the residue is a pair written behind
/// punctuation no dialect separates pairs with: <c>Host=h|Custom=hunter2</c> is one value as far as
/// this can tell, and is printed if it happens to match <c>host</c>'s shape, masked otherwise.
/// Widening the separator set is not the answer — <c>:</c> and <c>/</c> carry
/// <c>Data Source=tcp:host,1433</c> and every URL — and neither is masking any value containing an
/// <c>=</c>, which would reduce an Oracle descriptor to nothing. <see cref="KnownCredentialKeywords"/>
/// covers the conventional names in that position; an unconventional one behind unconventional
/// punctuation is knowingly left.
/// </para>
/// </remarks>
internal static class ConnectionStringRedaction
{
    /// <summary>
    /// What is shown in place of a connection string that could not be scanned.
    /// </summary>
    /// <remarks>
    /// Named so the caller can tell it from a redacted value and drop the sentence explaining how
    /// values are shown — nothing here was shown, redacted or otherwise.
    /// </remarks>
    internal const string Unscannable =
        "<connection string omitted: it could not be scanned for credentials>";

    private const string Mask = "***";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// A run of ASCII digits, 1 to 5 of them — a port and nothing else.
    /// </summary>
    private static readonly Regex PortShape =
        new(@"\A[0-9]{1,5}\z", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// A hostname, an IPv4 literal, or a bracketed IPv6 literal, with an optional <c>tcp:</c> or
    /// <c>tcp://</c> network-library prefix in front and an optional <c>:port</c> or <c>,port</c>
    /// behind — <c>host</c>, <c>server</c> and <c>data source</c>'s shape.
    /// </summary>
    /// <remarks>
    /// No userinfo, no path, no query: a value carrying any of those is not addressing a host, it is
    /// carrying something this type cannot vouch for — a credential, a file path, an Oracle TNS
    /// descriptor — and is masked whole rather than partially trusted.
    /// <para>
    /// The permitted prefix is the literal word <c>tcp</c>, not any word: an earlier draft accepted
    /// <em>any</em> letter-led run in front of a colon as a "scheme", which let
    /// <c>Data Source=aB3xK9zQ2mR7pL4w:db.prod.internal</c> print whole — the alnum secret in front
    /// of the colon parsed as a bogus scheme rather than being rejected as part of no legitimate host.
    /// <c>tcp:</c> and <c>tcp://</c> are SQL Server's and the ODBC/ADO.NET net-library spellings of
    /// the same address, and are the only prefix this needs to admit for a value with nothing to hide
    /// to keep reading as one.
    /// </para>
    /// <para>
    /// The three letters are spelled out case by case, <c>[Tt][Cc][Pp]</c>, rather than matched with
    /// <see cref="RegexOptions.IgnoreCase"/>: that flag folds Unicode case-equivalents too, and under
    /// <see cref="RegexOptions.CultureInvariant"/> it still maps U+212A KELVIN SIGN to <c>k</c>,
    /// which would have widened every other letter class in this pattern — not just the three this
    /// needs — to accept it as well.
    /// </para>
    /// </remarks>
    private static readonly Regex HostShape = new(
        @"\A(?:[Tt][Cc][Pp]:(?://)?)?"
        + @"(?:\[[0-9A-Fa-f:]+\]|[A-Za-z0-9](?:[A-Za-z0-9._-])*)"
        + @"(?:[:,][0-9]{1,5})?\z",
        RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// A bare identifier — <c>database</c> and <c>initial catalog</c>'s shape.
    /// </summary>
    private static readonly Regex IdentifierShape =
        new(@"\A[A-Za-z0-9_][A-Za-z0-9_.$-]*\z", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// An identifier, optionally followed by <c>@</c> and another identifier — <c>user</c>'s shape.
    /// </summary>
    /// <remarks>
    /// The trailing <c>@identifier</c> is what keeps <c>UID=a@b.com</c> printing: SqlClient reads an
    /// email-shaped login name under this key, and it carries no secret of its own.
    /// </remarks>
    private static readonly Regex UserShape = new(
        @"\A[A-Za-z0-9_][A-Za-z0-9_.$-]*(?:@[A-Za-z0-9_][A-Za-z0-9_.$-]*)?\z",
        RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// An identifier, or an ODBC braced driver name — <c>driver</c> and <c>provider</c>'s shape.
    /// </summary>
    /// <remarks>
    /// The braced form has to admit spaces — <c>{ODBC Driver 18 for SQL Server}</c>,
    /// <c>{Microsoft Access Driver (*.mdb, *.accdb)}</c> — which is exactly what makes it the
    /// loosest of the five shapes. <c>[^{}]*</c> is too loose: it admits any character at all except
    /// a brace, so <c>Provider={hunter2 not a driver at all}</c> printed whole. The character set
    /// below still covers every driver name ODBC/OLEDB actually ship — letters, digits, space, and
    /// the punctuation real driver names use (<c>.</c> <c>,</c> <c>(</c> <c>)</c> <c>*</c> <c>_</c>
    /// <c>+</c> <c>-</c>) — while excluding every character a credential is actually written with:
    /// <c>=</c>, <c>@</c>, <c>:</c>, and the dialect's own separators. A driver name that happens to
    /// be made of nothing but words and spaces is the one shape this cannot rule out — the same
    /// residual every identifier-shaped key accepts — but that is a narrower target than "anything
    /// that is not a brace".
    /// </remarks>
    private static readonly Regex DriverShape = new(
        @"\A(?:\{[A-Za-z0-9 ,.()*_+-]*\}|[A-Za-z0-9_][A-Za-z0-9_.$-]*)\z",
        RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>
    /// The keys whose values are printed, and the shape each one is known to hold.
    /// </summary>
    /// <remarks>
    /// Deliberately short. Every addition is a fresh judgement that some key can never carry a
    /// secret, and an accumulation of such judgements is the thing this type exists to stop.
    /// <c>endpoint</c> is the instructive omission: an endpoint URL is exactly where an Azure shared
    /// access signature is written, so allowlisting it would fail open again.
    /// <para>
    /// Compared with <see cref="StringComparer.OrdinalIgnoreCase"/> rather than by lower-casing,
    /// because under <c>tr-TR</c> a culture-sensitive fold maps the <c>I</c> of <c>Initial
    /// Catalog</c> to a dotless <c>ı</c> and the lookup misses.
    /// </para>
    /// <para>
    /// <c>user id</c> and <c>userid</c> are here because <c>uid</c>, <c>user</c> and <c>username</c>
    /// are: they are SqlClient's spelling of the same concept, and without them <c>User ID=sa</c>
    /// would read as <c>***</c> while <c>UID=sa</c> printed.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, Regex> ShapesByKey = new(StringComparer.OrdinalIgnoreCase)
    {
        ["host"] = HostShape,
        ["server"] = HostShape,
        ["data source"] = HostShape,
        ["port"] = PortShape,
        ["database"] = IdentifierShape,
        ["initial catalog"] = IdentifierShape,
        ["user"] = UserShape,
        ["user id"] = UserShape,
        ["userid"] = UserShape,
        ["username"] = UserShape,
        ["uid"] = UserShape,
        ["driver"] = DriverShape,
        ["provider"] = DriverShape,
    };

    /// <summary>
    /// The keywords a secret is conventionally written under, wherever one appears.
    /// </summary>
    /// <remarks>
    /// A backstop, not the defence. It runs first and can only replace text with <c>***</c>, so it
    /// can only ever hide more — which is what makes it impossible for the shape check below to
    /// print something the keyword list caught. <see cref="Scan"/> can be surprised by a dialect
    /// nobody modelled, and one shape it is known to miss is a keyword behind a punctuation mark
    /// that introduces nothing: <c>Data Source=file:pwd=hunter2</c> hides its password inside an
    /// allowlisted value, where no separator marks it off. An unconditional <c>pwd=</c> finds it.
    /// <para>
    /// A quoted value is taken whole, because it owns the <c>;</c> inside it. Stopping at that
    /// <c>;</c> masked the first half of <c>Password='a;Host=hunter2'</c> and handed the second half
    /// to a scan that then read it as a host.
    /// </para>
    /// </remarks>
    private static readonly Regex KnownCredentialKeywords = new(
        @"(?<=(?:password|pwd|secret|token|accountkey|accesskey|apikey|signature)\s*=)"
        + @"(?:'[^']*'|""[^""]*""|[^;]+)",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant,
        RegexTimeout);

    /// <summary>
    /// Whether <paramref name="value"/> is worth passing through <see cref="Redact"/> at all.
    /// </summary>
    /// <remarks>
    /// The gate a caller outside this type needs before redacting a value it did not itself write as
    /// a connection string — the validator echoes every value it could not bind to a field's type,
    /// and most of those are not one: a path, a git ref, a hexadecimal port. <see cref="Redact"/>
    /// masks whole anything with no recognised shape, so running it unconditionally would replace
    /// those with a useless <c>***</c> for no credential ever at risk in them.
    /// <para>
    /// Asks the same two questions <see cref="Redact"/> itself asks, because a value it would
    /// change is exactly a value worth calling it for. A <c>key=value</c> pair is one of them, and
    /// is enough on its own — it is the shape every dialect this type covers is built from. But
    /// <see cref="FindPairs"/> only lets a pair begin after a separator this type recognises
    /// (<c>;</c>, <c>&amp;</c>, <c>?</c>, <c>,</c>, or the start of the string), so
    /// <c>db:Password=hunter2</c> — a colon in front, which is none of those — finds no pair at
    /// all even though it plainly carries one. <see cref="KnownCredentialKeywords"/> is what
    /// catches that: it is <see cref="Redact"/>'s own backstop, run without regard to separators,
    /// and asking it here as well as asking for a pair is what keeps this gate from ever answering
    /// "no" to a value <see cref="Redact"/> would answer "yes" to.
    /// </para>
    /// </remarks>
    public static bool LooksLikeConnectionString(string value) =>
        FindPairs(value, whitespaceBeginsAPair: false).Count > 0
        || MatchesKnownCredentialKeyword(value);

    /// <summary>
    /// Whether <see cref="KnownCredentialKeywords"/> finds a match, treating a timeout as a match
    /// rather than a miss.
    /// </summary>
    /// <remarks>
    /// A gate answering "no" is a promise that nothing here needs hiding, and a regex that gave up
    /// partway through has proven no such thing — the pathological input <see cref="Redact"/>
    /// itself falls back to <see cref="Unscannable"/> for is not evidence of safety, it is evidence
    /// the search never finished. Answering "yes" costs nothing here: it only sends the value on to
    /// <see cref="Redact"/>, which meets the same input with the same timeout and the same
    /// fail-closed answer.
    /// </remarks>
    private static bool MatchesKnownCredentialKeyword(string value)
    {
        try
        {
            return KnownCredentialKeywords.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>
    /// <paramref name="connectionString"/> with everything not recognised as safe to print replaced
    /// by <c>***</c>, or <see cref="Unscannable"/> if the search for credentials did not finish.
    /// </summary>
    public static string Redact(string connectionString)
    {
        try
        {
            var backstopped = KnownCredentialKeywords.Replace(connectionString, Mask);

            return Scan(backstopped);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological value is not a reason to fail differently than the developer expects,
            // and it is emphatically not a reason to print the thing this method exists to hide.
            return Unscannable;
        }
    }

    /// <summary>
    /// Where one <c>key=value</c> pair sits in the string being scanned.
    /// </summary>
    /// <param name="KeyStart">Where the key's first character is.</param>
    /// <param name="KeyEnd">Where the key's text ends, before any space in front of the <c>=</c>.</param>
    /// <param name="ValueStart">Where the value begins, just after the <c>=</c>.</param>
    private readonly record struct Pair(int KeyStart, int KeyEnd, int ValueStart);

    /// <summary>
    /// <paramref name="text"/> rebuilt with every unrecognised value replaced.
    /// </summary>
    /// <remarks>
    /// Keys, the spacing around each <c>=</c> and the runs of separators between pairs are copied
    /// across verbatim, so a string with nothing to hide comes back byte-identical. The caller
    /// decides whether to explain the masking by comparing this with what it passed in, and any
    /// normalisation here would make that comparison lie.
    /// </remarks>
    private static string Scan(string text)
        => text.Length == 0
            ? text
            : Rebuild(text, FindPairs(text, whitespaceBeginsAPair: false), RedactPrefix, RedactValue);

    /// <summary>
    /// What is printed for <paramref name="value"/> under <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// An empty value never reaches here: <see cref="Rebuild"/> leaves one exactly as it found it,
    /// because an empty string cannot be a secret and because it is the entire diagnosis in the case
    /// this message exists for — a shell that ate a <c>${port}</c> leaves the key behind with
    /// nothing in it, and masking that would assert something was hidden where nothing was.
    /// </remarks>
    private static string RedactValue(string key, string value)
        => ShapesByKey.TryGetValue(key, out var shape) ? RedactShapedValue(shape, value) : MaskKeepingLeadingSpace(value);

    /// <summary>
    /// <paramref name="value"/> under a key whose shape is <paramref name="shape"/>, printed whole
    /// when it matches and masked whole when it does not.
    /// </summary>
    /// <remarks>
    /// libpq's conninfo writes its pairs separated by spaces — <c>host=h port=5432
    /// password=hunter2</c> — so a value that is printed because its key is allowlisted can carry
    /// several more pairs, one of which is the password. A space begins a pair <em>here</em>, inside
    /// a value already recognised as safe to print. Letting it do so one level up would print the
    /// <c>def</c> in <c>Rotation Key=abc user=def</c>, which is not a username but the tail of a
    /// value nothing recognised.
    /// <para>
    /// One level deep, and no deeper: this is the last point at which anything is printed, so there
    /// is no recursion to bound. The head — what precedes the first nested pair — is checked against
    /// the outer key's own shape, because whatever it could not vouch for taints the pairs found
    /// after it: in <c>host=db.internal;hunter2 port=5432</c> read one level down, the head is not a
    /// clean hostname, so nothing here is trusted enough to print.
    /// </para>
    /// </remarks>
    private static string RedactShapedValue(Regex shape, string value)
    {
        var pairs = FindPairs(value, whitespaceBeginsAPair: true);

        if (pairs.Count == 0)
        {
            return MatchesWhole(shape, value) ? value : MaskKeepingLeadingSpace(value);
        }

        var head = value[..pairs[0].KeyStart];

        return MatchesWhole(shape, head)
            ? Rebuild(value, pairs, _ => head, RedactNestedValue)
            : MaskKeepingLeadingSpace(value);
    }

    /// <summary>
    /// What is printed for a pair written inside a value that was already recognised.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="RedactValue"/> one level down, and the last point at which anything
    /// is printed: a recognised value here is compared against its own key's shape rather than being
    /// scanned again, so there is no third level and no cycle to bound.
    /// </remarks>
    private static string RedactNestedValue(string key, string value)
        => ShapesByKey.TryGetValue(key, out var shape) && MatchesWhole(shape, value)
            ? value
            : MaskKeepingLeadingSpace(value);

    /// <summary>
    /// Whether <paramref name="candidate"/>, once its leading layout whitespace is set aside,
    /// matches <paramref name="shape"/> from end to end.
    /// </summary>
    /// <remarks>
    /// Leading space is layout the developer wrote around the <c>=</c>, not part of the value — see
    /// <see cref="MaskKeepingLeadingSpace"/>, which is what puts it back regardless of the answer
    /// here. Nothing trailing is ever present: <see cref="Rebuild"/> has already trimmed the
    /// separators, whitespace among them, off the end before a value reaches this.
    /// </remarks>
    private static bool MatchesWhole(Regex shape, string candidate)
    {
        // Layout at either edge is not content: a head ends wherever the next nested key begins,
        // which is always right after the whitespace that introduced it — 'x ' in
        // 'Host=x Custom Port=5432' is not a hostname called 'x ', it is the hostname 'x' plus the
        // space that comes next.
        var (_, rest) = SplitLeadingLayout(candidate);
        var trimmed = TrimTrailingLayout(rest);

        return trimmed.Length == 0 || shape.IsMatch(trimmed);
    }

    /// <summary>
    /// <paramref name="value"/> split into a leading run of dialect punctuation and whatever follows
    /// it.
    /// </summary>
    /// <remarks>
    /// Not only whitespace: <c>Port=;2fa=hunter2</c> leaves <c>port</c> with a value beginning at the
    /// <c>;</c> rather than at a space, because nothing but that <c>;</c> stood between the <c>=</c>
    /// and the next key. It is still layout, not content — <c>port</c> holds nothing here, and the
    /// <c>;</c> is what a shell-eaten <c>${port}</c> leaves behind.
    /// </remarks>
    private static (string Leading, string Content) SplitLeadingLayout(string value)
    {
        var leading = 0;

        while (leading < value.Length && IsSeparator(value[leading]))
        {
            leading++;
        }

        return (value[..leading], value[leading..]);
    }

    /// <summary>
    /// <paramref name="text"/> with any trailing run of dialect punctuation removed.
    /// </summary>
    private static string TrimTrailingLayout(string text)
    {
        var end = text.Length;

        while (end > 0 && IsSeparator(text[end - 1]))
        {
            end--;
        }

        return text[..end];
    }

    /// <summary>
    /// <paramref name="text"/> with each of <paramref name="pairs"/> put through
    /// <paramref name="redactValue"/>, the text before the first through
    /// <paramref name="redactHead"/>, and everything else copied verbatim.
    /// </summary>
    /// <remarks>
    /// The head is what precedes the first key. At the top of the string that is an unvetted prefix
    /// and is treated as one; inside a value it is the part of a value already recognised, such as
    /// the <c>db.internal</c> in <c>host=db.internal port=5432</c>.
    /// </remarks>
    private static string Rebuild(
        string text,
        List<Pair> pairs,
        Func<string, string> redactHead,
        Func<string, string, string> redactValue)
    {
        if (pairs.Count == 0)
        {
            return redactHead(text);
        }

        var built = new StringBuilder(text.Length);

        built.Append(redactHead(text[..pairs[0].KeyStart]));

        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            var boundary = i + 1 < pairs.Count ? pairs[i + 1].KeyStart : text.Length;
            var valueEnd = boundary;

            while (valueEnd > pair.ValueStart && IsSeparator(text[valueEnd - 1]))
            {
                valueEnd--;
            }

            // Space in front of a value goes on with it rather than being trimmed off here. It is
            // the only thing that would introduce a pair one level down, and stripping it hid
            // 'port= 2fa=hunter2' from the scan that was supposed to read it.
            var value = text[pair.ValueStart..valueEnd];

            // Key, any space before the '=', and the '=' itself, exactly as written.
            built.Append(text, pair.KeyStart, pair.ValueStart - pair.KeyStart);

            // Trailing separators, whitespace among them, are already off the end of the region, so
            // a value that is not empty here ends in a character that is part of it.
            built.Append(value.Length == 0
                ? value
                : redactValue(text[pair.KeyStart..pair.KeyEnd], value));

            built.Append(text, valueEnd, boundary - valueEnd);
        }

        return built.ToString();
    }

    /// <summary>
    /// Every <c>key=</c> in <paramref name="text"/> that something could have introduced, in the
    /// order they appear.
    /// </summary>
    /// <remarks>
    /// A key is recognised only where a pair could begin, so the <c>host</c> in
    /// <c>Password=myhost=x</c> is not mistaken for a key of its own. What may begin one differs by
    /// level: at the top of the string one of <c>;</c> <c>&amp;</c> <c>?</c> <c>,</c> must have come
    /// first, optionally followed by space, while inside a value already recognised as safe to print
    /// space alone will do — see <see cref="RedactShapedValue"/>.
    /// <para>
    /// A doubled <c>=</c> is skipped rather than accepted: <c>Host==x=hunter2</c> is ADO.NET's
    /// escape for the key <c>host=x</c>, so reading <c>Host</c> as the key would find an allowlisted
    /// name and print the password behind it. Skipped, it is recognised as nothing and shown as
    /// nothing.
    /// </para>
    /// </remarks>
    private static List<Pair> FindPairs(string text, bool whitespaceBeginsAPair)
    {
        var pairs = new List<Pair>();

        // The last character that was not whitespace, so that "a separator, then any amount of
        // space" is answered in constant time however long that space runs.
        var lastMeaningful = '\0';

        for (var i = 0; i < text.Length; i++)
        {
            var mayBegin = whitespaceBeginsAPair
                ? i > 0 && char.IsWhiteSpace(text[i - 1])
                : lastMeaningful is '\0' or ';' or '&' or '?' or ',';

            var scannedTo = i;

            if (mayBegin && TryReadPair(text, i, out var pair, out scannedTo))
            {
                pairs.Add(pair);

                // A pair's head always ends at its '=', which introduces nothing — the value is then
                // scanned as ordinary text, and that is what stops a pair beginning inside it.
                lastMeaningful = '=';

                i = EndOfValue(text, pair.ValueStart) - 1;

                continue;
            }

            if (scannedTo > i)
            {
                // Nothing can begin inside what that scan already read: a key starting later in the
                // same run ends at the same place and fails the same way. Retrying at every position
                // in it is what made a long run of words quadratic.
                lastMeaningful = text[scannedTo - 1];
                i = scannedTo - 1;

                continue;
            }

            if (!char.IsWhiteSpace(text[i]))
            {
                lastMeaningful = text[i];
            }
        }

        return pairs;
    }

    /// <summary>
    /// Where the value beginning at <paramref name="valueStart"/> ends for the purpose of scanning
    /// on — past a quoted value, or past the token an empty value takes for its own.
    /// </summary>
    /// <remarks>
    /// Two things a scan must not walk into. A quoted value owns the separators inside it, so
    /// <c>Password='a;Host=hunter2'</c> is one value and not a password followed by a host — reading
    /// it the other way printed the tail. And <c>user = dev</c> is how libpq writes a pair, so the
    /// token after an empty value belongs to it; read as a key of its own it would swallow the
    /// pair after it, and <c>user= dev database=orders</c> would hide the database under a key
    /// called <c>dev database</c>.
    /// </remarks>
    private static int EndOfValue(string text, int valueStart)
    {
        var i = valueStart;

        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i < text.Length && text[i] is '"' or '\'')
        {
            var close = text.IndexOf(text[i], i + 1);

            // An unterminated quote closes nothing, so it cannot be allowed to claim the rest of the
            // string: doing so stopped every later pair from being found, and one stray apostrophe
            // turned off redaction for everything after it.
            return close < 0 ? valueStart : close + 1;
        }

        // Only where the '=' was followed by space: an empty value takes the next token, and that
        // token is a value rather than a key only if it carries no '=' of its own.
        if (i == valueStart || i >= text.Length)
        {
            return valueStart;
        }

        var token = i;

        while (token < text.Length && !char.IsWhiteSpace(text[token]) && !IsSeparator(text[token]))
        {
            if (text[token] == '=')
            {
                return valueStart;
            }

            token++;
        }

        return token;
    }

    /// <summary>
    /// Whether a pair begins at <paramref name="start"/>, and where its parts are.
    /// </summary>
    private static bool TryReadPair(string text, int start, out Pair pair, out int scannedTo)
    {
        pair = default;
        scannedTo = start;

        var keyEnd = KeyEndAt(text, start);

        if (keyEnd < 0)
        {
            return false;
        }

        scannedTo = keyEnd;

        // Whitespace is allowed on either side of the '=' — every keyword dialect trims it — but it
        // is not part of the key.
        var equals = keyEnd;

        while (equals < text.Length && char.IsWhiteSpace(text[equals]))
        {
            equals++;
        }

        if (equals >= text.Length || text[equals] != '=')
        {
            return false;
        }

        if (equals + 1 < text.Length && text[equals + 1] == '=')
        {
            return false;
        }

        pair = new Pair(start, keyEnd, equals + 1);

        return true;
    }

    /// <summary>
    /// Where the key starting at <paramref name="start"/> ends, or <c>-1</c> if none starts there.
    /// </summary>
    /// <remarks>
    /// Interior whitespace is part of a key, because several dialects write it — <c>Data Source</c>,
    /// <c>Initial Catalog</c>, <c>User ID</c>. It is only taken when a key character follows it, so
    /// space in front of the <c>=</c> belongs to the layout instead. A run of it counts the same as
    /// one space: were it not to, <c>Custom  Port=x</c> would be read as the allowlisted <c>Port</c>
    /// and print its value, while <c>Custom Port=x</c> did not.
    /// <para>
    /// The longest key wins, and that is the fail-closed reading rather than a tidiness preference.
    /// In <c>Host=x Custom Port=5432</c> the short read finds the allowlisted <c>Port</c> and prints
    /// <c>5432</c>; the long one finds <c>Custom Port</c>, which is nothing this recognises.
    /// </para>
    /// </remarks>
    private static int KeyEndAt(string text, int start)
    {
        if (!char.IsAsciiLetter(text[start]) && text[start] != '_')
        {
            return -1;
        }

        var end = start + 1;

        while (end < text.Length)
        {
            var c = text[end];

            if (char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')
            {
                end++;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                var afterSpace = end;

                while (afterSpace < text.Length && char.IsWhiteSpace(text[afterSpace]))
                {
                    afterSpace++;
                }

                if (afterSpace < text.Length
                    && (char.IsAsciiLetterOrDigit(text[afterSpace]) || text[afterSpace] == '_'))
                {
                    end = afterSpace;
                    continue;
                }
            }

            break;
        }

        return end;
    }

    /// <summary>
    /// <c>***</c>, with any layout in front of the value left where the developer wrote it.
    /// </summary>
    /// <remarks>
    /// So <c>Rotation Key = hunter2</c> reads back as <c>Rotation Key = ***</c> rather than losing
    /// the layout around the value it hid. Both levels end here rather than at each other, which is
    /// what keeps the scan two levels deep and free of a cycle to bound.
    /// <para>
    /// Leading punctuation is kept too, not only whitespace: <c>Port=;2fa=hunter2</c> leaves
    /// <c>port</c> with nothing of its own before the <c>;</c>, and printing <c>Port=;***</c> rather
    /// than <c>Port=***</c> is what keeps that emptiness visible — it is the diagnosis the message
    /// exists to deliver when a shell has eaten a <c>${port}</c>.
    /// </para>
    /// </remarks>
    private static string MaskKeepingLeadingSpace(string value)
    {
        var (leading, _) = SplitLeadingLayout(value);

        return leading + Mask;
    }

    /// <summary>
    /// <paramref name="value"/> with any <c>user:pass@host</c> authority in it reduced to its host.
    /// </summary>
    /// <remarks>
    /// Used only for the bare-URI prefix — a connection string with no <c>key=</c> in it at all — so
    /// this is the one place authority masking still applies. A key=value pair under an allowlisted
    /// key is judged by <see cref="ShapesByKey"/> instead, and a value carrying an authority does not
    /// match any of those shapes, so it is masked whole rather than reduced to its host.
    /// <para>
    /// The <em>last</em> <c>@</c> is taken, and nothing stops the search at <c>/</c>, <c>?</c> or
    /// <c>#</c>: all three are legal unencoded in a password people actually write, and a rule that
    /// stopped at them printed the password whole.
    /// </para>
    /// </remarks>
    private static string MaskAuthority(string value)
    {
        var at = value.LastIndexOf('@');

        if (at <= 0)
        {
            return value;
        }

        // Something has to separate the name from the secret for this to be an authority rather than
        // an address: ':' in every URL, '/' in Oracle's 'scott/tiger@//host:1521/svc'. Requiring one
        // is what leaves 'UID=a@b.com' alone.
        if (value.LastIndexOf(':', at - 1) < 0 && value.LastIndexOf('/', at - 1) < 0)
        {
            return value;
        }

        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        var keep = scheme >= 0 && scheme + 3 <= at ? scheme + 3 : 0;

        return string.Concat(value.AsSpan(0, keep), Mask, value.AsSpan(at));
    }

    /// <summary>
    /// What is printed for the text before the first pair.
    /// </summary>
    /// <remarks>
    /// A connection string that is a bare URI has no <c>key=</c> in it at all, so all of it arrives
    /// here. So does the <c>host:port</c> that Redis and Kafka address a tunnel with, which is
    /// recognised by shape — without it the message would answer a Redis developer with <c>***</c>
    /// and nothing else. Anything else is unrecognised, and is not printed.
    /// </remarks>
    private static string RedactPrefix(string prefix)
    {
        var coreEnd = prefix.Length;

        while (coreEnd > 0 && IsSeparator(prefix[coreEnd - 1]))
        {
            coreEnd--;
        }

        if (coreEnd == 0)
        {
            return prefix;
        }

        var core = prefix[..coreEnd];
        var trailing = prefix[coreEnd..];

        if (BeginsWithAScheme(core))
        {
            return MaskUri(core) + trailing;
        }

        return IsHostAndPort(core) ? prefix : Mask + trailing;
    }

    /// <summary>
    /// <paramref name="uri"/> with its userinfo masked and any unrecognised query text dropped.
    /// </summary>
    /// <remarks>
    /// Whatever follows the first separator and was not recognised as a pair was vetted by nothing,
    /// so it is replaced rather than printed — every mark this type calls a separator, and <c>#</c>
    /// besides. Cutting at only some of them left the rest printing: a fragment leaked
    /// <c>redis://h:6379/0#sig2=hunter2</c>, and a <c>;</c> or <c>&amp;</c> leaked
    /// <c>redis://cache:6379/0&amp;2fa=hunter2</c>, where the key is invisible to the scan because it
    /// begins with a digit.
    /// </remarks>
    private static string MaskUri(string uri)
    {
        var masked = MaskAuthority(uri);
        var scheme = masked.IndexOf("://", StringComparison.Ordinal) + 3;
        var authorityEnd = scheme;

        while (authorityEnd < masked.Length && masked[authorityEnd] is not ('/' or '?' or '#'))
        {
            authorityEnd++;
        }

        // A comma inside the authority lists hosts — 'mongodb://h1:27017,h2:27017/db' is how a
        // replica set is addressed — and a host there is recognised by its shape. Past the
        // authority a comma separates nothing this knows about, so it ends the vetted part.
        var built = new StringBuilder(masked[..scheme]);
        var hosts = masked[scheme..authorityEnd].Split(',');

        for (var i = 0; i < hosts.Length; i++)
        {
            if (i > 0)
            {
                built.Append(',');
            }

            built.Append(i == 0 || IsHostAndPort(hosts[i]) ? hosts[i] : Mask);
        }

        var hostsEnd = built.Length;

        masked = built.Append(masked, authorityEnd, masked.Length - authorityEnd).ToString();

        var unvetted = -1;

        for (var i = scheme; i < masked.Length; i++)
        {
            var c = masked[i];

            // A path holds no pairs, so a '=' past the authority was separated from it by nothing
            // and vetted by nothing — 'postgres://u:p@h/db=hunter2' printed on the strength of the
            // host in front of it.
            if (c == '=' && i >= hostsEnd)
            {
                unvetted = i;

                break;
            }

            if ((IsSeparator(c) || c == '#') && !(c == ',' && i < hostsEnd))
            {
                unvetted = i;

                break;
            }
        }

        return unvetted < 0 || unvetted == masked.Length - 1
            ? masked
            : string.Concat(masked.AsSpan(0, unvetted + 1), Mask);
    }

    /// <summary>
    /// Whether <paramref name="text"/> opens with a scheme, so that what follows is a URI.
    /// </summary>
    /// <remarks>
    /// Merely containing <c>://</c> is not enough, or any text with a URL somewhere in it is printed
    /// up to its first separator on the strength of the URL — <c>s3cr3t redis://db:6379</c> showed
    /// the token in front. A scheme is one unbroken run, which <c>jdbc:postgresql://</c> is and
    /// <c>s3cr3t redis://</c> is not.
    /// </remarks>
    private static bool BeginsWithAScheme(string text)
    {
        var scheme = text.IndexOf("://", StringComparison.Ordinal);

        if (scheme <= 0)
        {
            return false;
        }

        foreach (var c in text.AsSpan(0, scheme))
        {
            if (IsSeparator(c) || c is '#' or '@')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is a host and a port and nothing else.
    /// </summary>
    /// <remarks>
    /// Requiring the port is what keeps this safe: a bare token would let an API key through on the
    /// grounds that it is shaped like a hostname.
    /// </remarks>
    private static bool IsHostAndPort(string text)
    {
        var colon = text.LastIndexOf(':');

        if (colon <= 0 || colon == text.Length - 1)
        {
            return false;
        }

        var port = text.AsSpan(colon + 1);

        if (port.Length > 5)
        {
            return false;
        }

        foreach (var c in port)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        var host = text.AsSpan(0, colon);

        if (host[0] == '[' && host[^1] == ']')
        {
            foreach (var c in host[1..^1])
            {
                if (!char.IsAsciiHexDigit(c) && c != ':')
                {
                    return false;
                }
            }

            return host.Length > 2;
        }

        foreach (var c in host)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="c"/> is something a dialect uses to introduce the next pair.
    /// </summary>
    private static bool IsSeparator(char c) => c is ';' or '&' or '?' or ',' || char.IsWhiteSpace(c);
}
