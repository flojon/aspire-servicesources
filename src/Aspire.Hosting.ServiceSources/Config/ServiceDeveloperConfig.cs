using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// One service's entry in the developer config. Each source's settings live in a block named for
/// that source, so only the block <see cref="Source"/> names is ever read.
/// </summary>
/// <remarks>
/// The nesting is what makes a source switchable from a higher configuration layer.
/// <see cref="IConfiguration"/> merges layers per key rather than per object, so with the settings
/// flat on this type a lower layer's <c>url</c> would survive a higher layer setting
/// <c>source: repository</c> and land here alongside it. Under a block it still survives, but nothing
/// reads it.
///
/// The blocks are never null. An entry naming a source with no block of its own is the common case
/// — <c>{ "source": "repository" }</c> is a complete entry — and an absent block and an empty one mean
/// the same thing, so consumers read through them without a null check.
///
/// <see cref="Repository"/> is the one exception to "a block named for its source": <see
/// cref="Local"/> is its deprecated alias, bound from the same-shaped <c>local</c> key so a config
/// written before the rename keeps working. <see cref="ReconcileRepositoryAlias"/> settles the two
/// into one before anything else reads either — every other consumer reads only <see
/// cref="Repository"/>.
/// </remarks>
internal sealed class ServiceDeveloperConfig
{
    public string Source { get; set; } = "";

    /// <summary>
    /// The <c>"repository"</c> source's settings, read from the <c>repository</c> block.
    /// </summary>
    /// <remarks>
    /// The effective value once <see cref="ReconcileRepositoryAlias"/> has run: every consumer past
    /// that point — <c>LocalProjectSource</c>, <c>LocalGitCheckout</c>, the checkout prefetch — reads
    /// this and never <see cref="Local"/>, whichever key the developer actually wrote.
    /// </remarks>
    public LocalDeveloperConfig Repository { get; set; } = new();

    /// <summary>
    /// The deprecated spelling of <see cref="Repository"/>'s block — <c>local</c>, from when the
    /// <c>"repository"</c> source was itself named <c>"local"</c>. Kept only so
    /// <see cref="ReconcileRepositoryAlias"/> has something to bind it from; nothing past that point
    /// reads this.
    /// </summary>
    public LocalDeveloperConfig Local { get; set; } = new();

    public PathDeveloperConfig Path { get; set; } = new();

    public UrlDeveloperConfig Url { get; set; } = new();

    public KubernetesDeveloperConfig Kubernetes { get; set; } = new();

    public ContainerDeveloperConfig Container { get; set; } = new();

    /// <summary>
    /// Settles <see cref="Local"/> (deprecated) against <see cref="Repository"/> (current), so every
    /// consumer past this point reads only <see cref="Repository"/> whichever key the developer
    /// actually wrote. See <see cref="DeveloperConfiguration.ReadFrom"/>, the one caller — scoped
    /// there to the services this AppHost's catalog actually declares, and collected across every
    /// such entry so a conflict is reported once for the whole file rather than one failed startup
    /// per faulted service.
    /// </summary>
    /// <remarks>
    /// Deliberately does not throw, and does not compose the alias's own deprecation notice —
    /// unlike the retired source value <c>"local"</c> (<see cref="DeveloperConfigShape.ThrowIfRetiredSource"/>),
    /// which is a hard, unconditional error, or the deprecated <c>repository.path</c> notice
    /// (<c>LocalProjectSource.LocalPathDeprecationNotice</c>), whose call site already decides the
    /// notice separately. This block-name alias earns the identical treatment as that neighbour:
    /// <c>LocalProjectSource.RepositoryAliasDeprecationNotice</c> fires only once a service is
    /// actually resolved through <c>"repository"</c>, reading <see cref="Local"/>.IsDeclared (still
    /// true after the merge below, since it is never cleared) rather than anything returned here.
    /// </remarks>
    /// <returns>
    /// The conflict reason when both <see cref="Local"/> and <see cref="Repository"/> are declared —
    /// two spellings of the same block, with no rule for which one would win — so the caller can
    /// collect it rather than fail immediately; <see langword="null"/> otherwise, after merging
    /// <see cref="Local"/> into <see cref="Repository"/> when <see cref="Local"/> is the one the
    /// developer wrote.
    /// </returns>
    public Raw? ReconcileRepositoryAlias(string serviceName)
    {
        if (Local.IsDeclared && Repository.IsDeclared)
        {
            return Raw.Compose(
                $"Service '{new Name(serviceName)}': sets both 'local' and 'repository' — 'local' is the "
                + $"deprecated alias for 'repository', the same per-developer block under its old name, so "
                + $"only one of them may be written on this entry. Keep 'repository' and remove 'local'.");
        }

        if (Local.IsDeclared)
        {
            Repository = Local;
        }

        return null;
    }
}
