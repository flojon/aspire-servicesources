namespace Aspire.Hosting.ServiceSources.Git;

internal interface IGitClient
{
    void Clone(string repositoryUrl, string destinationPath);

    void Checkout(string repositoryPath, string reference);

    /// <summary>
    /// Returns the URL of the "origin" remote for the repository already checked out at
    /// <paramref name="repositoryPath"/>, or <see langword="null"/> if it cannot be determined
    /// (e.g. no "origin" remote is configured). Never performs any network operation.
    /// </summary>
    string? GetOriginUrl(string repositoryPath);
}
