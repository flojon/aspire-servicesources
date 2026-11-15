using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Git;

internal sealed record OwnerRecord(
    [property: JsonPropertyName("checkout")] string Checkout,
    [property: JsonPropertyName("createdUtc")] DateTimeOffset CreatedUtc,
    [property: JsonPropertyName("lastUsedUtc")] DateTimeOffset LastUsedUtc);

/// <param name="GitDirectory">The git directory of an in-place <c>.git</c> directory or an accepted <c>.git</c> file.</param>
/// <param name="Refusal">Why a <c>.git</c> file was rejected; both are <see langword="null"/> when there is no <c>.git</c> entry.</param>
internal readonly record struct ResolveResult(string? GitDirectory, string? Refusal);

/// <summary>
/// Names, records and finds the short git directories managed clones keep outside their working tree.
/// </summary>
internal static partial class ManagedGitDirectory
{
    internal const string OwnerFileName = "servicesources-owner.json";

    private const int MaxGitFileBytes = 4096;
    private const string GitDirPrefix = "gitdir:";

    internal static readonly Regex NamePattern = NameRegex();

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    internal static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex(@"^[0-9a-f]{16}-[0-9a-f]{8}\.git$")]
    private static partial Regex NameRegex();

    public static string NameFor(string finalCheckoutPath, string id) => $"{HashOf(finalCheckoutPath)}-{id}.git";

    // Upper-cased because Windows compares paths ignoring case.
    public static string HashOf(string finalCheckoutPath)
    {
        var normalized = Normalize(finalCheckoutPath).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
    }

    internal static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static void WriteOwner(string gitDir, OwnerRecord record)
    {
        var scratch = Path.Combine(gitDir, $"{OwnerFileName}.tmp-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(scratch, JsonSerializer.Serialize(record, SerializerOptions));
            PrepareMarker.MoveOntoMarker(scratch, Path.Combine(gitDir, OwnerFileName));
        }
        catch
        {
            PrepareMarker.TryDelete(scratch);
            throw;
        }
    }

    public static OwnerRecord? ReadOwner(string gitDir)
    {
        try
        {
            var record = JsonSerializer.Deserialize<OwnerRecord>(
                PrepareMarker.ReadAllTextSharingRename(Path.Combine(gitDir, OwnerFileName)), SerializerOptions);
            if (string.IsNullOrWhiteSpace(record?.Checkout))
            {
                return null;
            }

            // A record is untrusted, so a path that cannot be normalized later must not throw out of callers.
            _ = Path.GetFullPath(record.Checkout);
            return record;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The one place that answers whether a checkout's <c>.git</c> entry is one this tool made.
    /// </summary>
    /// <remarks>
    /// A <c>.git</c> file is accepted only when it names a direct child of the git root that carries
    /// an owner record naming this checkout: git runs hooks from whatever git directory it is pointed at.
    /// </remarks>
    public static ResolveResult Resolve(string repoRoot, string? gitRoot)
    {
        var dotGit = Path.Combine(repoRoot, ".git");
        if (Directory.Exists(dotGit))
        {
            return new ResolveResult(dotGit, null);
        }

        if (!File.Exists(dotGit))
        {
            return default;
        }

        var refusal = Validate(dotGit, repoRoot, gitRoot, out var gitDirectory);
        return refusal is null ? new ResolveResult(gitDirectory, null) : new ResolveResult(null, refusal);
    }

    private static string? Validate(string dotGit, string repoRoot, string? gitRoot, out string? gitDirectory)
    {
        gitDirectory = null;

        if (string.IsNullOrEmpty(gitRoot))
        {
            return $"the '.git' file at '{dotGit}' names a git directory, but this machine has no usable git directory root (no local application data folder), so it cannot be verified as one this tool made";
        }

        string text;
        try
        {
            if (new FileInfo(dotGit).Length > MaxGitFileBytes)
            {
                return $"the '.git' file at '{dotGit}' is larger than 4 KiB, so it is not one git wrote";
            }

            text = File.ReadAllText(dotGit);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"the '.git' file at '{dotGit}' cannot be read ({ex.Message})";
        }

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count != 1)
        {
            return $"the '.git' file at '{dotGit}' has more than one line, so it is not one git wrote";
        }

        if (!lines[0].StartsWith(GitDirPrefix, StringComparison.Ordinal))
        {
            return $"the '.git' file at '{dotGit}' does not start with 'gitdir:'";
        }

        var target = lines[0][GitDirPrefix.Length..].Trim();
        if (target.Length == 0 || !Path.IsPathRooted(target))
        {
            return $"the '.git' file at '{dotGit}' does not name an absolute git directory";
        }

        if (target.Split('/', '\\').Contains(".."))
        {
            return $"the '.git' file at '{dotGit}' names a git directory with a '..' segment";
        }

        string full;
        string root;
        try
        {
            full = Normalize(target);
            root = Normalize(gitRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return $"the '.git' file at '{dotGit}' names a path that cannot be normalized";
        }

        var parent = Path.GetDirectoryName(full);
        if (parent is null || !string.Equals(parent, root, PathComparison))
        {
            return $"the '.git' file at '{dotGit}' names '{full}', which is not a direct child of this machine's git root '{root}'. It was made by another account, an elevated process, a different LOCALAPPDATA or WSL";
        }

        if (!NamePattern.IsMatch(Path.GetFileName(full)))
        {
            return $"the '.git' file at '{dotGit}' names '{full}', whose name is not one this tool gives a git directory";
        }

        if (!Directory.Exists(full))
        {
            return $"the git directory '{full}' the '.git' file at '{dotGit}' names no longer exists";
        }

        if (FindReparsePoint(full, root) is { } reparse)
        {
            return $"'{reparse}', on the way to the git directory '{full}', is a reparse point";
        }

        var ownerPath = Path.Combine(full, OwnerFileName);
        if (!File.Exists(ownerPath))
        {
            return $"the git directory '{full}' has no ownership record, so it was not made by this tool";
        }

        if (ReadOwner(full) is not { } owner)
        {
            return $"the ownership record in '{full}' cannot be read";
        }

        if (!string.Equals(Normalize(owner.Checkout), Normalize(repoRoot), PathComparison))
        {
            return $"the git directory '{full}' belongs to the checkout '{owner.Checkout}', not '{repoRoot}': the AppHost directory or worktree was moved or renamed. Delete the checkout directory and re-run to clone it again";
        }

        gitDirectory = full;
        return null;
    }

    // Above LocalApplicationData is the user's own profile, inside the trust boundary.
    private static string? FindReparsePoint(string gitDirectory, string gitRoot)
    {
        var stop = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var stopAt = string.IsNullOrEmpty(stop) ? null : Normalize(stop);

        for (var current = (string?)gitDirectory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (stopAt is not null && string.Equals(current, stopAt, PathComparison))
            {
                break;
            }

            try
            {
                if (Directory.Exists(current) && new DirectoryInfo(current).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return current;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return current;
            }
        }

        return null;
    }
}
