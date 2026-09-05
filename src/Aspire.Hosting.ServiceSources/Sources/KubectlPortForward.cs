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
    /// The arguments that forward <paramref name="localPort"/> to <paramref name="remotePort"/> on
    /// the Service <paramref name="service"/>.
    /// </summary>
    /// <param name="namespace">
    /// The namespace, or <see langword="null"/> for <see cref="DefaultNamespace"/>.
    /// </param>
    public static string[] Args(
        string service, int localPort, int remotePort, string context, string? @namespace) =>
        [
            "port-forward",
            $"svc/{service}",
            $"{localPort}:{remotePort}",
            "--context",
            context,
            "--namespace",
            @namespace ?? DefaultNamespace,
        ];
}
