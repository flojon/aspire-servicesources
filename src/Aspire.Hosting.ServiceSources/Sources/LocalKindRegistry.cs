using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Per-builder registry of <see cref="ILocalResourceKind"/> handlers, keyed by the <c>kind</c>
/// name a service's <c>servicesources.yaml</c> entry declares. Populated by satellite packages via
/// <see cref="ServiceSourcesBuilderExtensions.AddLocalKind"/>, consulted by the <c>"local"</c>
/// source for any kind other than the built-in <c>"dotnet"</c>.
/// </summary>
internal sealed class LocalKindRegistry
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, LocalKindRegistry> Cache = new();

    private readonly Dictionary<string, ILocalResourceKind> _handlers = new();

    public static LocalKindRegistry For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static _ => new LocalKindRegistry());

    public void Register(string kind, ILocalResourceKind handler)
    {
        if (string.Equals(kind, LocalKinds.Dotnet, StringComparison.Ordinal))
        {
            throw new ServiceSourcesConfigurationException(
                "Local kind 'dotnet' is reserved for the built-in project resolution and cannot be registered via AddLocalKind.");
        }

        if (!_handlers.TryAdd(kind, handler))
        {
            throw new ServiceSourcesConfigurationException(
                $"Local kind '{kind}' is already registered. Call AddLocalKind for a given kind at most once.");
        }
    }

    public bool TryGet(string kind, out ILocalResourceKind? handler) =>
        _handlers.TryGetValue(kind, out handler);

    /// <summary>
    /// Returns a trailing-space-terminated sentence naming the registered kind (or the built-in
    /// <c>"dotnet"</c>) that <paramref name="kind"/> differs from only by case, or an empty string
    /// when there is no such near match. Kind names are matched exactly — a casing slip would
    /// otherwise report only that the kind "is not registered", which sends the reader looking for
    /// a missing package instead of a typo.
    /// </summary>
    public string DescribeNearMatch(string kind)
    {
        var candidates = _handlers.Keys.Append(LocalKinds.Dotnet);
        var match = candidates.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
        return match is null ? "" : $"Kind names are case-sensitive — did you mean '{match}'? ";
    }
}
