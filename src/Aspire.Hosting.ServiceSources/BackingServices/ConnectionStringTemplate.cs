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
/// claimed every <c>{…}</c> would reject them. Only a token whose first word is one this package
/// defines is read as a placeholder — and then it is read strictly, because at that point a
/// developer plainly meant one.
/// </para>
/// <para>
/// There is no escape for a literal <c>{port}</c> or <c>{secret:a:b}</c>. Doubling the brace is the
/// obvious spelling to add if a connection string ever needs one, and nothing needs one today.
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
        /// <summary>How the piece was written, for a message that has to point at it.</summary>
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
        public override string AsWritten => Name is null ? $"{{{PortKeyword}}}" : $"{{{PortKeyword}:{Name}}}";
    }

    /// <summary>One key of one Kubernetes secret — <c>{secret:orders-creds:password}</c>.</summary>
    public sealed record Secret(string Name, string Key) : Segment
    {
        public override string AsWritten => $"{{{SecretKeyword}:{Name}:{Key}}}";
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
        var literalFrom = 0;
        var at = 0;

        while (at < template.Length)
        {
            var open = template.IndexOf('{', at);

            if (open < 0)
            {
                break;
            }

            var close = template.IndexOf('}', open + 1);
            var body = close < 0 ? template[(open + 1)..] : template[(open + 1)..close];

            if (!TryReadPlaceholder(body, backingServiceName, configKey, close < 0, out var placeholder))
            {
                // Not a placeholder at all: a brace the connection string's own dialect uses. Left
                // in the literal run, and the scan resumes after the brace rather than after the
                // token, so that `{a{port}` still finds the placeholder inside it.
                at = open + 1;
                continue;
            }

            if (open > literalFrom)
            {
                segments.Add(new Literal(template[literalFrom..open]));
            }

            segments.Add(placeholder);
            literalFrom = at = close + 1;
        }

        if (literalFrom < template.Length)
        {
            segments.Add(new Literal(template[literalFrom..]));
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

        if (!keyword.Equals(PortKeyword, StringComparison.OrdinalIgnoreCase)
            && !keyword.Equals(SecretKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (unterminated)
        {
            throw Malformed(
                backingServiceName,
                configKey,
                $"{{{body}",
                "it has no closing '}'.");
        }

        if (keyword.Equals(PortKeyword, StringComparison.OrdinalIgnoreCase))
        {
            placeholder = parts.Length switch
            {
                1 => new Port(Name: null),
                2 when IsNamed(parts[1]) => new Port(parts[1]),
                2 => throw Malformed(
                    backingServiceName, configKey, $"{{{body}}}",
                    "the port name after 'port:' is empty. Write '{port}' for the only forwarded port, "
                    + "or '{port:<name>}' to name one of several."),
                _ => throw Malformed(
                    backingServiceName, configKey, $"{{{body}}}",
                    $"a port placeholder takes at most one name, and this has {parts.Length - 1} "
                    + "colon-separated parts after 'port'."),
            };

            return true;
        }

        placeholder = parts.Length switch
        {
            3 when IsNamed(parts[1]) && IsNamed(parts[2]) => new Secret(parts[1], parts[2]),
            3 => throw Malformed(
                backingServiceName, configKey, $"{{{body}}}",
                "the secret name and key must both be given: '{secret:<name>:<key>}'."),
            < 3 => throw Malformed(
                backingServiceName, configKey, $"{{{body}}}",
                "a secret placeholder names a secret and a key inside it: '{secret:<name>:<key>}'."),
            _ => throw Malformed(
                backingServiceName, configKey, $"{{{body}}}",
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

    private static ServiceSourcesConfigurationException Malformed(
        string backingServiceName, string configKey, string placeholder, string problem) =>
        new($"Backing service '{backingServiceName}': the connection string carries the placeholder "
            + $"'{placeholder}', which cannot be read — {problem} "
            + $"The key is '{configKey}', which any configuration layer can set: "
            + $"{Config.DeveloperConfiguration.FileName}, appsettings, user secrets, the environment "
            + $"variable {configKey.Replace(":", "__", StringComparison.Ordinal)}, or the command line.");
}
