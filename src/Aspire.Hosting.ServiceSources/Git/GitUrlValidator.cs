namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Rejects repository URLs that the bundled LibGit2Sharp native binaries cannot handle, so
/// resolution fails with a clear message instead of an opaque clone failure.
/// </summary>
internal static class GitUrlValidator
{
    public static void EnsureSupported(string serviceName, string repositoryUrl)
    {
        if (IsSshUrl(repositoryUrl))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': repository '{repositoryUrl}' is an SSH URL. LibGit2Sharp's " +
                "bundled native binaries do not include an SSH transport, so this URL cannot be cloned. " +
                "Use the repository's HTTPS URL instead (e.g. 'https://host/org/repo').");
        }
    }

    private static bool IsSshUrl(string repositoryUrl)
    {
        var trimmed = repositoryUrl.Trim();
        if (trimmed.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        // scp-like syntax: user@host:path (e.g. git@github.com:org/repo.git). Requiring '@'
        // avoids misidentifying a Windows drive path (e.g. "C:\repos\orders") as SSH.
        var colonIndex = trimmed.IndexOf(':');
        var slashIndex = trimmed.IndexOf('/');
        return colonIndex > 0
            && (slashIndex < 0 || colonIndex < slashIndex)
            && trimmed[..colonIndex].Contains('@');
    }
}
