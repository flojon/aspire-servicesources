using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

/// <summary>Records which clones borrowed from a reference, and can make the borrowing fail.</summary>
internal sealed class ScriptedCloneGitClient : IGitClient
{
    private readonly object _gate = new();
    private readonly List<string> _plainClones = [];
    private readonly List<(string Destination, string Reference)> _referenceClones = [];

    /// <summary>What <see cref="GetOriginUrl"/> answers for any checkout, the home one included.</summary>
    public string? HomeOrigin { get; init; }

    public Exception? ReferenceFailure { get; init; }

    public IReadOnlyList<string> PlainClones { get { lock (_gate) { return [.. _plainClones]; } } }

    public IReadOnlyList<(string Destination, string Reference)> ReferenceClones { get { lock (_gate) { return [.. _referenceClones]; } } }

    public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null)
    {
        lock (_gate)
        {
            _plainClones.Add(destinationPath);
        }

        Directory.CreateDirectory(Path.Combine(destinationPath, ".git"));
    }

    public void CloneWithReference(
        string repositoryUrl, string destinationPath, string referenceRepository, IGitProgressSink? progress = null)
    {
        lock (_gate)
        {
            _referenceClones.Add((destinationPath, referenceRepository));
        }

        // What a failed attempt can leave behind, so the fallback has debris to avoid.
        Directory.CreateDirectory(destinationPath);

        if (ReferenceFailure is not null)
        {
            throw ReferenceFailure;
        }

        Directory.CreateDirectory(Path.Combine(destinationPath, ".git"));
    }

    public void Checkout(string repositoryPath, string reference)
    {
    }

    public void Fetch(string repositoryPath)
    {
    }

    public bool HasUncommittedChanges(string repositoryPath) => false;

    public bool IsRefCheckedOut(string repositoryPath, string reference) => true;

    public string? GetOriginUrl(string repositoryPath) => HomeOrigin;
}
