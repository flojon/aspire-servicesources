using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Catalog;

/// <summary>
/// Accumulates code-declared service entries for one <c>AddServiceCatalog(…)</c> call (or several —
/// see <see cref="ServiceSourcesBuilderExtensions.AddServiceCatalog"/>, which appends). Frozen once
/// the catalog is composed (<c>LoadedConfig.Load</c>); any call reaching a frozen builder throws
/// <see cref="InvalidOperationException"/>, per design "Composition, freezing, and the errors".
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class ServiceCatalogBuilder
{
    private readonly Dictionary<string, ServiceDefinitionBuilder> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RepositoryBuilder> _repositories = new(StringComparer.Ordinal);
    private bool _frozen;

    internal ServiceCatalogBuilder()
    {
    }

    /// <summary>
    /// Declares a service, returning a chain to configure its source(s). Two code-declared names
    /// differing only by case are rejected here, naming both — see design "Names."
    /// </summary>
    [AspireExport]
    public ServiceDefinitionBuilder AddService(string name)
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                "This ServiceCatalogBuilder was already frozen by AddServiceCatalog composing the catalog. " +
                "A builder captured and mutated after that point contributes nothing — declare every service " +
                "before the first AddService(…) call instead.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ServiceSourcesConfigurationException(
                "AddServiceCatalog: a service name is required and cannot be empty or whitespace.");
        }

        var caseCollision = _entries.Keys.FirstOrDefault(
            existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(existing, name, StringComparison.Ordinal));

        if (caseCollision is not null)
        {
            throw new ServiceSourcesConfigurationException(
                $"AddServiceCatalog: service '{name}' differs only by case from already-declared " +
                $"'{caseCollision}'. Two code-declared names must differ by more than case.");
        }

        if (!_entries.TryAdd(name, new ServiceDefinitionBuilder(name)))
        {
            throw new ServiceSourcesConfigurationException(
                $"AddServiceCatalog: service '{name}' is declared twice in code. Remove one of the two calls.");
        }

        return _entries[name];
    }

    /// <summary>
    /// Declares a repository as a shared handle — pass the result to several services'
    /// <see cref="ServiceDefinitionBuilder.WithSharedRepository"/> to clone it once and share the
    /// working tree, rather than each cloning their own. See design "The authoring API" and the
    /// monorepo shape under "Motivation".
    /// </summary>
    /// <param name="name">
    /// This repository's <see cref="RepositoryDefinition.CheckoutName"/>, validated the same way a
    /// service name is before it becomes a directory (#224).
    /// </param>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// <paramref name="url"/> is blank; <paramref name="name"/> cannot be a checkout directory of its
    /// own (#224); or <paramref name="name"/> collides with an already-declared repository.
    /// </exception>
    public RepositoryBuilder AddRepository([ResourceName] string name, string url, string? defaultRef = null)
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                "This ServiceCatalogBuilder was already frozen by AddServiceCatalog composing the catalog. " +
                "A builder captured and mutated after that point contributes nothing — declare every service " +
                "and repository before the first AddService(…)/AddRepository(…) call instead.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ServiceSourcesConfigurationException(
                "AddRepository: a repository name is required and cannot be empty or whitespace.");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ServiceSourcesConfigurationException(
                "AddRepository: a repository url is required and cannot be empty or whitespace.");
        }

        if (!LocalGitCheckout.IsContainedCheckoutDirectoryName(name))
        {
            throw new ServiceSourcesConfigurationException(
                $"AddRepository: '{name}' cannot be used as this repository's checkout directory name — "
                + LocalGitCheckout.ContainedNameRule);
        }

        if (_repositories.TryGetValue(name, out var existing))
        {
            throw new ServiceSourcesConfigurationException(
                $"AddRepository: '{name}' is already declared, naming '{GitUrl.Redact(existing.Url)}'. Two "
                + "repositories cannot share one checkout directory name — pass a different name to this "
                + $"AddRepository call for '{GitUrl.Redact(url)}', or to the other one already declared.");
        }

        // Case-only, the same rule AddService already applies to two code-declared service names
        // (design "Names"): the default filesystem on Windows and macOS, the two platforms this
        // inner dev loop tool primarily targets, is case-insensitive, so 'checkouts/Monorepo' and
        // 'checkouts/monorepo' are the same directory — two unrelated repositories would clone into
        // (and fight over) one, surfacing later as a confusing checkout-mismatch error instead of
        // this composition-time one.
        var caseCollision = _repositories.Keys.FirstOrDefault(
            existingName => string.Equals(existingName, name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(existingName, name, StringComparison.Ordinal));

        if (caseCollision is not null)
        {
            throw new ServiceSourcesConfigurationException(
                $"AddRepository: '{name}' differs only by case from already-declared "
                + $"'{caseCollision}'. Two repositories must differ by more than case.");
        }

        var repository = new RepositoryBuilder(url, name, defaultRef);
        _repositories.Add(name, repository);
        return repository;
    }

    /// <summary>
    /// Builds every accumulated service and repository entry and marks this builder frozen. Every
    /// declared repository is built whether or not a service names it — design question 2 decided an
    /// unused repository goes unreported, so there is nothing here to filter.
    /// </summary>
    internal (
        IReadOnlyDictionary<string, ServiceDefinition> Services,
        IReadOnlyDictionary<string, RepositoryDefinition> Repositories) Freeze()
    {
        _frozen = true;

        var services = _entries.ToDictionary(e => e.Key, e => e.Value.Build(), StringComparer.Ordinal);
        var repositories = _repositories.ToDictionary(e => e.Key, e => e.Value.Build(), StringComparer.Ordinal);

        return (services, repositories);
    }
}
