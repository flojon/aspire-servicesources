namespace Aspire.Hosting.ServiceSources.Config.Catalog;

/// <summary>
/// One repository: the "local" source's block, referenced by every <see cref="ServiceDefinition"/>
/// that names it. Every service has one — an ungrouped service gets its own anonymous instance,
/// minted by whichever producer declared it (<see cref="ServiceMetadata.ToDefinition"/> or
/// <see cref="Catalog.ServiceDefinitionBuilder.Build"/>), so there is no null case downstream.
/// Identity is the instance itself, never a value compared for equality — see design "The domain
/// type".
/// </summary>
internal sealed class RepositoryDefinition
{
    public required string Url { get; init; }

    public string? DefaultRef { get; init; }

    public PrepareMetadata? Prepare { get; init; }

    /// <summary>
    /// The single directory name a managed checkout of this repository is placed under
    /// (<c>checkouts/&lt;CheckoutName&gt;</c>). For an anonymous (ungrouped) record this is always
    /// the owning service's name, which is what keeps every existing checkout path byte-identical
    /// (design finding 2, criterion 6); a shared record (<c>AddRepository</c>/
    /// <c>WithSharedRepository</c>, or yaml's <c>repositoryRef</c>) carries the repository's own
    /// name instead, so every service naming it resolves the identical directory. Read wherever a
    /// message or a checkout path needs to distinguish "this service" from "this service's
    /// repository" — see <c>PreparePlan.ServiceLabel</c>/<c>RepositoryLabel</c> and
    /// <c>LocalGitCheckout.PrepareRepoRoot</c>'s grouped-service checks — and still to be re-keyed
    /// onto for the checkout directory itself (Task 5/#291).
    /// </summary>
    public required string CheckoutName { get; init; }
}
