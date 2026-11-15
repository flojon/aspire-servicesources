using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Tests.Worktrees;

/// <summary>Records which clones borrowed from a reference, and can make the borrowing fail.</summary>
internal sealed class ScriptedCloneGitClient : IGitClient
{
    private readonly object _gate = new();
    private readonly List<string> _plainClones = [];
    private readonly List<(string Destination, string Reference)> _referenceClones = [];
    private readonly List<string> _separateGitDirs = [];

    /// <summary>What <see cref="GetOriginUrl"/> answers for any checkout, the home one included.</summary>
    public string? HomeOrigin { get; init; }

    public Exception? ReferenceFailure { get; init; }

    /// <summary>Thrown by <see cref="GetOriginUrl"/>, as a corrupt or foreign-owned home checkout would.</summary>
    public Exception? OriginFailure { get; init; }

    /// <summary>The git directory each attempt was told to use, in order, for the attempts that had one.</summary>
    public IReadOnlyList<string> SeparateGitDirs { get { lock (_gate) { return [.. _separateGitDirs]; } } }

    public IReadOnlyList<string> PlainClones { get { lock (_gate) { return [.. _plainClones]; } } }

    public IReadOnlyList<(string Destination, string Reference)> ReferenceClones { get { lock (_gate) { return [.. _referenceClones]; } } }

    public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null, string? separateGitDir = null)
    {
        lock (_gate)
        {
            _plainClones.Add(destinationPath);
        }

        if (separateGitDir is not null)
        {
            lock (_gate)
            {
                _separateGitDirs.Add(separateGitDir);
            }
        }

        CreateGit(destinationPath, separateGitDir);
    }

    public void CloneWithReference(
        string repositoryUrl, string destinationPath, string referenceRepository, IGitProgressSink? progress = null,
        string? separateGitDir = null)
    {
        lock (_gate)
        {
            _referenceClones.Add((destinationPath, referenceRepository));
        }

        // What a failed attempt can leave behind, so the fallback has debris to avoid.
        Directory.CreateDirectory(destinationPath);
        if (separateGitDir is not null)
        {
            Directory.CreateDirectory(separateGitDir);
            lock (_gate)
            {
                _separateGitDirs.Add(separateGitDir);
            }
        }

        if (ReferenceFailure is not null)
        {
            throw ReferenceFailure;
        }

        CreateGit(destinationPath, separateGitDir);
    }

    public void Checkout(string repositoryPath, string reference)
    {
    }

    public void Fetch(string repositoryPath)
    {
    }

    public bool HasUncommittedChanges(string repositoryPath) => false;

    public bool IsRefCheckedOut(string repositoryPath, string reference) => true;

    public string? GetOriginUrl(string repositoryPath) => OriginFailure is null ? HomeOrigin : throw OriginFailure;

    private static void CreateGit(string destinationPath, string? separateGitDir)
    {
        if (separateGitDir is null)
        {
            Directory.CreateDirectory(Path.Combine(destinationPath, ".git"));
            return;
        }

        Directory.CreateDirectory(separateGitDir);
        Directory.CreateDirectory(destinationPath);
        File.WriteAllText(Path.Combine(destinationPath, ".git"), $"gitdir: {separateGitDir.Replace('\\', '/')}\n");
    }
}
