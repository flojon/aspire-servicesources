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
    /// Already-safe fragments joined. Safe by construction: every part is a <see cref="Raw"/>
    /// already and the separator is a constant, so nothing unescaped enters through here.
    /// </summary>
    internal static Raw Join([ConstantExpected] string separator, IEnumerable<Raw> parts) =>
        new(string.Join(separator, parts.Select(part => part.ToString())));

    /// <summary>
    /// Where a catalog entry came from. Takes the origin itself rather than its rendering, so a
    /// name cannot be passed here — and its wording carries quotes that must not be escaped.
    /// </summary>
    internal static Raw Origin(CatalogOrigin origin) => new(origin.Describe());

    /// <summary>Empty rather than null: <c>default(Raw)</c> must render, not throw.</summary>
    public override string ToString() => text ?? string.Empty;
}
