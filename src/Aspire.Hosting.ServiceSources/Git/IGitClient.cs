namespace Aspire.Hosting.ServiceSources.Git;

internal interface IGitClient
{
    void Clone(string repositoryUrl, string destinationPath);

    void Checkout(string repositoryPath, string reference);

    /// <summary>
    /// Fetches all refs from the "origin" remote into the local clone at
    /// <paramref name="repositoryPath"/>. A no-op if no "origin" remote is configured.
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
