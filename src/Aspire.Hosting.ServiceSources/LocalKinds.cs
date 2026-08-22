namespace Aspire.Hosting.ServiceSources;

internal static class LocalKinds
{
    /// <summary>
    /// The built-in local kind, resolved directly by the <c>"local"</c> source rather than through
    /// an <see cref="ILocalResourceKind"/> handler: it needs the service's top-level
    /// <c>project</c> metadata (which the handler interface deliberately doesn't expose) and its
    /// project-file lookup runs in the parallel, failure-aggregating phase of resolution.
    /// </summary>
    public const string Dotnet = "dotnet";
}
