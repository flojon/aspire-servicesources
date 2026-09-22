using System.Runtime.CompilerServices;
using Aspire.Hosting.ServiceSources.Java;
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Per-builder registry of <see cref="ILocalResourceKind"/> handlers, keyed by the <c>kind</c>
/// name a service's <c>servicesources.yaml</c> entry declares. Populated via
/// <see cref="ServiceSourcesBuilderExtensions.AddLocalKind"/>, consulted by the <c>"local"</c>
/// source for any kind other than the built-in <c>"dotnet"</c>.
/// </summary>
/// <remarks>
/// <c>"java"</c> and <c>"javascript"</c> are built in too, the same way <c>"dotnet"</c> is: unlike
/// <c>dotnet</c> they still go through <see cref="ILocalResourceKind"/> (they need no service-level
/// metadata the interface doesn't expose), so <see cref="TryGet"/> falls back to a default instance
/// for either name when nothing was registered for it — <c>UseJava()</c>/<c>UseJavaScript()</c> are
/// obsolete no-ops that register this exact same default. Both handler types live unconditionally in
/// this assembly (see <c>MissingHostingPackageTests</c>'s own framing of this post-#187), so
/// constructing a default costs nothing until a service of that kind actually resolves. Calling
/// <c>AddLocalKind</c> explicitly for either name still works — e.g. to substitute a test double —
/// and takes priority over the fallback, since it is checked first.
/// </remarks>
internal sealed class LocalKindRegistry
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, LocalKindRegistry> Cache = new();

    private static readonly Lazy<ILocalResourceKind> DefaultJava = new(() => new JavaLocalResourceKind());
    private static readonly Lazy<ILocalResourceKind> DefaultJavaScript = new(() => new JavaScriptLocalKind());

    private readonly Dictionary<string, ILocalResourceKind> _handlers = new();

    public static LocalKindRegistry For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static _ => new LocalKindRegistry());

    public void Register(string kind, ILocalResourceKind handler)
    {
        if (string.Equals(kind, LocalKinds.Dotnet, StringComparison.Ordinal))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Local kind 'dotnet' is reserved for the built-in project resolution and cannot be registered via AddLocalKind.");
        }

        // The reserved-name collision is a yaml document-shape constraint (#73): a kind's options
        // block sits at the same nesting level as a service's other yaml properties, so a name
        // matching one of those is ambiguous only for a service that actually names this kind from
        // its own yaml entry. Registering the handler is not itself that — an AppHost-wide gate here
        // used to reject a name just because *some* yaml catalog existed, even for a service that
        // never referenced this kind (#133) — so the check now lives where the collision would
        // actually occur: ServiceCatalogLoader.Load, scoped to the one service using it.
        if (!_handlers.TryAdd(kind, handler))
        {
            throw ServiceSourcesConfigurationException.For(
                $"Local kind '{new Name(kind)}' is already registered. Call AddLocalKind for a given kind at most once.");
        }
    }

    public bool TryGet(string kind, out ILocalResourceKind? handler)
    {
        if (_handlers.TryGetValue(kind, out handler))
        {
            return true;
        }

        handler = kind switch
        {
            JavaLocalResourceKind.KindName => DefaultJava.Value,
            JavaScriptLocalKind.KindName => DefaultJavaScript.Value,
            _ => null,
        };
        return handler is not null;
    }

    /// <summary>
    /// Returns a trailing-space-terminated sentence naming the registered kind (or one of the
    /// built-in <c>"dotnet"</c>/<c>"java"</c>/<c>"javascript"</c> kinds) that <paramref name="kind"/>
    /// differs from only by case, or an empty string when there is no such near match. Kind names
    /// are matched exactly — a casing slip would otherwise report only that the kind "is not
    /// registered", which sends the reader looking for a missing package instead of a typo.
    /// </summary>
    public Raw DescribeNearMatch(string kind)
    {
        var candidates = _handlers.Keys
            .Append(LocalKinds.Dotnet)
            .Append(JavaLocalResourceKind.KindName)
            .Append(JavaScriptLocalKind.KindName);
        var match = candidates.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
        return match is null ? default : Raw.Compose($"Kind names are case-sensitive — did you mean '{new Name(match)}'? ");
    }
}
