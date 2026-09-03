namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for a backing service's <c>"direct"</c> source, read from the
/// <c>direct</c> block of its entry. Bound only when that is the entry's effective source.
/// </summary>
/// <remarks>
/// "Direct" is about the absence of a tunnel, not about where the thing runs: a Postgres the
/// developer started by hand on <c>localhost</c> and a cluster database reachable through an
/// ingress are the same case from the AppHost's side — an address to connect to, and no process to
/// manage.
/// </remarks>
internal sealed class DirectDeveloperConfig
{
    /// <summary>
    /// The connection string to hand consumers, optionally carrying placeholders. Required by this
    /// source: it is the whole of what the source supplies.
    /// </summary>
    public string? ConnectionString { get; set; }
}
