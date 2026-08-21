namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Thrown by <see cref="LibGit2SharpGitClient"/> when a clone or fetch fails in a way that looks
/// like a rejected or missing credential, so callers can produce an error that names
/// authentication as the likely cause instead of a generic clone/fetch failure.
/// </summary>
internal sealed class GitAuthenticationFailedException(string message, Exception innerException)
    : Exception(message, innerException);
