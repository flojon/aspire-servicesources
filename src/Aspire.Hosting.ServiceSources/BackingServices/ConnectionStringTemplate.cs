using System.Text;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources.BackingServices;

/// <summary>
/// A backing service's configured <c>connectionString</c>, split into the literal text it is mostly
/// made of and the placeholders whose values are not known when it is written.
/// </summary>
/// <remarks>
/// Parsed when <c>AddBackingService</c> is called, so a malformed placeholder is a startup failure
/// naming the backing service rather than a connection string that reaches the app with
/// <c>{secret:orders-creds}</c> still in it. The <i>values</i> are a separate question: a port is
/// known synchronously and substituted as a literal, while a secret is fetched at resolution time,
/// so what this produces is the structure and not the string.
/// <para>
/// A brace that does not open a placeholder is literal text, deliberately: <c>Driver={PostgreSQL}</c>
/// and <c>Server={host}\instance</c> are ordinary ODBC connection strings, and a parser that
/// claimed every <c>{…}</c> would reject them. A <c>{</c> begins a placeholder only when the word
/// after it — up to the first <c>:</c> or <c>}</c>, or to the end — is <em>exactly</em> a keyword
/// this package defines, and then it is read strictly, because at that point a developer plainly
/// meant one. Equality rather than a prefix, so <c>{portal}</c>, <c>{secretariat}</c> and
/// <c>{secrets:a}</c> are all text.
/// </para>
/// <para>
/// <b>There is no escape, and a doubled brace is not one.</b> That reserves one shape rather than
/// two spellings: <c>{port}</c>, <c>{PORT}</c>, <c>{port:amqp}</c>, <c>{secret}</c> and
/// <c>{secret:a}</c> alike cannot appear as literal text, since the keyword is matched
/// case-insensitively and a keyword-shaped token that cannot be read fails rather than passing
/// through. That is a real limitation, and the errors that hit it say so rather than sending the
/// reader to look for a spelling that does not exist.
/// </para>
/// <para>
/// The obvious escape — doubling the brace, as every format string does — was tried and withdrawn,
/// because a connection string already uses braces <i>with its own doubling rule</i> and the two
/// collide in both directions. ODBC quotes a value in braces and doubles an embedded <c>}</c>, so
/// <c>PWD={pa}}ss}</c> is the password <c>pa}ss</c>: collapsing that <c>}}</c> yields
/// <c>PWD={pa}ss}</c>, which the driver reads as ending at the brace, and the app connects with
/// <c>pa</c> and trailing rubbish. It does not require doubling <c>{</c>, so <c>PWD={{abc}</c> is
/// the password <c>{abc</c>, and collapsing that <c>{{</c> silently drops a character from it.
/// Either way a working connection string is rewritten and nothing says so — much worse than
/// being unable to write a literal <c>{port}</c>, which nothing has yet wanted.
/// </para>
/// <para>
/// Scoping the escape to the parts of the string outside a brace-quoted value would fix it and is
/// not on offer: it means knowing each backend's grammar, which is the thing the placeholder design
/// exists to avoid. A syntax braces do not use — a backslash, say — would work, and can be added
/// the day something needs it.
/// </para>
/// </remarks>
internal sealed class ConnectionStringTemplate
{
    /// <summary>The placeholder keyword for a locally-forwarded port.</summary>
    private const string PortKeyword = "port";

    /// <summary>The placeholder keyword for a value read out of a Kubernetes secret.</summary>
    private const string SecretKeyword = "secret";

    private ConnectionStringTemplate(IReadOnlyList<Segment> segments) => Segments = segments;

    /// <summary>The template in order, as literal text and placeholders.</summary>
    public IReadOnlyList<Segment> Segments { get; }

    /// <summary>One piece of a template.</summary>
    public abstract record Segment
    {
        /// <summary>
        /// How the piece was written, verbatim, for a message that has to point at it.
        /// </summary>
        /// <remarks>
        /// Verbatim, not rebuilt from the keyword constants. The keyword is matched with
        /// <see cref="StringComparison.OrdinalIgnoreCase"/>, so a rebuilt token quietly changes the
        /// casing: a message about <c>Port={PORT}</c> quoted <c>'{port}'</c>, a spelling nowhere in
        /// the developer's file, and said nothing about the one that is. Anything echoing a value
        /// back has to echo what was written — the same rule the config validator's own
        /// value-escaping follows.
        /// </remarks>
        public abstract string AsWritten { get; }
    }

    /// <summary>Text to use as-is.</summary>
    public sealed record Literal(string Text) : Segment
    {
        public override string AsWritten => Text;
    }

