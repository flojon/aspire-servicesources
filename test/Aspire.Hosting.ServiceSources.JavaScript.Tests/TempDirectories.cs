using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

/// <summary>
/// Redirects every test's temp directory under one common root, and removes this process's share
/// of it when the process exits — including a run that got killed, via an opportunistic sweep of
/// any sibling left behind by a process that is no longer running.
/// </summary>
internal static class TempDirectories
{
    public static readonly string CommonRoot = Path.Combine(Path.GetTempPath(), "aspire-servicesources-tests");

    public static readonly string ProcessRoot =
        Path.Combine(CommonRoot, $"{Environment.ProcessId}-{Path.GetRandomFileName()}");

    [ModuleInitializer]
    internal static void Initialize()
    {
        SweepOrphanedProcessRoots();
        Directory.CreateDirectory(ProcessRoot);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Remove(ProcessRoot);
    }

    public static DirectoryInfo CreateSubdirectory(string prefix = "") =>
        Directory.CreateDirectory(Path.Combine(ProcessRoot, $"{prefix}{Path.GetRandomFileName()}"));

    /// <summary>
    /// Removes a sibling per-process subfolder left under <see cref="CommonRoot"/> by a process
    /// that is no longer running — the case <c>ProcessExit</c> misses.
    /// </summary>
    internal static void SweepOrphanedProcessRoots()
    {
        if (!Directory.Exists(CommonRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(CommonRoot))
        {
            if (string.Equals(directory, ProcessRoot, StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileName(directory);
            var dash = name.IndexOf('-');

            if (dash <= 0 || !int.TryParse(name[..dash], out var processId) || IsRunning(processId))
            {
                continue;
            }

            Remove(directory);
        }
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
}
