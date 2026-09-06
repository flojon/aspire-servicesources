using System.Globalization;
using System.Text;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// How a value read out of configuration is rendered back into a message.
/// </summary>
/// <remarks>
/// Shared rather than private to <see cref="DeveloperConfigValidator"/>, which is where it started
/// and where it was the only thing echoing developer-written text. A backing service's
/// <c>kubernetes.port</c> block changed that: its keys are names the developer invents, and they are
/// quoted by the validator, by <c>KubernetesBackingServiceSource</c>'s refusals and by a health
/// check's description — three files, one rule.
/// </remarks>
internal static class ConfiguredValue
{
    /// <summary>
    /// Whether the character at <paramref name="index"/> is one a reader cannot see: whitespace, a
    /// control character, or one of Unicode's <see cref="UnicodeCategory.Format"/> characters.
    /// </summary>
    /// <remarks>
    /// Asked of the string and an index rather than of a <see cref="char"/>, because the answer for
    /// half a surrogate pair is <see cref="UnicodeCategory.Surrogate"/> whatever the pair actually
    /// spells. The invisible characters above the BMP are exactly the ones worth catching — plane 14
    /// carries the tag block, which exists to be unseeable — so asking per <see cref="char"/> would
    /// miss the deliberate cases and catch only the accidental ones.
    /// <para>
    /// It stops short of a combining mark, which is invisible too and is a real thing to write: a
    /// decomposed accented letter carries one.
    /// </para>
    /// <para>
    /// It lives here, and <see cref="DeveloperConfigValidator"/>'s trimming asks it here too, so that
    /// what a message escapes and what a remedy drops stay the same set — two spellings of one
    /// predicate are two things to keep in step.
    /// </para>
    /// </remarks>
    public static bool IsInvisible(string value, int index) =>
        char.IsWhiteSpace(value[index])
        || char.IsControl(value[index])
        || CharUnicodeInfo.GetUnicodeCategory(value, index) == UnicodeCategory.Format;

    /// <summary>
    /// Whether <paramref name="value"/> reaches a reader as the characters it is made of — nothing
    /// in it that escaping would have to spell out.
    /// </summary>
    /// <remarks>
    /// A message that quotes two values back has to ask this, because the escaping is <b>not</b>
    /// injective: a real tab and a written backslash-<c>t</c> both print as <c>\t</c>. Without it a
    /// message can read "you wrote X, which does not exist — did you mean X?", which tells the reader
    /// nothing and looks like a bug in this package rather than in their file.
    /// <para>
    /// A plain space is allowed, for the reason <see cref="Bare"/> leaves it alone: it is the
    /// character a reader assumes.
    /// </para>
    /// </remarks>
    public static bool PrintsAsItself(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != ' ' && IsInvisible(value, i))
            {
                return false;
            }

            if (char.IsSurrogatePair(value, i))
            {
                i++;
            }
        }

        return true;
    }

    /// <summary>
    /// A value as a quoted literal with its whitespace spelled out, so that a character which
    /// looks like a space — a tab, a newline, U+00A0 — is distinguishable from one.
    /// </summary>
    /// <remarks>
    /// The plain space is left as itself: it is the character a reader assumes, so escaping it
    /// would add noise to the common case and nothing else. Everything else invisible gets its
    /// code point, which is what a developer needs in order to find it in the file.
    ///
    /// Every message that echoes a value or a developer-invented key goes through this, rather than
    /// only the ones about whitespace. A message is read by someone who cannot see what they typed,
    /// and which messages a whitespace value can reach is not a thing to work out per message: it
    /// was reaching one of them unescaped for exactly as long as it took to notice.
    /// <para>
    /// It also keeps a newline in a name from forging a line of its own. These messages are relayed
    /// into <c>~/.aspire/logs</c> and routinely pasted into issues, and a port named
    /// <c>"amqp\n\nBacking service 'x': all is well."</c> would otherwise read as two sentences from
    /// this package rather than as one name.
    /// </para>
    /// </remarks>
    public static string Escaped(string? value) => $"'{Bare(value)}'";

    /// <summary>
    /// The same escaping without the surrounding quotes, for the places that build a spelling around
    /// a value rather than quoting it on its own — <c>${port:&lt;name&gt;}</c>, and a configuration
    /// key path.
    /// </summary>
    /// <remarks>
    /// Its own method rather than <see cref="Escaped"/> with the quotes trimmed back off. Trimming
    /// also strips apostrophes the value itself begins or ends with, so a port genuinely named
    /// <c>'amqp'</c> came back as <c>amqp</c> — a name that does not exist — in the one sentence
    /// whose job is to hand the reader something to paste.
    /// </remarks>
    public static string Bare(string? value)
    {
        if (value is null)
        {
            return "";
        }

        var text = new StringBuilder();

        for (var i = 0; i < value.Length; i++)
        {
            // A code point rather than a char, so that an invisible above the BMP — which arrives
            // as a surrogate pair, and which no per-char question can classify — is spelled out
            // rather than printed as the nothing it looks like.
            var width = char.IsSurrogatePair(value, i) ? 2 : 1;

            text.Append(value[i] switch
            {
                ' ' => " ",
                '\t' => "\\t",
                '\n' => "\\n",
                '\r' => "\\r",

                // The one predicate the trimming uses, rather than a second spelling of it: what a
                // message escapes and what a remedy drops have to be the same set, and two switch
                // arms saying so in different words are two things to keep in step.
                // Spelled as the surrogate pair rather than as the code point it makes, because
                // this text is advice a developer pastes back: `\uD834\uDD73` is a JSON escape and
                // `\U0001d173` is not, so the eight-digit form would name a spelling that breaks the
                // file it is typed into.
                _ when IsInvisible(value, i) => width == 1
                    ? $"\\u{(int)value[i]:x4}"
                    : $"\\u{(int)value[i]:x4}\\u{(int)value[i + 1]:x4}",

                _ => value.Substring(i, width),
            });

            i += width - 1;
        }

        return text.ToString();
    }
}
