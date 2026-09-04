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
    /// <remarks>
    /// Handed on as written, so the address has to be one reached from outside Aspire. The case
    /// that catches people out is pointing this at a container the same AppHost runs: the host and
    /// port the dashboard and <c>aspire describe</c> report for a container endpoint belong to
    /// Aspire's endpoint proxy, which exists only while that AppHost runs and is reassigned on the
    /// next start — not to the container's own published port. Nothing here can detect that; the
    /// value is opaque to this source by design.
    /// </remarks>
    public string? ConnectionString { get; set; }
}
