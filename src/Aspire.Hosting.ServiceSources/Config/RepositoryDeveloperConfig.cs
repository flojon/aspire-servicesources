namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// A developer's settings for one repository — a group of services (#291) sharing a checkout — read
/// from <c>ServiceSources:Repositories:{CheckoutName}</c>.
/// </summary>
internal sealed class RepositoryDeveloperConfig
{
    /// <summary>
    /// <c>"path"</c> points the whole group at <see cref="Path"/>; <c>"repository"</c> is the explicit
    /// managed clone, which lets a higher layer cancel a lower layer's <c>"path"</c>. Anything else is
    /// refused when the configuration is read.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// The directory the group uses as its repository root. Read only while <see cref="Source"/> is
    /// <c>"path"</c>, the way a service's block for an unselected source is never read.
    /// </summary>
    public PathDeveloperConfig? Path { get; set; }

    /// <summary>The ref the group's shared managed checkout sits on. Unread while the group is on <c>"path"</c>.</summary>
    public string? Ref { get; set; }

    /// <summary>Refused when the configuration is read; a per-service <c>path.prepare</c> is the supported spelling.</summary>
    public PrepareDeveloperConfig? Prepare { get; set; }
}