    /// <summary>
    /// A local port the AppHost forwards to the backing service — <c>{port}</c>, or
    /// <c>{port:amqp}</c> when the backing service forwards more than one.
    /// </summary>
    public sealed record Port(string? Name) : Segment
    {
        /// <summary>The token as the template spelled it, casing and all.</summary>
        public required string Token { get; init; }

        public override string AsWritten => Token;
    }

    /// <summary>One key of one Kubernetes secret — <c>{secret:orders-creds:password}</c>.</summary>
    public sealed record Secret(string Name, string Key) : Segment
    {
        /// <summary>The token as the template spelled it, casing and all.</summary>
        public required string Token { get; init; }

        public override string AsWritten => Token;
    }

    /// <summary>
    /// Splits <paramref name="template"/>, failing on a placeholder this package recognizes the
    /// keyword of but cannot read.
    /// </summary>
    /// <param name="template">The configured connection string.</param>
    /// <param name="backingServiceName">The backing service, for the error messages.</param>
    /// <param name="configKey">
    /// The configuration key <paramref name="template"/> was read from, so a message can name the
    /// layer that set it rather than only the file a developer usually writes it in.
    /// </param>
    public static ConnectionStringTemplate Parse(string template, string backingServiceName, string configKey)
    {
        var segments = new List<Segment>();

        // Accumulated rather than sliced out of the template. Every literal a run produces is a
        // substring of what was written, so slicing would work — but it needs an index for where
        // the run began, kept correct across the branch that finds a brace opening no placeholder
        // and resumes one character in without ending the run. That invariant is the only thing
        // either version can get wrong, and appending has no index to hold.
        var literal = new StringBuilder();
        var at = 0;

        while (at < template.Length)
        {
            var character = template[at];

            // A doubled brace is two braces, not one. Nothing here escapes anything — see the
            // remarks on the type for why a brace-doubling escape cannot be added to a connection
            // string.
            if (character is not '{')
            {
                literal.Append(character);
                at++;
                continue;
            }

            var close = template.IndexOf('}', at + 1);
            var body = close < 0 ? template[(at + 1)..] : template[(at + 1)..close];

            if (!TryReadPlaceholder(body, backingServiceName, configKey, close < 0, out var placeholder))
            {
                // Not a placeholder at all: a brace the connection string's own dialect uses. Kept
                // as text, and the scan resumes after the brace rather than after the token, so
                // that `{a{port}` still finds the placeholder inside it.
                literal.Append(character);
                at++;
                continue;
            }

            if (literal.Length > 0)
            {
                segments.Add(new Literal(literal.ToString()));
                literal.Clear();
            }

            segments.Add(placeholder);
            at = close + 1;
        }

        if (literal.Length > 0)
        {
            segments.Add(new Literal(literal.ToString()));
        }

        return new ConnectionStringTemplate(segments);
    }

