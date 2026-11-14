namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The <c>kubectl port-forward</c> command line, in one place, for the two things this package
/// tunnels: a service (<see cref="KubernetesSource"/>) and a backing service
/// (<see cref="BackingServices.KubernetesBackingServiceSource"/>).
/// </summary>
/// <remarks>
/// Shared because the arguments are the same command whichever kind of thing is behind them, and
/// because they are the one part of either source a test cannot check by observing behaviour — no
/// unit test runs <c>kubectl</c>, so both sources' coverage of this is an assertion about a string
/// array, and two arrays asserted separately drift.
/// </remarks>
internal static class KubectlPortForward
{
    /// <summary>The namespace a port-forward runs in when the developer names none.</summary>
    /// <remarks>
    /// <c>kubectl</c>'s own default is the context's configured namespace rather than
    /// <c>default</c>, so this is a deliberate departure: the context is config here, and letting
    /// the namespace come from the developer's kubeconfig would make an AppHost's behaviour depend
    /// on a <c>kubectl config set-context --current --namespace=…</c> nobody recorded.
    /// </remarks>
    public const string DefaultNamespace = "default";

    /// <summary>
    /// How long one probe of the tunnel's local port may take before it counts as a failure.
    /// </summary>
    /// <remarks>
    /// Set explicitly because <c>AddCheck</c>'s instance overload leaves the registration's timeout
    /// infinite, and a connect that <em>hangs</em> rather than refuses would then stall that
    /// resource's monitor loop for the life of the run — no result, and so no report to act on.
    /// Loopback makes that nearly impossible, which is the reason to spend one argument on it
    /// rather than a mechanism: the failure it forecloses is cheap to prevent and silent to hit.
    /// </remarks>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The arguments that forward <paramref name="localPort"/> to <paramref name="remotePort"/> on
    /// the Service <paramref name="service"/>.
    /// </summary>
    /// <param name="namespace">
    /// The namespace, or <see langword="null"/> for <see cref="DefaultNamespace"/>.
    /// </param>
    public static string[] Args(
        string service, int localPort, int remotePort, string context, string? @namespace) =>
        Args(service, [(localPort, remotePort)], context, @namespace);

    /// <summary>
    /// The arguments that forward every pair in <paramref name="ports"/> against the Service
    /// <paramref name="service"/>, through one invocation.
    /// </summary>
    /// <remarks>
    /// One invocation rather than one per pair, which is a decision and not a convenience:
    /// <c>kubectl port-forward</c> accepts several pairs against one Service from one process, so
    /// two entries would mean two processes and two tunnels to the same Service — measured against a
    /// two-port Service in <c>kind</c>, where a single invocation forwarded both and carried real
    /// traffic on each.
    /// <para>
    /// The order of <paramref name="ports"/> is the caller's, and the caller owes it a deterministic
    /// one: the command line is read in a dashboard beside a connection string, and a pair order
    /// that moved between runs would make the two impossible to check against each other.
    /// </para>
    /// </remarks>
    public static string[] Args(
        string service,
        IReadOnlyList<(int LocalPort, int RemotePort)> ports,
        string context,
        string? @namespace) =>
        [
            "port-forward",
            $"svc/{service}",
            .. ports.Select(pair => $"{pair.LocalPort}:{pair.RemotePort}"),
            "--context",
            context,
            "--namespace",
            @namespace ?? DefaultNamespace,
        ];
}
