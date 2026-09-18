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
        // The escape character first, or a name's own '\' before a quote un-escapes back to a live one.
        var literal = value?.Replace("\\", "\\\\", StringComparison.Ordinal);

        // Bare must run between the two replaces: its own \t/\n/\uXXXX escapes are synthesized after
        // the doubling, so they are not themselves doubled, and are emitted before the quote escapes,
        // which do not touch '\' and so leave them alone.
        var escaped = ConfiguredValue.Bare(literal)
            .Replace("'", "\\'", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

        return escaped.Length <= MaxLength ? escaped : escaped[..CutAt(escaped)] + '…';
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
                i += char.IsHighSurrogate(escaped[i]) && i + 1 < escaped.Length ? 2 : 1;
                continue;
            }

            // Bare's longest unit is \uXXXX; every other escape it emits is two characters.
            i += i + 1 < escaped.Length && escaped[i + 1] == 'u' ? 6 : 2;
        }

        return last;
    }
}
