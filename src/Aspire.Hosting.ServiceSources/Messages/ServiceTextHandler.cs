using System.Runtime.CompilerServices;
using System.Text;

namespace Aspire.Hosting.ServiceSources.Messages;

/// <summary>
/// The seam every reader-facing message is composed through. A hole is a <see cref="Name"/>, a
/// <see cref="Raw"/>, or one of the primitives enumerated here — anything else does not compile.
/// </summary>
/// <remarks>
/// There is deliberately no open generic <c>AppendFormatted&lt;T&gt;</c> and no <c>string</c>
/// overload: an un-enumerated hole must be refused, not escaped by default.
/// </remarks>
[InterpolatedStringHandler]
internal ref struct ServiceTextHandler
{
    private readonly StringBuilder builder;

    public ServiceTextHandler(int literalLength, int formattedCount) =>
        builder = new StringBuilder(literalLength + (formattedCount * 16));

    /// <summary>The message author's own words.</summary>
    public void AppendLiteral(string value) => builder.Append(value);

    /// <summary>Caller-controlled: escaped, then capped.</summary>
    public void AppendFormatted(Name value) => builder.Append(value.ToString());

    /// <summary>Already safe, and never capped — a path or a joined list must not be truncated.</summary>
    public void AppendFormatted(Raw value) => builder.Append(value.ToString());

    public void AppendFormatted(int value) => builder.Append(value);

    public void AppendFormatted(int? value) => builder.Append(value);

    /// <summary>
    /// Refused because <c>char</c> widens to <c>int</c>, which would otherwise accept '\n' silently.
    /// </summary>
    [Obsolete("A char hole is refused: wrap the value as Name or Raw.", error: true)]
    public void AppendFormatted(char value) => throw new NotSupportedException();

    internal string Text => builder.ToString();
}
