using System.Text;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Cuts git's progress stream into lines as the chunks of it arrive.
/// </summary>
/// <remarks>
/// <para>
/// Progress is <c>\r</c>-separated, not <c>\n</c>-separated: git rewrites one line in place, so a
/// <c>ReadLine()</c> loop blocks until the phase ends and then delivers the whole phase as a single
/// enormous line. Splitting on both delimiters is what makes the stream readable while it is still
/// arriving — which is the whole point of reporting progress at all.
/// </para>
/// <para>
/// Holds the tail of a chunk that no delimiter has closed yet, so a line split across two reads is
/// reported once and whole. One instance per stream; not thread-safe, and nothing needs it to be —
/// a stream has one reader.
/// </para>
/// </remarks>
internal sealed class ProgressLineSplitter
{
    private readonly StringBuilder _pending = new();

    /// <summary>
    /// The lines <paramref name="chunk"/> completes, in order. Empty when it completed none.
    /// </summary>
    public IReadOnlyList<string> Append(string chunk)
    {
        List<string>? lines = null;

        foreach (var character in chunk)
        {
            if (character is '\r' or '\n')
            {
                // A "\r\n" closes one line and then finds nothing to close, and a phase git rewrote
                // without producing anything ends up the same way. Neither is a blank line worth
                // reporting.
                if (Take() is { } line)
                {
                    (lines ??= []).Add(line);
                }

                continue;
            }

            _pending.Append(character);
        }

        return lines ?? [];
    }

    /// <summary>
    /// The last line, if the stream ended without a delimiter after it. git's final progress line
    /// arrives this way — the "done." one, terminated by the process exiting rather than by a
    /// newline — so without this the phase would appear to stop one line short of finishing.
    /// </summary>
    public string? Flush() => Take();

    /// <summary>
    /// Empties the buffer, returning what was in it or <see langword="null"/> if that was nothing
    /// but the padding git writes to erase a longer line it printed before.
    /// </summary>
    private string? Take()
    {
        // TrimEnd, not Trim: git pads each progress line with trailing spaces so that it covers the
        // longer one underneath it in a terminal, and those would otherwise reach the resource logs.
        // Leading whitespace is git's own alignment of the percentage and is left alone.
        var line = _pending.ToString().TrimEnd();
        _pending.Clear();

        return line.Length > 0 ? line : null;
    }
}
