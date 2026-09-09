namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// A developer's settings for one repository — a group of services (#291) sharing a checkout — read
/// from <c>ServiceSources:Repositories:{CheckoutName}</c>. Mirrors <see cref="LocalDeveloperConfig"/>'s
/// three fields, since a repository-level override is the same three questions a service's own
/// <c>local</c> block answers, now asked once for the whole group instead of per member.
/// </summary>
internal sealed class RepositoryDeveloperConfig
{
    /// <summary>
    /// Reserved rather than implemented as a whole-group redirect: a non-null value is rejected as a
    /// configuration error naming the repository. Design finding 4 is explicit that the per-service
    /// <c>local.path</c> escape is the only one ("No new field, no repository-level list of
    /// exceptions") — a developer who needs to redirect one member's checkout still does it on that
    /// service's own entry, not here. The field exists on the type because the design names the
    /// triple literally, and because a future repository-level redirect, if one is ever added, has
    /// somewhere to land without another config-shape change.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>The ref the group's shared managed checkout sits on.</summary>
    public string? Ref { get; set; }

    /// <summary>
    /// This developer's own <c>prepare</c> step for the group's shared checkout, merged over the
    /// repository's catalog block per field — the same merge <see cref="LocalDeveloperConfig.Prepare"/>
    /// performs for an ungrouped service's block.
    /// </summary>
    public PrepareDeveloperConfig? Prepare { get; set; }
}
