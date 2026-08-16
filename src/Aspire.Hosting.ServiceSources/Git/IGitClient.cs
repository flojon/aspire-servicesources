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
    /// has any uncommitted modification (staged or unstaged).
    /// </summary>
    bool HasUncommittedChanges(string repositoryPath);

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="reference"/> resolves, using only
    /// local data (no network), to the same commit currently checked out at HEAD.
    /// </summary>
    bool IsRefCheckedOut(string repositoryPath, string reference);
}
