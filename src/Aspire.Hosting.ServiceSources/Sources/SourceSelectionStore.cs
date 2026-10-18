using System.Text.Json;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>How <see cref="SourceSelectionStore.Read"/> found <c>selection.json</c>.</summary>
internal enum SelectionFileState
{
    /// <summary>There is no file.</summary>
    Missing,

    /// <summary>The file was read and every entry is usable.</summary>
    Valid,

    /// <summary>
    /// The file was read but some entries were unusable and are ignored; the rest are kept, so one bad
    /// entry never costs the developer their other decisions.
    /// </summary>
    Partial,

    /// <summary>The file is malformed, oversized or not a regular file; it is treated as empty.</summary>
    Invalid,

    /// <summary>The file was written by a newer version of the package and must not be replaced.</summary>
    Newer,
}

/// <summary>One saved decision and, when known, the fingerprint of the service it was made about.</summary>
internal readonly record struct SelectionEntry(bool Start, string? Fingerprint);

/// <summary>
/// Reads and writes <c>.servicesources/selection.json</c>: which cold checkouts the developer chose
/// to clone and start.
/// </summary>
/// <remarks>
/// The file only selects among services the AppHost already declares. It never supplies a URL, path,
/// ref or command, so a hostile or corrupted file can at worst skip or start a declared service.
/// </remarks>
internal static class SourceSelectionStore
{
    public const string FileName = "selection.json";

    /// <summary>Largest file read; anything bigger is treated as invalid rather than buffered.</summary>
    public const int MaxBytes = 64 * 1024;

    private const int CurrentVersion = 1;

    private const string LockFileName = ".selection.lock";

    private const int LockAttempts = 50;

    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(40);

    public static (IReadOnlyDictionary<string, bool> Decisions, SelectionFileState State) Read(string appHostDirectory)
    {
        var (entries, state) = ReadEntries(appHostDirectory);
        var decisions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in entries)
        {
            decisions[name] = entry.Start;
        }

