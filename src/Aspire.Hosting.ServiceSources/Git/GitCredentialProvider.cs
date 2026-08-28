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
    private int _resolvedACredential;

    public GitCredentialProvider(CredentialsHandler next) =>
        Handler = (url, usernameFromUrl, types) =>
        {
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
    /// Whether no username/password was ever handed to libgit2. Only meaningful once the operation
    /// has finished: a clone of a public repository never invokes the callback at all, which leaves
    /// this true without anything having gone wrong.
    /// </summary>
    public bool ResolvedNoCredentials => Volatile.Read(ref _resolvedACredential) == 0;
}
