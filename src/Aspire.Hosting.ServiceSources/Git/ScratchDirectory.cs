using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Where a clone's separate git directory goes: a short per-user root, named from the checkout's final path.
/// </summary>
internal sealed record SeparateGit(string GitRoot, string FinalCheckoutPath);

/// <summary>
/// A <c>.incoming-*</c> directory a clone is staged in before being renamed into place, owned for
/// as long as this object lives.
/// </summary>
/// <remarks>
/// <para>
/// The name is deliberately short: git's longest in-repo path is appended to it, and on Windows
/// every character here comes out of the same MAX_PATH budget.
/// </para>
/// <para>
/// Ownership is a sibling <c>.incoming-*.lock</c> file held open with no sharing for the owner's
/// lifetime. The operating system releases it when the process dies, however it dies, so a sweep
/// can tell a killed clone from a running one by whether it can take the lock — no waiting out a
/// timer, and no risk to a concurrent clone.
/// </para>
/// <para>
/// A clone with a separate git directory pairs one, named from the same id and locked the same way
/// in the git root. The working tree is moved into place on success; the git directory stays.
/// </para>
/// </remarks>
internal sealed class ScratchDirectory : IDisposable
{
    /// <summary>
    /// How long a directory with no lock file (left by a version that predates it) must go untouched.
    /// </summary>
    private static readonly TimeSpan UnmarkedAbandonedAge = TimeSpan.FromHours(24);

    internal const string Prefix = ".incoming-";
    internal const int IdLength = 8;
    private const string LockSuffix = ".lock";

    private readonly string _checkoutsRoot;
    private readonly Func<string> _nextId;
    private FileStream? _lock;
    private FileStream? _gitLock;
    private bool _gitAdopted;

    private ScratchDirectory(string checkoutsRoot, Func<string> nextId, string path, FileStream lockStream)
    {
        _checkoutsRoot = checkoutsRoot;
        _nextId = nextId;
        Path = path;
        _lock = lockStream;
    }

    /// <summary>The directory to clone into. Not created: <c>git clone</c> creates it.</summary>
    public string Path { get; private set; }

    /// <summary>The separate git directory paired with this scratch, or <see langword="null"/> for an in-place clone.</summary>
    public string? GitDirectory { get; private set; }

    private string Id => System.IO.Path.GetFileName(Path)[Prefix.Length..];

    /// <summary>
    /// Claims an unused name under <paramref name="checkoutsRoot"/>.
    /// </summary>
    /// <param name="nextId">Source of the 8-character name suffix; replaceable to force collisions.</param>
    public static ScratchDirectory Create(string checkoutsRoot, Func<string>? nextId = null)
    {
        nextId ??= () => Guid.NewGuid().ToString("N")[..IdLength];

        // Creating the lock with CreateNew is the atomic claim, so two clones can never pick one name.
        while (true)
        {
            var path = System.IO.Path.Combine(checkoutsRoot, Prefix + nextId());
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            try
            {
                var stream = new FileStream(
                    path + LockSuffix, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1);
                return new ScratchDirectory(checkoutsRoot, nextId, path, stream);
            }
            catch (IOException) when (File.Exists(path + LockSuffix))
            {
                // Taken by another clone, or a stale lock file; try another name.
            }
        }
    }

    /// <summary>
    /// Claims a git directory name in the git root for this clone, locked like the staging directory
    /// so a sweep from another AppHost can tell it from an abandoned one. <c>git clone</c> creates
    /// the directory itself.
    /// </summary>
    /// <param name="nextId">Source of a replacement id when the git directory's name is already taken.</param>
    public void PairGitDirectory(SeparateGit separate, Func<string>? nextId = null)
    {
        nextId ??= _nextId;
        Directory.CreateDirectory(separate.GitRoot);

        while (true)
        {
            var gitDirectory = System.IO.Path.Combine(
                separate.GitRoot, ManagedGitDirectory.NameFor(separate.FinalCheckoutPath, Id));
            if (!Directory.Exists(gitDirectory) && !File.Exists(gitDirectory))
            {
                try
                {
                    _gitLock = new FileStream(
                        gitDirectory + LockSuffix, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1);
                    GitDirectory = gitDirectory;
                    return;
                }
                catch (IOException) when (File.Exists(gitDirectory + LockSuffix))
                {
                    // Taken by another clone, or a stale lock file; take another id.
                }
            }

            // The staging name embeds the id, so a new id means a new staging lock too.
            var replacement = Create(_checkoutsRoot, nextId);
            var releasedLock = _lock;
            var releasedPath = Path;
            Path = replacement.Path;
            _lock = replacement._lock;
            replacement._lock = null;
            releasedLock?.Dispose();
            TryDeleteLockFile(releasedPath + LockSuffix);
        }
    }

