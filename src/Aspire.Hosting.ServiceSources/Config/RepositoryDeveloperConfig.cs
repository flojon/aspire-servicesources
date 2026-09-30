namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// A developer's settings for one repository — a group of services (#291) sharing a checkout — read
/// from <c>ServiceSources:Repositories:{CheckoutName}</c>. Mirrors <see cref="LocalDeveloperConfig"/>'s
/// three fields, since a repository-level override is the same three questions a service's own
/// <c>repository</c> block answers, now asked once for the whole group instead of per member.
/// </summary>
internal sealed class RepositoryDeveloperConfig
{
    /// <summary>
    /// An existing directory every grouped member uses as its repository root, so nothing is cloned,
    /// fetched or reconciled. A member's own <c>repository.path</c> still wins, and it cannot be
    /// combined with <see cref="Ref"/>.
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
