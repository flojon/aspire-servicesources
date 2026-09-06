using System.ComponentModel;
using System.Diagnostics;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Redirects every test's temp directory under one common root, and removes this process's share
/// of it when the process exits — including a run that got killed, via an opportunistic sweep of
/// any sibling left behind by a process that is no longer running.
/// </summary>
/// <remarks>
/// Linked into the Java and JavaScript test projects rather than duplicated — see their
/// <c>.csproj</c> files.
/// </remarks>
internal static class TempDirectories
{
    public static readonly string CommonRoot = Path.Combine(Path.GetTempPath(), "aspire-servicesources-tests");

    public static readonly string ProcessRoot =
        Path.Combine(CommonRoot, $"{Environment.ProcessId}-{Path.GetRandomFileName()}");

    static TempDirectories()
    {
        SweepOrphanedProcessRoots();
        CreateDirectory(ProcessRoot);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Remove(ProcessRoot);
    }

    public static DirectoryInfo CreateSubdirectory(string prefix = "") =>
        CreateDirectory(Path.Combine(ProcessRoot, $"{prefix}{Path.GetRandomFileName()}"));

    /// <summary>
    /// Removes a sibling per-process subfolder left under <see cref="CommonRoot"/> by a process
    /// that is no longer running — the case <c>ProcessExit</c> misses.
    /// </summary>
    internal static void SweepOrphanedProcessRoots()
    {
        string[] siblings;

        try
        {
            // A snapshot, not EnumerateDirectories: a concurrently-sweeping sibling process can
            // delete an entry out from under a lazy enumerator's own MoveNext(), which would
            // throw from outside the per-sibling try/catch below and, since this whole method
            // runs from the static constructor, take the entire test run down with it.
            siblings = Directory.Exists(CommonRoot) ? Directory.GetDirectories(CommonRoot) : [];
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var directory in siblings)
        {
            try
            {
                SweepOne(directory);
            }
            catch (IOException)
            {
                // A sibling process finished sweeping (or exited) the same folder concurrently.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void SweepOne(string directory)
    {
        if (string.Equals(directory, ProcessRoot, StringComparison.Ordinal))
        {
            return;
        }

        var name = Path.GetFileName(directory);
        var dash = name.IndexOf('-');

        if (dash <= 0 || !int.TryParse(name[..dash], out var processId) || IsRunning(processId))
        {
            return;
        }

        Remove(directory);
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            // Access to the process is restricted rather than the process being gone — a dead
            // process's /proc entry doesn't stick around to be access-denied, so this means it's
            // alive and owned by someone else. Treat it as running: the safe default is to leave
            // the directory rather than risk deleting one a live process still uses.
            return true;
        }
    }

    private static void Remove(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // A checkout the test left a handle on is not worth failing a green run over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Restricts the directory itself to this user on Unix, matching what
    /// <see cref="Directory.CreateTempSubdirectory(string)"/> does for the leaf it creates — this
    /// lives under a fixed, predictable, shared path, so it doesn't get to rely on an unguessable
    /// name the way the BCL method does. Only the leaf: <see cref="CommonRoot"/> itself, created
    /// implicitly as a parent directory the first time any process needs it, keeps the OS default
    /// mode, so its child names (process ids, nothing else) are listable by other local users even
    /// though no process's own contents are.
    /// </summary>
    private static DirectoryInfo CreateDirectory(string path) =>
        OperatingSystem.IsWindows()
            ? Directory.CreateDirectory(path)
            : Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
}
