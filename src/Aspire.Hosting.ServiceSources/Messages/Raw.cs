using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Messages;

/// <summary>
/// Message text that is already safe: either composed through the seam, or a compile-time constant.
/// </summary>
/// <remarks>
/// No accessible constructor, deliberately. A wrapper anyone could call would be a one-token bypass
/// spelled identically to its safe use — the property this seam exists to remove.
/// </remarks>
internal readonly struct Raw
{
    private readonly string? text;

    private Raw(string? text) => this.text = text;

    /// <summary>Text built through the seam, so every name inside it is already escaped.</summary>
    internal static Raw Compose(ServiceTextHandler text) => new(text.Text);

    /// <summary>A compile-time constant, which cannot carry a runtime value.</summary>
    internal static Raw Literal([ConstantExpected] string text) => new(text);

    /// <summary>
    /// Caller-controlled text escaped by the <see cref="Name"/> rule but deliberately not capped —
    /// a URL, where truncating at 64 would remove the diagnosis the message exists to give.
    /// </summary>
    /// <remarks>
    /// Safe by the same rule as a <see cref="Name"/> hole, not by being trusted: this escapes its
    /// argument rather than believing it, which is what separates it from a bypass.
    /// </remarks>
    internal static Raw Escaped(string? value) => new(Name.Escape(value));

    /// <summary>
    /// Already-safe fragments joined. Safe by construction: every part is a <see cref="Raw"/>
    /// already and the separator is a constant, so nothing unescaped enters through here.
    /// </summary>
    internal static Raw Join([ConstantExpected] string separator, IEnumerable<Raw> parts) =>
        new(string.Join(separator, parts.Select(part => part.ToString())));

    /// <summary>
    /// Where a catalog entry came from. Takes the origin itself rather than its rendering, so a
    /// name cannot be passed here — and its wording carries quotes that must not be escaped.
    /// </summary>
    /// <remarks>
    /// The yaml path is escaped rather than trusted: it is a developer-chosen filesystem path sitting
    /// inside this message's own quotes, so a directory named with an apostrophe closes them.
    /// <see cref="CatalogOrigin.Describe"/> still renders it raw for the sites this seam has not
    /// reached, which is why the escaped spelling is composed here instead of moved into it.
    /// </remarks>
    internal static Raw Origin(CatalogOrigin origin) =>
        origin.Kind == CatalogOriginKind.Code
            ? new(origin.Describe())
            : Compose($"'{Escaped(origin.YamlPath)}'");

    /// <summary>
    /// A third party's wording, made unable to forge a line. Takes the exception rather than its
    /// message, so a name cannot be passed here.
    /// </summary>
    /// <remarks>
    /// Neither quoted nor capped: a cause is a diagnosis, not a name, and truncating it would
    /// discard the detail the line exists to carry.
    /// </remarks>
    internal static Raw Cause(Exception exception) => new(SingleLine(exception.Message));

    /// <remarks>
    /// <see cref="string.ReplaceLineEndings(string)"/> rather than a hand-written set of three:
    /// U+0085, U+2028, U+2029 and U+000C also start a line, and a rule written out here is a fourth
    /// spelling of one this package already has. U+000B is not in that set — measured, not assumed.
    /// </remarks>
    private static string SingleLine(string message) => message.ReplaceLineEndings("\\n");

    /// <summary>Empty rather than null: <c>default(Raw)</c> must render, not throw.</summary>
    public override string ToString() => text ?? string.Empty;
}
