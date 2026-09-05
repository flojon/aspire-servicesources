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
/// full; that list was corrected three times in as many reviews, each time by someone finding a
/// shape it missed. Here the value of every pair is hidden unless its key is one of the few known
/// to hold nothing — see <see cref="KeysThatHoldNoSecret"/>. A key nobody anticipated reads as
/// <c>***</c>, which is mildly annoying rather than dangerous.
/// </para>
/// <para>
/// <b>The invariant, which any change here must be checked against:</b> nothing is printed unless it
/// has been positively recognised as safe to print — a key name; a value under an allowlisted key;
/// a URI's scheme, its authority after the userinfo, and its path; a bare <c>host:port</c>; an empty
/// value; and the separators and spacing between all of those.
/// </para>
/// <para>
/// The boundary of "recognised" is a tokenizer, so the residue is a pair written behind punctuation
/// no dialect separates pairs with: <c>Host=h|Custom=hunter2</c> is one value as far as this can
/// tell, and is printed. Widening the separator set is not the answer — <c>:</c> and <c>/</c> carry
/// <c>Data Source=tcp:host,1433</c> and every URL — and neither is masking any value containing an
/// <c>=</c>, which would reduce an Oracle descriptor to nothing. <see cref="KnownCredentialKeywords"/>
/// covers the conventional names in that position; an unconventional one behind unconventional
/// punctuation is knowingly left.
/// </para>
/// <para>
/// Redaction by key is only fail-closed if the scan that finds the keys is at least as permissive as
/// every syntax that could have written the string, so the scan looks for keys rather than for
/// separators. A pair may be introduced by <c>;</c>, <c>&amp;</c>, <c>?</c> or <c>,</c> — and, inside
/// a value whose key is allowlisted, by whitespace alone (see <see cref="RedactRecognisedValue"/>).
/// Space may sit on either side of the <c>=</c>, and a value runs to the next key rather than to the
/// next separator — which is also why a quoted value carrying a separator, <c>Password='a;b'</c>,
/// costs nothing to handle.
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

    /// <summary>
    /// The keys whose values are printed.
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
    private static readonly HashSet<string> KeysThatHoldNoSecret = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "server", "data source", "port", "database", "initial catalog",
        "user", "user id", "userid", "username", "uid", "driver", "provider",
    };

    /// <summary>
    /// The keywords a secret is conventionally written under, wherever one appears.
    /// </summary>
    /// <remarks>
    /// A backstop, not the defence. It runs first and can only replace text with <c>***</c>, so it
    /// can only ever hide more — which is what makes it impossible for the allowlist to print
    /// something the keyword list caught. <see cref="Scan"/> can be surprised by a dialect nobody
    /// modelled, and one shape it is known to miss is a keyword behind a punctuation mark that
    /// introduces nothing: <c>Data Source=file:pwd=hunter2</c> hides its password inside an
    /// allowlisted value, where no separator marks it off. An unconditional <c>pwd=</c> finds it.
    /// <para>
    /// Keywords only. A URI's <c>user:pass@host</c> is not matched here — <see cref="MaskUri"/> and
    /// <see cref="MaskAuthority"/> cover every shape of it, including a password carrying <c>/</c>,
    /// <c>?</c> or <c>#</c> raw, which no lookbehind anchored on <c>://</c> can bound.
    /// <para>
    /// A quoted value is taken whole, because it owns the <c>;</c> inside it. Stopping at that
    /// <c>;</c> masked the first half of <c>Password='a;Host=hunter2'</c> and handed the second half
    /// to a scan that then read it as a host.
    /// </para>
    /// </para>
    /// </remarks>
    private static readonly Regex KnownCredentialKeywords = new(
        @"(?<=(?:password|pwd|secret|token|accountkey|accesskey|apikey|signature)\s*=)"
        + @"(?:'[^']*'|""[^""]*""|[^;]+)",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// <paramref name="connectionString"/> with everything not recognised as safe to print replaced
    /// by <c>***</c>, or <see cref="Unscannable"/> if the search for credentials did not finish.
    /// </summary>
    public static string Redact(string connectionString)
    {
        string backstopped;

        try
        {
            backstopped = KnownCredentialKeywords.Replace(connectionString, Mask);
        }
        catch (RegexMatchTimeoutException)
        {
            // A pathological value is not a reason to fail differently than the developer expects,
            // and it is emphatically not a reason to print the thing this method exists to hide.
            return Unscannable;
        }

        return Scan(backstopped);
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
    /// <paramref name="value"/> under a key the allowlist names, with anything written inside it
    /// that is a pair of its own held to the same rule.
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
    /// is no recursion to bound.
    /// </para>
    /// </remarks>
    private static string RedactRecognisedValue(string value)
        => Rebuild(value, FindPairs(value, whitespaceBeginsAPair: true), RecognisedText, RedactNestedValue);

    /// <summary>
    /// What is printed for a pair written inside a value that was already recognised.
    /// </summary>
    /// <remarks>
    /// The mirror of <see cref="RedactValue"/> one level down, and the last point at which anything
    /// is printed: a recognised value here goes to <see cref="RecognisedText"/> rather than being
    /// scanned again, so there is no third level and no recursion to bound.
    /// </remarks>
    private static string RedactNestedValue(string key, string value)
        => KeysThatHoldNoSecret.Contains(key) ? RecognisedText(value) : Mask;

    /// <summary>
    /// The part of <paramref name="text"/> that belongs to the value it was found in, with anything
    /// beyond it replaced.
    /// </summary>
    /// <remarks>
    /// A value ends where a dialect could have ended it. Whatever follows the first separator was
    /// not read as a pair — otherwise the scan would have taken it — so it was vetted by nothing and
    /// is not printed. That is the difference between hiding a value and hiding a key nobody
    /// anticipated: <c>Host=db.internal;2fa=hunter2</c> has no key the scan can see, because a key
    /// does not begin with a digit, and printing the value whole printed the password with it.
    /// <para>
    /// A quoted value owns the separators inside it, so <c>Data Source="C:\a;b\x.mdb"</c> is one
    /// value and comes back whole.
    /// </para>
    /// </remarks>
    private static string RecognisedText(string text)
    {
        var end = 0;

        if (text.Length > 0 && text[0] is '"' or '\'')
        {
            var close = text.IndexOf(text[0], 1);

            end = close < 0 ? text.Length : close + 1;
        }

        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not (';' or '&' or '?' or '#'))
        {
            end++;
        }

        var printed = MaskAuthorityAndTrailingFields(text[..end]);

        if (end == text.Length)
        {
            return printed;
        }

        var afterSeparators = end;

        while (afterSeparators < text.Length
               && (char.IsWhiteSpace(text[afterSeparators]) || IsSeparator(text[afterSeparators])))
        {
            afterSeparators++;
        }

        return afterSeparators == text.Length
            ? printed + text[end..]
            : printed + text[end..afterSeparators] + Mask;
    }

    /// <summary>
    /// <paramref name="value"/> with its authority masked and any comma-separated field after the
    /// first shown only where it is a port.
    /// </summary>
    /// <remarks>
    /// A comma inside a value is how SQL Server writes a port — <c>Server=localhost,1433</c> — and
    /// that is the only thing after one this can vouch for. Anything else there is a field the scan
    /// never looked at, which is where <c>Host=h,hunter2</c> hid a password.
    /// </remarks>
    private static string MaskAuthorityAndTrailingFields(string value)
    {
        var comma = value.IndexOf(',');

        if (comma < 0)
        {
            return MaskAuthority(value);
        }

        var built = new StringBuilder(MaskAuthority(value[..comma]));

        foreach (var field in value[comma..].Split(','))
        {
            if (field.Length == 0)
            {
                continue;
            }

            built.Append(',').Append(IsPort(field) ? field : Mask);
        }

        return built.ToString();
    }

    /// <summary>
    /// Whether <paramref name="field"/> is a port number and nothing else.
    /// </summary>
    private static bool IsPort(ReadOnlySpan<char> field)
    {
        if (field.Length is 0 or > 5)
        {
            return false;
        }

        foreach (var c in field)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
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

            var valueStart = pair.ValueStart;

            // Space in front of a value belongs to the layout the developer wrote, not to the value:
            // 'Port = 5432' must come back with its spacing whatever happens to the 5432.
            while (valueStart < valueEnd && char.IsWhiteSpace(text[valueStart]))
            {
                valueStart++;
            }

            var value = text[valueStart..valueEnd];

            // Key, any space around the '=', and the '=' itself, exactly as written.
            built.Append(text, pair.KeyStart, valueStart - pair.KeyStart);

            if (value.Length > 0)
            {
                built.Append(redactValue(text[pair.KeyStart..pair.KeyEnd], value));
            }

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
    /// space alone will do — see <see cref="RedactRecognisedValue"/>.
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

            return close < 0 ? text.Length : close + 1;
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
    /// What is printed for <paramref name="value"/> under <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// An empty value never reaches here: <see cref="Rebuild"/> leaves one exactly as it found it,
    /// because an empty string cannot be a secret and because it is the entire diagnosis in the case
    /// this message exists for — a shell that ate a <c>${port}</c> leaves the key behind with
    /// nothing in it, and masking that would assert something was hidden where nothing was.
    /// </remarks>
    private static string RedactValue(string key, string value)
    {
        return KeysThatHoldNoSecret.Contains(key) ? RedactRecognisedValue(value) : Mask;
    }

    /// <summary>
    /// <paramref name="value"/> with any <c>user:pass@host</c> authority in it reduced to its host.
    /// </summary>
    /// <remarks>
    /// An allowlisted key can still hold a URL — <c>Data Source=postgresql://app:pw@db:5432</c> — and
    /// the scheme is optional, since <c>Data Source=user:pw@h:1433</c> says the same thing without
    /// one. A <c>:</c> must appear before the <c>@</c> for this to be an authority at all, which is
    /// what leaves <c>UID=a@b.com</c> alone.
    /// <para>
    /// The <em>last</em> <c>@</c> is taken, and nothing stops the search at <c>/</c>, <c>?</c> or
    /// <c>#</c>: all three are legal unencoded in a password people actually write, and a rule that
    /// stopped at them printed the password whole.
    /// </para>
    /// </remarks>
    private static string MaskAuthority(string value)
    {
        var at = value.LastIndexOf('@');

        if (at <= 0 || value.LastIndexOf(':', at - 1) < 0)
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

        if (core.Contains("://", StringComparison.Ordinal))
        {
            return MaskUri(core) + trailing;
        }

        return IsHostAndPort(core) ? prefix : Mask + trailing;
    }

    /// <summary>
    /// <paramref name="uri"/> with its userinfo masked and any unrecognised query text dropped.
    /// </summary>
    /// <remarks>
    /// Whatever sits after a <c>?</c> or a <c>#</c> and was not recognised as a pair was vetted by
    /// nothing, so it is replaced rather than printed. A fragment is no more vetted than a query:
    /// leaving it alone printed <c>redis://h:6379/0#sig2=hunter2</c> whole.
    /// </remarks>
    private static string MaskUri(string uri)
    {
        var masked = MaskAuthority(uri);
        var unvetted = masked.AsSpan().IndexOfAny('?', '#');

        return unvetted < 0 || unvetted == masked.Length - 1
            ? masked
            : string.Concat(masked.AsSpan(0, unvetted + 1), Mask);
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
