namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The <c>javascript:</c> options block of a <c>"local"</c>-sourced service whose catalog entry
/// declares <c>kind: javascript</c>. Deserialized from the service's opaque per-kind yaml block by
/// <see cref="LocalKindConfig.Parse{T}"/>, which rejects any property not defined here — so every
/// name below is part of the package's public config surface even though the type is internal.
/// </summary>
internal sealed class JavaScriptKindOptions
{
    /// <summary>
    /// Which <c>Aspire.Hosting.JavaScript</c> integration runs the app: <c>javascript</c> (the
    /// default), <c>vite</c>, <c>nextjs</c>, <c>node</c>, or <c>bun</c>. See
    /// <see cref="JavaScriptAppTypes"/>.
    /// </summary>
    public string? AppType { get; set; }

    /// <summary>
    /// The directory holding the app's <c>package.json</c>, relative to the repository root.
    /// Defaults to the repository root itself.
    /// </summary>
    public string? AppDirectory { get; set; }

    /// <summary>
    /// The <c>package.json</c> script to run. Left unset, the underlying integration's own default
    /// (<c>dev</c>) applies; for <c>node</c>/<c>bun</c>, leaving it unset runs
    /// <see cref="ScriptPath"/> directly instead of any script.
    /// </summary>
    public string? RunScript { get; set; }

    /// <summary>
    /// The entry-point file the runtime executes directly (e.g. <c>server.js</c>), relative to
    /// <see cref="AppDirectory"/>. Required by — and only meaningful for — the <c>node</c> and
    /// <c>bun</c> app types, whose integrations take a file rather than a script name.
    /// </summary>
    public string? ScriptPath { get; set; }

    /// <summary>
    /// <c>npm</c>, <c>yarn</c>, <c>pnpm</c>, or <c>bun</c>. Selects the package manager used to
    /// install dependencies before the app starts (a fresh clone has no <c>node_modules</c>). Left
    /// unset, the integration's own default applies — npm for most app types, Bun for
    /// <c>appType: bun</c>.
    /// </summary>
    public string? PackageManager { get; set; }

    /// <summary>
    /// The port consumers reach the service on. Left unset, Aspire allocates one.
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// The port the app itself listens on, when it is fixed rather than read from
    /// <see cref="PortEnv"/>. Left unset, Aspire allocates one and the app is expected to honour
    /// <see cref="PortEnv"/>.
    /// </summary>
    public int? TargetPort { get; set; }

    /// <summary>
    /// The environment variable the app reads its listen port from; defaults to <c>PORT</c>. Not
    /// applicable to <c>vite</c>/<c>nextjs</c>, whose integrations bind the dev server's port
    /// themselves.
    /// </summary>
    public string? PortEnv { get; set; }
}
