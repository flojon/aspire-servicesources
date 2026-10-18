namespace Aspire.Hosting.ServiceSources.Git;

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
/// </remarks>
internal sealed class ScratchDirectory : IDisposable
{
    /// <summary>
    /// How long a directory with no lock file (left by a version that predates it) must go untouched.
    /// </summary>
    private static readonly TimeSpan UnmarkedAbandonedAge = TimeSpan.FromHours(24);

    private const string Prefix = ".incoming-";
    private const string LockSuffix = ".lock";

    private FileStream? _lock;

    private ScratchDirectory(string path, FileStream lockStream)
    {
        Path = path;
        _lock = lockStream;
    }

    /// <summary>The directory to clone into. Not created: <c>git clone</c> creates it.</summary>
    public string Path { get; }

    /// <summary>
    /// Claims an unused name under <paramref name="checkoutsRoot"/>.
    /// </summary>
    /// <param name="nextId">Source of the 8-character name suffix; replaceable to force collisions.</param>
    public static ScratchDirectory Create(string checkoutsRoot, Func<string>? nextId = null)
    {
        nextId ??= () => Guid.NewGuid().ToString("N")[..8];

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
                    path + LockSuffix, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                return new ScratchDirectory(path, stream);
            }
            catch (IOException) when (File.Exists(path + LockSuffix))
            {
                // Taken by another clone, or a stale lock file; try another name.
            }
        }
    }

    /// <summary>
    /// Removes staging directories whose owner is gone. Best effort: failing to sweep must never fail
    /// resolution.
    /// </summary>
    public static void SweepAbandoned(string checkoutsRoot)
    {
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(checkoutsRoot, Prefix + "*"))
            {
                try
                {
                    SweepOne(directory);
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

    private static void SweepOne(string directory)
    {
        var lockPath = directory + LockSuffix;
        if (!File.Exists(lockPath))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) >= UnmarkedAbandonedAge)
            {
                DeleteTree(directory);
            }

            return;
        }

        FileStream stale;
        try
        {
            stale = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            // Another process holds it: a live clone.
            return;
        }

        using (stale)
        {
            DeleteTree(directory);
        }
    }

    public void Dispose()
    {
        if (_lock is null)
        {
            return;
        }

        try
        {
            DeleteTree(Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leaked directory costs disk in a gitignored tree. Releasing the lock
            // below hands it to the next sweep, which retries.
        }
        finally
        {
            _lock.Dispose();
            _lock = null;
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
