namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Rejects repository URLs that the bundled LibGit2Sharp native binaries cannot handle, so
/// resolution fails with a clear message instead of an opaque clone failure.
/// </summary>
internal static class GitUrlValidator
{
    /// <summary>
    /// Validates a URL taken from a service's configuration, naming the service in the error so
    /// the developer knows which catalog entry to fix.
    /// </summary>
    public static void EnsureSupported(string serviceName, string repositoryUrl) =>
        Ensure($"Service '{serviceName}': repository", repositoryUrl);

    /// <summary>
    /// Validates a URL with no service context available. This is the backstop inside
    /// <see cref="LibGit2SharpGitClient"/> itself — the unsupported transport is a property of that
    /// implementation, so the check belongs there and not only at the call sites that remember it.
    /// Callers that do know the service should use the overload above for a better message.
    /// </summary>
    public static void EnsureSupported(string repositoryUrl) => Ensure("Repository", repositoryUrl);

    private static void Ensure(string subject, string repositoryUrl)
    {
        if (GitUrl.Parse(repositoryUrl).IsSsh)
        {
            throw new ServiceSourcesConfigurationException(
                $"{subject} '{GitUrl.Redact(repositoryUrl)}' is an SSH URL. LibGit2Sharp's bundled native binaries " +
                "do not include an SSH transport, so this URL cannot be cloned. Use the repository's " +
                "HTTPS URL instead (e.g. 'https://host/org/repo').");
        }
    }
}
