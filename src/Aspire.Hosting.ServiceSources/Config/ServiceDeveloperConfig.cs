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
    /// Settles <see cref="Local"/> (deprecated) against <see cref="Repository"/> (current), right
    /// after binding and before anything else reads either — see <see cref="DeveloperConfiguration.ReadFrom"/>,
    /// the one caller.
    /// </summary>
    /// <remarks>
    /// Mirrors how the retired source value <c>"local"</c> and the deprecated <c>repository.path</c>
    /// are handled elsewhere in this file's neighbourhood (<see cref="DeveloperConfigShape.ThrowIfRetiredSource"/>,
    /// <c>LocalProjectSource.LocalPathDeprecationNotice</c>): the old spelling keeps working, exactly
    /// as written, behind a one-time notice rather than a break.
    /// </remarks>
    /// <returns>
    /// The one-time deprecation notice to report when <see cref="Local"/> is the one the developer
    /// wrote; <see langword="null"/> when only <see cref="Repository"/> was written, or neither.
    /// </returns>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// Both <see cref="Local"/> and <see cref="Repository"/> are declared — two spellings of the same
    /// block, and there is no rule for which one would win.
    /// </exception>
    public Raw? ReconcileRepositoryAlias(string serviceName)
    {
        if (Local.IsDeclared && Repository.IsDeclared)
        {
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': sets both 'local' and 'repository' — 'local' is the "
                + $"deprecated alias for 'repository', the same per-developer block under its old name, so "
                + $"only one of them may be written on this entry. Keep 'repository' and remove 'local'.");
        }

        if (!Local.IsDeclared)
        {
            return null;
        }

        Repository = Local;

        return Raw.Compose(
            $"Service '{new Name(serviceName)}': the 'local' block is deprecated — renamed to 'repository' so "
            + $"it reads as the source's own name rather than colliding in spelling with "
            + $"'{Raw.Literal(DeveloperConfiguration.FileName)}'. Same fields ('path', 'ref', 'prepare'); it "
            + $"keeps working exactly as written, so this notice is only the nudge to rename it.");
    }
}
