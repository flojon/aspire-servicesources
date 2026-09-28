namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for the <c>"repository"</c> source, read from a service entry's
/// <c>repository</c> block — or its deprecated alias, <c>local</c> (<see
/// cref="ServiceDeveloperConfig.ReconcileRepositoryAlias"/>). Bound only when that is the entry's
/// effective source.
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

    /// <summary>
    /// Whether the developer wrote anything in this block at all — the same question
    /// <see cref="PrepareDeveloperConfig.IsDeclared"/> answers for the block nested inside it, asked
    /// here so <see cref="ServiceDeveloperConfig.ReconcileRepositoryAlias"/> can tell "wrote nothing
    /// under 'repository' or 'local'" from "wrote one of them", which decides whether the alias
    /// notice fires and whether writing both is a conflict.
    /// </summary>
    public bool IsDeclared => Path is not null || Ref is not null || Prepare?.IsDeclared == true;
}
