namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for the <c>"kubernetes"</c> source, read from the <c>kubernetes</c>
/// block of a service's entry. Bound only when that is the entry's effective source.
/// </summary>
internal sealed class KubernetesDeveloperConfig
{
    /// <summary>The kubectl context the port-forward runs against. Required by this source.</summary>
    public string? Context { get; set; }

    /// <summary>The namespace the service lives in. Defaults to <c>default</c>.</summary>
    public string? Namespace { get; set; }

    /// <summary>The port inside the cluster, overriding the catalog's <c>kubernetes.port</c>.</summary>
    public int? Port { get; set; }

    /// <summary>
    /// The scheme the port-forward's endpoint is named for, overriding the catalog's
    /// <c>kubernetes.scheme</c>. See <see cref="Sources.EndpointScheme"/>.
    /// </summary>
    public string? Scheme { get; set; }
}
