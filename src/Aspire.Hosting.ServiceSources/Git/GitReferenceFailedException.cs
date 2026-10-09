using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// A clone that failed because of the reference repository it borrowed objects from, so the caller
/// can retry without one instead of retrying every failure.
/// </summary>
internal sealed class GitReferenceFailedException(string message, Exception innerException)
    : Exception(message, innerException)
{
#pragma warning disable RS0030
    internal static GitReferenceFailedException For(ServiceTextHandler message, Exception innerException) =>
        new(message.Text, innerException);
#pragma warning restore RS0030
}
