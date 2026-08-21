namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Rejects repository URLs that the bundled LibGit2Sharp native binaries cannot handle, so
/// resolution fails with a clear message instead of an opaque clone failure.
/// </summary>
internal static class GitUrlValidator
{
    public static void EnsureSupported(string serviceName, string repositoryUrl)
    {
        if (GitUrl.Parse(repositoryUrl).IsSsh)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': repository '{repositoryUrl}' is an SSH URL. LibGit2Sharp's " +
                "bundled native binaries do not include an SSH transport, so this URL cannot be cloned. " +
                "Use the repository's HTTPS URL instead (e.g. 'https://host/org/repo').");
        }
    }
}
