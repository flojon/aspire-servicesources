using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Catalog;

/// <summary>
/// Accumulates code-declared service entries for one <c>AddServiceCatalog(…)</c> call (or several —
/// see <see cref="ServiceSourcesBuilderExtensions.AddServiceCatalog"/>, which appends). Frozen once
/// the catalog is composed (<c>LoadedConfig.Load</c>); any call reaching a frozen builder is ignored,
/// per design "Composition, freezing, and the errors".
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class ServiceCatalogBuilder
{
    private readonly Dictionary<string, ServiceDefinitionBuilder> _entries = new(StringComparer.Ordinal);
    private bool _frozen;

    /// <summary>
    /// Declares a service, returning a chain to configure its source(s). Two code-declared names
    /// differing only by case are rejected here, naming both — see design "Names."
    /// </summary>
    [AspireExport("addServiceToCatalog")]
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

    /// <summary>Builds every accumulated entry and marks this builder frozen.</summary>
    internal IReadOnlyDictionary<string, ServiceDefinition> Freeze()
    {
        _frozen = true;
        return _entries.ToDictionary(e => e.Key, e => e.Value.Build(), StringComparer.Ordinal);
    }
}