    /// <summary>
    /// Reads the inside of a <c>{…}</c> token, or reports that it is not one of ours.
    /// </summary>
    /// <param name="unterminated">
    /// Whether the token had no closing brace. Only interesting once the keyword says a placeholder
    /// was meant: <c>Server={host</c> is a connection string that happens to end mid-brace, while
    /// <c>Port={port</c> is a placeholder someone forgot to close.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the token is a placeholder, <see langword="false"/> when its
    /// first word is not a keyword this package defines. A token that <i>is</i> one and is still
    /// unreadable throws rather than returning either.
    /// </returns>
    private static bool TryReadPlaceholder(
        string body,
        string backingServiceName,
        string configKey,
        bool unterminated,
        out Segment placeholder)
    {
        placeholder = null!;

        // Split on every colon rather than the first two, so an extra part is reported as one
        // instead of being folded into the last field — `{secret:a:b:c}` naming a key of `b:c`
        // would then fail at fetch time, in a cluster, against a key nobody wrote.
        var parts = body.Split(':');
        var keyword = parts[0];

        // Equality, not a prefix: `{portal}` and `{secrets:a}` are text, and only a token whose
        // word before the first colon *is* the keyword is claimed.
        if (!keyword.Equals(PortKeyword, StringComparison.OrdinalIgnoreCase)
            && !keyword.Equals(SecretKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Reassembled from the template's own text rather than from the keyword constants, so every
        // message about this token quotes the spelling the developer wrote — see Segment.AsWritten.
        var token = unterminated ? $"{{{body}" : $"{{{body}}}";

        if (unterminated)
        {
            throw Malformed(backingServiceName, configKey, token, "it has no closing '}'.");
        }

        if (keyword.Equals(PortKeyword, StringComparison.OrdinalIgnoreCase))
        {
            placeholder = parts.Length switch
            {
                1 => new Port(Name: null) { Token = token },
                2 when IsNamed(parts[1]) => new Port(parts[1]) { Token = token },
                2 => throw Malformed(
                    backingServiceName, configKey, token,
                    "the port name after 'port:' is empty. Write '{port}' for the only forwarded port, "
                    + "or '{port:<name>}' to name one of several."),
                _ => throw Malformed(
                    backingServiceName, configKey, token,
                    $"a port placeholder takes at most one name, and this has {parts.Length - 1} "
                    + "colon-separated parts after 'port'."),
            };

            return true;
        }

        placeholder = parts.Length switch
        {
            3 when IsNamed(parts[1]) && IsNamed(parts[2]) => new Secret(parts[1], parts[2]) { Token = token },
            3 => throw Malformed(
                backingServiceName, configKey, token,
                "the secret name and key must both be given: '{secret:<name>:<key>}'."),
            < 3 => throw Malformed(
                backingServiceName, configKey, token,
                "a secret placeholder names a secret and a key inside it: '{secret:<name>:<key>}'."),
            _ => throw Malformed(
                backingServiceName, configKey, token,
                $"a secret placeholder takes exactly a name and a key, and this has {parts.Length - 1} "
                + "colon-separated parts after 'secret'."),
        };

        return true;
    }

    /// <summary>Whether a placeholder's name part is a name rather than nothing.</summary>
    private static bool IsNamed(string part) => !string.IsNullOrWhiteSpace(part);

    /// <summary>
    /// Appends literal text to a connection-string expression, escaped so that a brace in it stays
    /// a brace.
    /// </summary>
    /// <remarks>
    /// <see cref="ReferenceExpressionBuilder.AppendLiteral"/> takes text that is already a
    /// <see cref="string.Format(string, object?[])"/> format string, and appends it unchanged:
    /// <see cref="ReferenceExpression"/> holds a format plus its value providers and formats them
    /// on resolution. So an unescaped <c>{</c> is read as the start of a placeholder <em>of the
    /// format's own</em>, and the failure lands at app start, not here — a <c>FormatException</c>
    /// saying it "expected an ASCII digit", naming no connection string and no backing service.
    /// <para>
    /// Which matters because a connection string is one of the few strings that carries braces for
    /// real: <c>Driver={PostgreSQL}</c> is ordinary ODBC, and a generated password may hold one
    /// anywhere. Measured against Aspire 13.5.2 — an unescaped template threw on resolution, a
    /// doubled one resolved back to exactly what was configured.
    /// </para>
    /// <para>
    /// <c>AppendFormatted(string)</c> is not the way around it: it appends the string to the format
    /// as well, so it fails identically. Escaping is the only route, and it goes here rather than in
    /// each source so that a source appending literal text around a placeholder cannot forget it.
    /// </para>
    /// </remarks>
    public static void AppendLiteral(ReferenceExpressionBuilder expression, string text) =>
        expression.AppendLiteral(
            text.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));

    /// <summary>
    /// The error for a token this package recognizes the keyword of but cannot read.
    /// </summary>
    /// <remarks>
    /// Says that the text cannot be kept, as well as what is wrong with it as a placeholder. This is
    /// the path a value that was never meant as a placeholder arrives on — <c>PWD={secret}</c> is an
    /// ODBC-quoted password that happens to be the word, and it is keyword-shaped, so it lands here
    /// rather than passing through as text. Told only what a secret placeholder should look like,
    /// its author would go on trying to write one; the fact they need is that the spelling is
    /// unavailable to them whatever they do to it, since there is no escape. See the remarks on this
    /// type for why there is not.
    /// </remarks>
    private static ServiceSourcesConfigurationException Malformed(
        string backingServiceName, string configKey, string placeholder, string problem) =>
        new($"Backing service '{backingServiceName}': the connection string carries the placeholder "
            + $"'{placeholder}', which cannot be read — {problem} "
            + $"If '{placeholder}' was meant as text, it cannot be: a '{{' begins a placeholder "
            + $"whenever the word after it — up to the first ':' or '}}', or to the end — is exactly "
            + $"'{PortKeyword}' or '{SecretKeyword}' in any casing, and there is no escape for it. "
            + $"The key is '{configKey}', which any configuration layer can set: "
            + $"{Config.DeveloperConfiguration.FileName}, appsettings, user secrets, the environment "
            + $"variable {configKey.Replace(":", "__", StringComparison.Ordinal)}, or the command line.");
}
