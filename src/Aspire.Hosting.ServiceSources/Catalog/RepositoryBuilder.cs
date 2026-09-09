using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Catalog;

/// <summary>
/// A shared repository handle, returned by <see cref="ServiceCatalogBuilder.AddRepository"/> and
/// passed to <see cref="ServiceDefinitionBuilder.WithSharedRepository"/> by every service that clones
/// it — one working tree for all of them, rather than one each. See design "The authoring API": this
/// type's whole surface is <see cref="WithPrepare"/>. There is deliberately no <c>AddService</c> here
/// — a service stays declared on the catalog, and the repository is a block it can carry, exactly
/// like <c>WithUrl</c> or <c>WithContainer</c> (design "Does AddService belong on the repository
/// handle?").
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class RepositoryBuilder
{
    private readonly string _url;
    private readonly string _name;
    private readonly string? _defaultRef;

    private PrepareMetadata? _prepare;
    private RepositoryDefinition? _built;

    internal RepositoryBuilder(string url, string name, string? defaultRef)
    {
        _url = url;
        _name = name;
        _defaultRef = defaultRef;
    }

    /// <summary>
    /// This repository's resolved name — derived from <paramref name="url"/> or the explicit
    /// <c>name:</c> <see cref="ServiceCatalogBuilder.AddRepository"/> was given — used as this
    /// repository's <see cref="RepositoryDefinition.CheckoutName"/> and in error messages that need
    /// to name the handle rather than any one service on it.
    /// </summary>
    internal string Name => _name;

    /// <summary>This repository's URL, for a collision message that names what two declarations point at.</summary>
    internal string Url => _url;

    /// <summary>
    /// Declares a bootstrap command the <c>"local"</c> source runs once inside this repository's
    /// shared checkout, before any service on it is allowed to run — the equivalent of
    /// <see cref="ServiceDefinitionBuilder.WithPrepare"/>, moved here because the step runs once per
    /// checkout, not once per service (design "prepare moves to the repository", finding 3). See
    /// <see cref="ServiceDefinitionBuilder.WithPrepare"/> for the parameters' full documentation —
    /// they are identical.
    /// </summary>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// <paramref name="mode"/> is not one of the four <see cref="PrepareMode"/> values.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Called after this repository's <see cref="RepositoryDefinition"/> was already built — every
    /// service sharing this handle has already been frozen into its final, immutable form, so a
    /// prepare step declared now would silently not reach any of them. Unreachable through the
    /// ordinary API: <c>AddServiceCatalog</c>'s configure lambda always finishes running, and this
    /// handle along with it, before the catalog is ever frozen.
    /// </exception>
    public RepositoryBuilder WithPrepare(
        string[] command, string[]? windowsCommand = null, PrepareMode mode = PrepareMode.OncePerCommit)
    {
        if (_built is not null)
        {
            throw new InvalidOperationException(
                $"Repository '{_name}' was already built — every service sharing this handle has already been "
                + $"frozen, so a prepare step declared now would reach none of them. Call {nameof(WithPrepare)} "
                + "before the catalog is composed, alongside AddRepository.");
        }

        if (_prepare is not null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Repository '{_name}': {nameof(WithPrepare)} was already called. Calls are additive across "
                + "different blocks, but a repeated call to the same one is not — remove one of the two.");
        }

        _prepare = PrepareMetadataFactory.Create($"Repository '{_name}'", command, windowsCommand, mode);

        return this;
    }

    /// <summary>
    /// Builds — once, and cached thereafter — the immutable <see cref="RepositoryDefinition"/> every
    /// service naming this handle shares. Every caller gets the identical instance back, which is the
    /// reference-equality identity mechanism design "The domain type" describes: nothing compares two
    /// repositories for equality, the shared instance simply <em>is</em> the group.
    /// </summary>
    internal RepositoryDefinition Build() => _built ??= new RepositoryDefinition
    {
        Url = _url,
        DefaultRef = _defaultRef,
        Prepare = _prepare,
        CheckoutName = _name,
    };
}
