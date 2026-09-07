using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Catalog;

/// <summary>
/// The fluent <c>With*</c>/<c>As*</c> chain for one code-declared service. Accumulates onto a
/// mutable internal state object; <see cref="Build"/> (called only by
/// <see cref="ServiceCatalogBuilder.Freeze"/>) turns it into an immutable <see cref="ServiceDefinition"/>.
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class ServiceDefinitionBuilder
{
    private readonly string _serviceName;

    // Task 3 adds Repository/Project/DefaultRef fields and the single folded WithRepository(url,
    // project:, defaultRef:) — no separate WithProject method exists. Task 4 adds Url/WithUrl; Task 5
    // adds Container/WithContainer; Task 6 adds Kubernetes/WithKubernetes; Task 8 adds
    // Kind/KindOptions/WithKind. Each field starts null and each With* throws the additive-error
    // below if its field is already set — see Task 3 for the exact error and the shared helper.

    internal ServiceDefinitionBuilder(string serviceName)
    {
        _serviceName = serviceName;
    }

    internal string ServiceName => _serviceName;

    /// <summary>Builds the immutable <see cref="ServiceDefinition"/> this chain describes.</summary>
    internal ServiceDefinition Build() => new()
    {
        // Repository/Project default to "" (ServiceMetadata's own defaults) until Task 3 sets them —
        // a service declared with no With* call at all is caught downstream by the same
        // "no source configured" path an empty yaml entry hits today, not rejected here.
        Repository = "",
        Project = "",
        Kind = LocalKinds.Dotnet,
        Origin = CatalogOrigin.Code,
    };
}
