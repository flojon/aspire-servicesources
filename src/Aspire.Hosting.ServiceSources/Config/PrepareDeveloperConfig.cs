namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// A developer's own settings for the <c>prepare</c> step, read from the <c>prepare</c> block inside
/// a service's <c>local</c> block. Merged over the catalog's block per field.
/// </summary>
/// <remarks>
/// This is where a developer disables an inherited step (<c>{"mode": "never"}</c>), forces it to
/// re-run on every start (<c>{"mode": "always"}</c>), or substitutes a command of their own — none
/// of which they should have to edit shared team configuration to do. It is also the <em>only</em>
/// place a step can be declared for a <c>path</c> checkout, which never inherits the catalog's
/// block: what runs in a directory the developer took over has to be what the developer wrote.
/// </remarks>
internal sealed class PrepareDeveloperConfig
{
    /// <inheritdoc cref="PrepareMetadata.Command"/>
    public string[]? Command { get; set; }

    /// <inheritdoc cref="PrepareMetadata.WindowsCommand"/>
    public string[]? WindowsCommand { get; set; }

    /// <inheritdoc cref="PrepareMetadata.Mode"/>
    public string? Mode { get; set; }

    /// <summary>
    /// Whether the developer wrote anything here at all.
    /// </summary>
    /// <remarks>
    /// An empty block is not a declaration. It cannot be told from an absent one by looking — the
    /// configuration binder produces an instance for <c>"prepare": {}</c> and for a block whose only
    /// keys came from a layer that blanked them — and neither says anything about what should run,
    /// so neither inherits the catalog's block on a <c>path</c> service nor silences the notice that
    /// asks for one.
    /// </remarks>
    public bool IsDeclared => Command is not null || WindowsCommand is not null || Mode is not null;
}
