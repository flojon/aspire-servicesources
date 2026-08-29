using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// A credentials callback paired with what it turned out to know. libgit2 only reports the failure
/// it saw at the end of the handshake, which cannot distinguish "the host refused this token" from
/// "there was never a token to offer" — so the callback records which of the two happened as it
/// hands credentials out, and the caller reads it when the operation fails.
/// </summary>
internal sealed class GitCredentialProvider
{
    private int _handlerInvoked;
    private int _resolvedACredential;

    public GitCredentialProvider(CredentialsHandler next) =>
        Handler = (url, usernameFromUrl, types) =>
        {
            Interlocked.Exchange(ref _handlerInvoked, 1);

            var credentials = next(url, usernameFromUrl, types);

            // Only a real username/password counts. DefaultCredentials is what the ladder falls
            // through to once it has nothing left, so treating it as "resolved" would erase the
            // very distinction this class exists to keep.
            if (credentials is UsernamePasswordCredentials)
            {
                Interlocked.Exchange(ref _resolvedACredential, 1);
            }

            return credentials;
        };

    public CredentialsHandler Handler { get; }

    /// <summary>
    /// Whether libgit2 asked this callback for a credential and it had none to hand over. Only
    /// meaningful once the operation has finished.
    /// </summary>
    /// <remarks>
    /// Both halves are load-bearing. libgit2 invokes the credentials callback only once a host
    /// answers with an authentication challenge, so a failure that never got that far leaves the
    /// callback untouched: a proxy answering the first unauthenticated request with 403, or a host
    /// that serves anonymously answering a mistyped repository path with 404, both reach the
    /// caller's failure detection without a single credential having been asked for. Reporting
    /// those as "no credential was resolved" would blame the developer's credential store for a
    /// failure it had no part in — so an uninvoked callback reports false, and the caller falls
    /// back to wording that leaves both readings open.
    /// </remarks>
    public bool ResolvedNoCredentials =>
        Volatile.Read(ref _handlerInvoked) == 1 && Volatile.Read(ref _resolvedACredential) == 0;
}