        return (decisions, state);
    }

    /// <summary>
    /// Like <see cref="Read"/>, with each entry's fingerprint: what the service was when the developer
    /// answered, so a decision about a repository that has since changed is not applied to the new one.
    /// </summary>
    public static (IReadOnlyDictionary<string, SelectionEntry> Entries, SelectionFileState State) ReadEntries(
        string appHostDirectory)
    {
        var empty = new Dictionary<string, SelectionEntry>(StringComparer.OrdinalIgnoreCase);
        var path = PathIn(appHostDirectory);

        try
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return (empty, SelectionFileState.Missing);
            }

            if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return (empty, SelectionFileState.Invalid);
            }

            var bytes = ReadBounded(path);
            return bytes is null ? (empty, SelectionFileState.Invalid) : Parse(bytes, empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return (empty, SelectionFileState.Invalid);
        }
    }

    /// <summary>
    /// A fingerprint for a service's repository and ref. Compared, never used: nothing read back from
    /// the file reaches a clone.
    /// </summary>
    public static string Fingerprint(string repositoryUrl, string? reference)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(repositoryUrl + "\n" + reference);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Merges <paramref name="answered"/> into the file. Returns <see langword="false"/> without
    /// throwing when it could not, including when the file is newer than this version understands.
    /// </summary>
    public static bool TrySave(
        string appHostDirectory, IReadOnlyDictionary<string, bool> answered,
        IReadOnlyDictionary<string, string>? fingerprints = null, int lockAttempts = LockAttempts)
    {
        string? scratch = null;
        FileStream? guard = null;

        try
        {
            // Ensured first so the file is git-ignored before it exists.
            var directory = ToolDirectory.Ensure(appHostDirectory);
            var path = Path.Combine(directory, FileName);

            // A link is read as invalid and ignored, so it is not replaced either: renaming a file
            // over it would destroy the link and stop updating whatever it points at.
            if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            guard = AcquireLock(directory, lockAttempts);
            if (guard is null)
            {
                return false;
            }

            // Re-read now, under the lock: another AppHost over this directory may have saved since
            // the prompt opened.
            var (existing, state) = ReadEntries(appHostDirectory);
            if (state == SelectionFileState.Newer)
            {
                return false;
            }

            var merged = new Dictionary<string, SelectionEntry>(existing, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, start) in answered)
            {
                // Removed first so the catalog's spelling replaces a differently cased older key.
                merged.Remove(name);
                merged[name] = new SelectionEntry(
                    start, fingerprints is not null && fingerprints.TryGetValue(name, out var fingerprint) ? fingerprint : null);
            }

            scratch = Path.Combine(directory, $".incoming-selection-{Guid.NewGuid():N}.json");
            File.WriteAllBytes(scratch, Serialize(merged));
            PrepareMarker.MoveOntoMarker(scratch, path);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            if (scratch is not null)
            {
                PrepareMarker.TryDelete(scratch);
            }

            return false;
        }
        finally
        {
            guard?.Dispose();
        }
    }

    /// <summary>
    /// Takes the cross-process lock that makes <see cref="TrySave"/>'s read-merge-write atomic, waiting
    /// briefly for a concurrent saver. Null when it could not.
    /// </summary>
    private static FileStream? AcquireLock(string directory, int lockAttempts)
    {
        var lockPath = Path.Combine(directory, LockFileName);

        for (var attempt = 0; attempt < lockAttempts; attempt++)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                Thread.Sleep(LockRetryDelay);
            }
        }

        return null;
    }

    internal static string PathIn(string appHostDirectory) =>
        Path.Combine(ToolDirectory.PathIn(appHostDirectory), FileName);

    private static byte[]? ReadBounded(string path)
    {
        // FileShare.Delete so a concurrent save's rename over this name is not held hostage on Windows.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var buffer = new byte[MaxBytes + 1];
        var length = 0;

        int read;
        while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
        {
            length += read;
        }

        return length > MaxBytes ? null : buffer.AsSpan(0, length).ToArray();
    }

    private static (IReadOnlyDictionary<string, SelectionEntry>, SelectionFileState) Parse(
        byte[] bytes, Dictionary<string, SelectionEntry> empty)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt64(out var versionNumber)
            || versionNumber < CurrentVersion)
        {
            return (empty, SelectionFileState.Invalid);
        }

        if (versionNumber > CurrentVersion)
        {
            return (empty, SelectionFileState.Newer);
        }

        if (!root.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Object)
        {
            return (empty, SelectionFileState.Invalid);
        }

        var entries = new Dictionary<string, SelectionEntry>(StringComparer.OrdinalIgnoreCase);
        var skipped = false;

        // Enumerated rather than looked up, so a duplicate key is last-wins on every target framework.
        foreach (var service in services.EnumerateObject())
        {
            // An unusable entry is dropped on its own: the file's other decisions stay good, and the
            // next save no longer carries it.
            if (service.Value.ValueKind != JsonValueKind.Object
                || !service.Value.TryGetProperty("start", out var start)
                || start.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                skipped = true;
                continue;
            }

            string? fingerprint = null;
            if (service.Value.TryGetProperty("fingerprint", out var print) && print.ValueKind == JsonValueKind.String)
            {
                fingerprint = print.GetString();
            }

            entries[service.Name] = new SelectionEntry(start.GetBoolean(), fingerprint);
        }

        return (entries, skipped ? SelectionFileState.Partial : SelectionFileState.Valid);
    }

    private static byte[] Serialize(Dictionary<string, SelectionEntry> decisions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", CurrentVersion);
            writer.WriteStartObject("services");

            foreach (var (name, entry) in decisions.OrderBy(entry => entry.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(name);
                writer.WriteBoolean("start", entry.Start);
                if (entry.Fingerprint is not null)
                {
                    writer.WriteString("fingerprint", entry.Fingerprint);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }
}
