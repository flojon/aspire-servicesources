namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for the <c>"local"</c> source, read from the <c>local</c> block of a
/// service's entry. Bound only when that is the entry's effective source.
/// </summary>
internal sealed class LocalDeveloperConfig
{
    /// <summary>An existing checkout to use as-is, instead of one this tool clones and manages.</summary>
    public string? Path { get; set; }

    /// <summary>The ref a managed checkout sits on. Cannot be combined with <see cref="Path"/>.</summary>
    public string? Ref { get; set; }
}
