namespace Aspire.Hosting.ServiceSources.Kubernetes;

/// <summary>
/// A secret fetch that produced no value.
/// </summary>
/// <remarks>
/// Deliberately not a <see cref="ServiceSourcesConfigurationException"/>. That one reports a
/// configuration mistake caught while the AppHost is being composed, and everything it says is
/// true before anything runs. This is the cluster answering at start time: the template parsed,
/// every field was present, and the fetch still did not produce a value. Aspire surfaces it
/// against the parameter that failed to resolve.
/// </remarks>
internal sealed class KubernetesSecretException : Exception
{
    public KubernetesSecretException(string message)
        : base(message)
    {
    }

    public KubernetesSecretException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