    /// <summary>The clone succeeded, so <see cref="Dispose"/> must leave the git directory in place.</summary>
    public void AdoptGitDirectory() => _gitAdopted = true;

    /// <summary>
    /// Removes staging directories whose owner is gone. Best effort: failing to sweep must never fail
    /// resolution.
    /// </summary>
    /// <param name="log">Told of each leftover removed, or <see langword="null"/> when nobody is watching.</param>
    /// <param name="gitRoot">Where separate git directories live; a removed staging directory's paired one goes with it.</param>
    public static void SweepAbandoned(string checkoutsRoot, IGitProgressSink? log = null, string? gitRoot = null)
    {
        try
        {
            foreach (var orphan in Directory.EnumerateFiles(checkoutsRoot, Prefix + "*" + LockSuffix))
            {
                if (!Directory.Exists(orphan[..^LockSuffix.Length]))
                {
                    SweepOrphanLock(orphan);
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(checkoutsRoot, Prefix + "*"))
            {
                try
                {
                    if (SweepOne(directory))
                    {
                        SweepPairedGitDirectory(gitRoot, System.IO.Path.GetFileName(directory)[Prefix.Length..]);
                        log?.Report(Raw.Compose(
                            $"Removed leftover clone staging directory '{Raw.Display(directory)}' from an earlier clone that did not finish.").ToString());
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held open by something, or not ours to delete. Leave it for a later run.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The enumeration itself failed, so there is nothing we can sweep.
        }
    }

    // The id is spliced into a search pattern, so anything but the generated shape must not reach it.
    private static bool IsStagingId(string id) => id.Length == 8 && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static void SweepPairedGitDirectory(string? gitRoot, string id)
    {
        if (gitRoot is null || !Directory.Exists(gitRoot) || !IsStagingId(id))
        {
            return;
        }

        try
        {
            foreach (var gitDirectory in Directory.EnumerateDirectories(gitRoot, "*-" + id + ".git"))
            {
                if (!ManagedGitDirectory.NamePattern.IsMatch(System.IO.Path.GetFileName(gitDirectory))
                    || new DirectoryInfo(gitDirectory).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var lockPath = gitDirectory + LockSuffix;
                if (File.Exists(lockPath) && !TryTakeLock(lockPath))
                {
                    continue;
                }

                DeleteTree(gitDirectory);
                File.Delete(lockPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the orphan sweep.
        }
    }

    private static bool TryTakeLock(string lockPath)
    {
        try
        {
            using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1))
            {
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void SweepOrphanLock(string lockPath)
    {
        try
        {
            // Opening exclusively first proves no clone owns it; one between claiming and cloning does.
            using (new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1))
            {
            }

            File.Delete(lockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by a live clone, or already gone.
        }
    }

    /// <returns>Whether the directory was removed.</returns>
    private static bool SweepOne(string directory)
    {
        var lockPath = directory + LockSuffix;
        if (!File.Exists(lockPath))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) >= UnmarkedAbandonedAge)
            {
                DeleteTree(directory);
                return true;
            }

            return false;
        }

        FileStream stale;
        try
        {
            stale = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1);
        }
        catch (IOException)
        {
            // Another process holds it: a live clone.
            return false;
        }

        using (stale)
        {
            DeleteTree(directory);
        }

        File.Delete(lockPath);
        return true;
    }

    public void Dispose()
    {
        if (_lock is null)
        {
            return;
        }

        var removed = false;
        try
        {
            DeleteTree(Path);
            removed = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leaked directory costs disk in a gitignored tree. Keeping the lock
            // file, now unheld, hands the directory to the next sweep immediately.
        }
        finally
        {
            _lock.Dispose();
            _lock = null;
        }

        if (removed)
        {
            TryDeleteLockFile(Path + LockSuffix);
        }

        DisposeGitDirectory();
    }

    private void DisposeGitDirectory()
    {
        if (GitDirectory is null)
        {
            return;
        }

        var removed = _gitAdopted;
        if (!_gitAdopted)
        {
            try
            {
                DeleteTree(GitDirectory);
                removed = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Left, with its lock unheld, for the next sweep.
            }
        }

        _gitLock?.Dispose();
        _gitLock = null;
        if (removed)
        {
            TryDeleteLockFile(GitDirectory + LockSuffix);
        }
    }

    private static void TryDeleteLockFile(string lockPath)
    {
        try
        {
            File.Delete(lockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unheld lock beside no directory is inert; the sweep removes it.
        }
    }

    /// <summary>
    /// Deletes a tree git populated: pack files are read-only, which makes a plain recursive delete
    /// fail with access denied on Windows. Long paths need no handling here because .NET addresses
    /// Windows paths in extended form.
    /// </summary>
    internal static void DeleteTree(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, recursive: true);
            return;
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException) when (Directory.Exists(directory))
        {
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(directory, recursive: true);
    }
}
