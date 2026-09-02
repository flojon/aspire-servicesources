namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for the <c>"container"</c> source, read from the <c>container</c> block
/// of a service's entry. Bound only when that is the entry's effective source.
/// </summary>
internal sealed class ContainerDeveloperConfig
{
    /// <summary>Overrides the catalog's <c>container.defaultTag</c> for this service.</summary>
    public string? Tag { get; set; }
}
