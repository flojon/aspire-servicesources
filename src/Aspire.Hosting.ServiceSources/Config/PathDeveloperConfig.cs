namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for the <c>"path"</c> source, read from the <c>path</c> block of a
/// service's entry. Bound only when that is the entry's effective source.
/// </summary>
/// <remarks>
/// Mirrors <see cref="LocalDeveloperConfig"/>'s shape, minus <see cref="LocalDeveloperConfig.Ref"/> —
/// design "<c>ref</c> is not offered": a <c>path</c>-resolved directory is not a separate checkout, so
/// there is no second commit for a ref to name.
/// </remarks>
internal sealed class PathDeveloperConfig
{
    /// <summary>
    /// An existing directory to use instead of the catalog's own <c>path:</c> — unconfined, exactly
    /// like <see cref="LocalDeveloperConfig.Path"/>: it points at the developer's own machine and
    /// directory, so the containment rule a committed <c>path:</c> gets does not apply to it.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// This developer's own <c>prepare</c> step, merged over the catalog's block per field.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="LocalDeveloperConfig.Prepare"/> — which replaces the catalog's block
    /// entirely for a developer-redirected checkout that inherits nothing from a repository it may
    /// not even be a clone of — a catalog-declared <c>path:</c> entry's own <c>prepare:</c> block runs
    /// normally, merged with this per field exactly as a managed <c>"repository"</c> checkout's is:
    /// there is no "someone else's directory" here to protect.
    /// </remarks>
    public PrepareDeveloperConfig? Prepare { get; set; }
}
