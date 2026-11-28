using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// The per-user root short git directories live in, and the cleanup of ones nothing refers to.
/// </summary>
internal static class SeparateGitDirectories
{
    private const string LockSuffix = ".lock";

    /// <summary>How long a git directory goes unreferenced and unused before the sweep removes it.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);

    /// <summary>
    /// The per-user directory separate git directories live in, or <see langword="null"/> when the
    /// account has no local application data folder (some service accounts).
    /// </summary>
    public static string? DefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "ServiceSources", "git");
    }

    /// <summary>
    /// Deletes git directories that no checkout resolves to and that have gone unused for
    /// <see cref="Retention"/>. Best effort: it never fails the caller.
    /// </summary>
    /// <remarks>
    /// Only a child it enumerated itself is ever deleted, never a path read from an owner record,
    /// which is untrusted data.
    /// </remarks>
    public static void SweepOrphans(string gitRoot, DateTimeOffset now, IGitProgressSink? log = null)
    {
        try
        {
            if (!Directory.Exists(gitRoot))
            {
                return;
            }

            foreach (var child in Directory.EnumerateDirectories(gitRoot))
            {
                try
                {
                    SweepOne(gitRoot, child, now, log);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Held open by something, or not ours to delete. Left for a later run.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The enumeration itself failed, so there is nothing to sweep.
        }
    }

    private static void SweepOne(string gitRoot, string child, DateTimeOffset now, IGitProgressSink? log)
    {
        if (!ManagedGitDirectory.NamePattern.IsMatch(Path.GetFileName(child))
            || new DirectoryInfo(child).Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return;
        }

        var lockPath = child + LockSuffix;
        var hasLock = File.Exists(lockPath);
        if (hasLock && !TryTakeLock(lockPath))
        {
            return;
        }

        var owner = ManagedGitDirectory.ReadOwner(child);
        if (owner is not null)
        {
            if (!IsLocalRootedPath(owner.Checkout))
            {
                return;
            }

            if (IsLive(gitRoot, child, owner.Checkout))
            {
                return;
            }
        }

        var lastUsed = owner?.LastUsedUtc ?? new DateTimeOffset(Directory.GetLastWriteTimeUtc(child), TimeSpan.Zero);
        if (now - lastUsed < Retention)
        {
            return;
        }

        ScratchDirectory.DeleteTree(child);
        if (hasLock)
        {
            File.Delete(lockPath);
        }

        log?.Report(Raw.Compose(
            $"Removed git directory '{Raw.Display(child)}': unreferenced for more than {(int)Retention.TotalDays} days.").ToString());
    }

    // A UNC or relative value is never probed, and a drive that is not mounted proves nothing about the checkout.
    private static bool IsLocalRootedPath(string path)
    {
        if (!PathForms.IsLocalAbsolute(path))
        {
            return false;
        }

        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && Directory.Exists(root);
    }

    private static bool IsLive(string gitRoot, string child, string checkout) =>
        ManagedGitDirectory.Resolve(checkout, gitRoot).GitDirectory is { } resolved
        && string.Equals(
            ManagedGitDirectory.Normalize(resolved), ManagedGitDirectory.Normalize(child), ManagedGitDirectory.PathComparison);

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

    /// <summary>
    /// Keeps a checkout in use from being swept, rewriting the record only when it is over a day old.
    /// </summary>
    public static void RefreshLastUsed(string gitDir, DateTimeOffset now)
    {
        try
        {
            if (ManagedGitDirectory.ReadOwner(gitDir) is { } owner && now - owner.LastUsedUtc > RefreshInterval)
            {
                ManagedGitDirectory.WriteOwner(gitDir, owner with { LastUsedUtc = now });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the worst case is the git directory looking unused a little early.
        }
    }
}
