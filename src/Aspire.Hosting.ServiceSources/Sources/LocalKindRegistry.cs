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
        if (kind == "dotnet")
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
}
