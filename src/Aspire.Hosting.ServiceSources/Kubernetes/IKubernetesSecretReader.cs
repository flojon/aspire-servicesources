namespace Aspire.Hosting.ServiceSources.Kubernetes;

/// <summary>
/// Reads one value out of a Kubernetes secret.
/// </summary>
/// <remarks>
/// A seam for the same reason <see cref="Git.IGitClient"/> and
/// <see cref="PortAllocation.IPortAllocator"/> are seams: the unit tests for a source that resolves
/// <c>${secret:...}</c> assert the model that source builds, and running <c>kubectl</c> to do that
/// would make them require a cluster to say anything at all.
/// <para>
/// Synchronous because <c>AddParameter</c>'s lazy overload takes a <see cref="Func{T}"/> and offers
/// no asynchronous shape. One fetch therefore blocks one start-time parameter resolution, which is
/// acceptable — but it is why the implementation carries a timeout of its own rather than
/// inheriting whatever <c>kubectl</c> would wait, since a hung fetch on this path stalls a resource
/// with no result to report.
/// </para>
/// </remarks>
internal interface IKubernetesSecretReader
{
    /// <summary>
    /// Fetches <paramref name="key"/> from the secret <paramref name="secretName"/>, decoded from
    /// the base64 the Kubernetes API stores it in.
    /// </summary>
    /// <exception cref="KubernetesSecretException">
    /// The secret or the key does not exist, the fetch failed, or it outlived the timeout. The
    /// message names what was asked for; the caller adds which backing service asked for it.
    /// </exception>
    string Read(string context, string @namespace, string secretName, string key);
}
