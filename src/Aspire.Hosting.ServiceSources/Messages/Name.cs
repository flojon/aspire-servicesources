using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Messages;

/// <summary>
/// A caller-controlled name made safe to sit inside the quotes a message delimits it with, and
/// bounded. The only escaping entry point a message hole has.
/// </summary>
/// <remarks>
/// <see cref="ConfiguredValue"/> rather than a second spelling of it: it is this package's rule for
/// developer-written text echoed back, and it catches the invisibles a control-character test
/// misses. The quotes, the escape character and the cap are what it does not cover, and all three
/// are these messages' own.
/// </remarks>
internal readonly struct Name
{
    internal const int MaxLength = 64;

    private readonly string? value;

    internal Name(string? value) => this.value = value;

    public override string ToString()
    {
        var escaped = Escape(value);

        return escaped.Length <= MaxLength ? escaped : escaped[..CutAt(escaped)] + '…';
    }

    /// <summary>
    /// The escaping rule without the cap, for the one hole kind that is caller-controlled but must
    /// not be truncated: a URL, where the cut would remove the diagnosis the message exists for.
    /// </summary>
    internal static string Escape(string? value)
    {
        // The escape character first, or a name's own '\' before a quote un-escapes back to a live one.
        var literal = value?.Replace("\\", "\\\\", StringComparison.Ordinal);

        // Bare must run between the two replaces: its own \t/\n/\uXXXX escapes are synthesized after
        // the doubling, so they are not themselves doubled, and are emitted before the quote escapes,
        // which do not touch '\' and so leave them alone.
        //
        // ' rather than \', for the reason Bare already spells invisibles as \uXXXX: these
        // messages hand the reader a name to paste back into servicesources.local.json, and \' is
        // not a legal JSON escape — it makes the whole file unparseable, which is worse than the
        // unescaped name was. \" needs no such treatment; it is legal in both JSON and C#.
        return ConfiguredValue.Bare(literal)
            .Replace("'", "\\u0027", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// The last boundary at or before the cap. Walked forward in whole units rather than counted
    /// backwards: a cut inside <c>\uXXXX</c> breaks injectivity, and half a surrogate pair is not text.
    /// </summary>
    private static int CutAt(string escaped)
    {
        var last = 0;

        for (var i = 0; i < escaped.Length && i <= MaxLength;)
        {
            last = i;

            if (escaped[i] != '\\')
            {
                // The low surrogate has to be checked, not assumed: stepping over the character
                // after a high one without checking desynchronises the walk from the escape units,
                // which is how a cut lands on half of one. Bare now spells out an unpaired surrogate
                // upstream, so what reaches here is pairs only - this guards that, not lone halves.
                i += char.IsSurrogatePair(escaped, i) ? 2 : 1;
                continue;
            }

            // Bare's longest unit is \uXXXX; every other escape it emits is two characters.
            i += i + 1 < escaped.Length && escaped[i + 1] == 'u' ? 6 : 2;
        }

        return last;
    }
}
