namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for the <c>"url"</c> source, read from the <c>url</c> block of a
/// service's entry. Bound only when that is the entry's effective source.
/// </summary>
internal sealed class UrlDeveloperConfig
{
    /// <summary>Overrides the catalog's <c>url.url</c> for this service.</summary>
    public string? Url { get; set; }
}
