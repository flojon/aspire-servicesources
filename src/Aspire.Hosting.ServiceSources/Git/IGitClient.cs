namespace Aspire.Hosting.ServiceSources.Git;

internal interface IGitClient
{
    /// <summary>
    /// Fails with a <see cref="ServiceSourcesConfigurationException"/> naming what is missing if
    /// this client cannot work on this machine. Called as a pre-flight, before any network work, so
    /// an unusable git is reported once and up front rather than as a clone failure per service.
    /// </summary>
    /// <remarks>
    /// Cheap and idempotent — it runs on every <c>"local"</c> resolution. Defaulted to a no-op so
    /// the test doubles that stand in for a real git don't each have to say they need nothing.
    /// </remarks>
    void EnsureAvailable()
    {
    }

    /// <summary>
    /// Clones <paramref name="repositoryUrl"/> into <paramref name="destinationPath"/>.
    /// </summary>
    /// <remarks>
    /// Implementations are responsible for authenticating the request so that private
    /// repositories work without per-repository configuration: resolve credentials from the local
    /// git installation's own credential helper first, then fall back to the
    /// <c>SERVICESOURCES_GIT_USERNAME</c>/<c>SERVICESOURCES_GIT_TOKEN</c> environment variables.
    /// <para>
    /// A failure that looks like a rejected or missing credential must be reported as
    /// <see cref="GitAuthenticationFailedException"/> so callers can name authentication as the
    /// likely cause.
    /// </para>
    /// </remarks>
    /// <param name="progress">
    /// Where to report the clone's own progress as it runs, or <see langword="null"/> when nobody is
    /// watching. Only <see cref="Clone"/> takes one: it is the operation that can take minutes on a
    /// first run, and the only one whose wait a developer is shown (see
    /// <see cref="Sources.DeferredCheckout"/>).
    /// </param>
    void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null);

    void Checkout(string repositoryPath, string reference);

    /// <summary>
    /// Fetches all refs from the "origin" remote into the local clone at
    /// <paramref name="repositoryPath"/>. A no-op if no "origin" remote is configured.
    /// Authenticates and reports failures under the same contract as
    /// <see cref="Clone"/>.
    /// </summary>
    void Fetch(string repositoryPath);

    /// <summary>
    /// Returns <see langword="true"/> if the working tree at <paramref name="repositoryPath"/>
    /// has any uncommitted modification (staged or unstaged) to a tracked file. Untracked files
    /// (e.g. build output) do not count.
    /// </summary>
    bool HasUncommittedChanges(string repositoryPath);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="reference"/> resolves, using only
    /// local data (no network), to the same commit currently checked out at HEAD.
    /// </summary>
    bool IsRefCheckedOut(string repositoryPath, string reference);

    /// <summary>
    /// Returns the URL of the "origin" remote for the repository already checked out at
    /// <paramref name="repositoryPath"/>, or <see langword="null"/> if it cannot be determined
    /// (e.g. no "origin" remote is configured). Never performs any network operation.
    /// </summary>
    string? GetOriginUrl(string repositoryPath);
}
