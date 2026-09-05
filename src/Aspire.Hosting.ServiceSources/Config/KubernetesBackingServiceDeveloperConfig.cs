namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for a backing service's <c>"kubernetes"</c> source, read from the
/// <c>kubernetes</c> block of its entry. Bound only when that is the entry's effective source.
/// </summary>
/// <remarks>
/// Separate from <see cref="KubernetesDeveloperConfig"/>, which carries the same source name for a
/// <em>service</em>, because the two describe different things and share only <c>context</c> and
/// <c>namespace</c>. A service reads <c>kubernetes.service</c> and its remote port from the
/// catalog, and needs a <c>scheme</c> so its forwarded endpoint can be named for what the pod
/// serves; a backing service has no catalog entry at all — see the design's <i>Decisions</i> — so
/// it names its own Service and port here, and needs no scheme, because a consumer reaches it by
/// connection string rather than by service discovery.
/// <para>
/// Every field is required except <see cref="Namespace"/>. There is nothing to fall back to: the
/// catalog carries no backing-service data, and a connection string this package invented would be
/// a guess at the dialect, the database name and the credentials all at once.
/// </para>
/// </remarks>
internal sealed class KubernetesBackingServiceDeveloperConfig
{
    /// <summary>
    /// The Kubernetes Service to forward to, by name. Required by this source.
    /// </summary>
    /// <remarks>
    /// A Service rather than a pod, deliberately, and the same choice the service-side source
    /// makes: a pod name carries a replica-set suffix that changes on every rollout, so a
    /// forwarded pod is a value that goes stale between one <c>kubectl get</c> and the next.
    /// <c>kubectl port-forward</c> against a Service picks a backing pod itself.
    /// </remarks>
    public string? Service { get; set; }

    /// <summary>
    /// The port the Service listens on inside the cluster. Required by this source.
    /// </summary>
    /// <remarks>
    /// The <em>remote</em> port. The local end is allocated, not configured, so that two backing
    /// services forwarded at once cannot collide — which is also why a connection string writes
    /// <c>${port}</c> rather than a number: the number is not known until the AppHost starts.
    /// </remarks>
    public KubernetesPorts? Port { get; set; }

    /// <summary>The kubectl context the port-forward runs against. Required by this source.</summary>
    /// <remarks>
    /// Required rather than defaulted to the current context, again as on the service side. The
    /// current context is whatever the developer last pointed <c>kubectl</c> at, so defaulting to
    /// it would make an AppHost's behaviour depend on a shell they may not have opened today — and
    /// the failure would be a connection to the wrong cluster rather than an error.
    /// </remarks>
    public string? Context { get; set; }

    /// <summary>The namespace the Service lives in. Defaults to <c>default</c>.</summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// The connection string to hand consumers, with <c>${port}</c> standing for the local end of
    /// the tunnel. Required by this source.
    /// </summary>
    /// <remarks>
    /// Declared here rather than once at the entry root, even though
    /// <see cref="DirectDeveloperConfig"/> declares a field of the same name, because the two take
    /// different templates: this one addresses the local end of a port-forward, so it carries a
    /// <c>${port}</c> that <c>"direct"</c> has nothing to resolve. A single shared field would let
    /// a developer switch source and keep a template that can only be right for one of them.
    /// </remarks>
    public string? ConnectionString { get; set; }
}
