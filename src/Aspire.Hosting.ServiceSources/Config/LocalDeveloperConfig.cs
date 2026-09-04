namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for the <c>"local"</c> source, read from the <c>local</c> block of a
/// service's entry. Bound only when that is the entry's effective source.
/// </summary>
internal sealed class LocalDeveloperConfig
{
    /// <summary>An existing checkout to use as-is, instead of one this tool clones and manages.</summary>
    public string? Path { get; set; }

    /// <summary>The ref a managed checkout sits on. Cannot be combined with <see cref="Path"/>.</summary>
    public string? Ref { get; set; }

    /// <summary>
    /// This developer's own <c>prepare</c> step, merged over the catalog's block per field — or the
    /// whole of the step for a <c>path</c> checkout, which inherits nothing.
    /// </summary>
    /// <remarks>
    /// Nullable, unlike the source blocks on <see cref="ServiceDeveloperConfig"/>, because there is
    /// something for absent to mean here: on a <c>path</c> service "the developer declared no block"
    /// is a different answer from "the developer declared one", and it decides whether a notice asks
    /// them to.
    /// </remarks>
    public PrepareDeveloperConfig? Prepare { get; set; }
}
