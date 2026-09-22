using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Thrown by <see cref="GitCliClient"/> when a clone or fetch fails in a way that looks
/// like a rejected or missing credential, so callers can produce an error that names
/// authentication as the likely cause instead of a generic clone/fetch failure.
/// </summary>
internal sealed class GitAuthenticationFailedException(
    string message, Exception innerException, bool noCredentialsResolved = false)
    : Exception(message, innerException)
{
    /// <summary>
    /// Whether the operation ran without a single username/password to offer — the credential
    /// helper yielded nothing and no environment token was set — so the only thing sent was the
    /// operating system's integrated credential. That is a different problem from a credential the
    /// host refused, and needs different remediation, so the two are not reported alike.
    /// </summary>
    public bool NoCredentialsResolved { get; } = noCredentialsResolved;

#pragma warning disable RS0030
    /// <summary>
    /// The only way this package builds a message: a raw <c>string</c> hole does not compile, so a
    /// caller-controlled name cannot reach a reader unescaped.
    /// </summary>
    internal static GitAuthenticationFailedException For(
        ServiceTextHandler message, Exception innerException, bool noCredentialsResolved = false) =>
        new(message.Text, innerException, noCredentialsResolved);
#pragma warning restore RS0030
}
