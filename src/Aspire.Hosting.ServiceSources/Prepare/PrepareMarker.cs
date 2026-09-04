using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.ServiceSources.Prepare;

/// <summary>
/// The record a successful <c>prepare</c> step leaves behind, which is what makes the next start
/// skip it: the hash of the command that ran and the commit it ran against.
/// </summary>
/// <remarks>
/// It answers "has this checkout moved since the step last succeeded", not "are the step's outputs
/// up to date". The second question is not answerable from here — nothing in the catalog says what
/// the command reads or writes, hashing the working tree is expensive and answers wrongly in both
/// directions, and re-running whenever the tree is dirty would pay the full bootstrap on every start
/// for exactly the developer <c>"local"</c> exists to serve. Incremental rebuild is a solved problem
/// with real dependency graphs behind it, so <c>mode: always</c> delegates to them instead.
/// </remarks>
internal sealed record PrepareMarker(
    [property: JsonPropertyName("commandHash")] string CommandHash,
    [property: JsonPropertyName("commit")] string? Commit,
    [property: JsonPropertyName("completedUtc")] string CompletedUtc,
    [property: JsonPropertyName("path")] string? Path = null)
{
    private const string FileNameInGitDirectory = "servicesources-prepare.json";

    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Where the marker for one service lives, which depends on who owns the checkout directory.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A managed checkout keeps it inside its own <c>.git</c>. It is invisible to the service
    /// repository's <c>git status</c>, so it cannot be mistaken for the developer's own file or
    /// accidentally committed, and it dies with the checkout — so a deleted-and-recloned checkout
    /// re-prepares even at the same commit, which a marker stored beside the checkout would get
    /// wrong. It also makes "once per checkout, not once per repository" automatic rather than a
    /// rule to enforce: the key is the directory, and two services off one repository have two
    /// working trees.
    /// </para>
    /// <para>
    /// A <c>path</c> checkout gets neither, and the part that is available would be unwelcome.
    /// <c>.git</c> is a <em>file</em> rather than a directory for a linked worktree and for a
    /// <c>--separate-git-dir</c> clone — the two shapes <c>CloneIntoPlace</c> refuses for managed
    /// checkouts, telling the developer to point <c>local.path</c> at it, so the tool's own
    /// documented remedy produces exactly the shape a <c>.git</c> marker cannot handle. And writing
    /// into a directory the tool does not own is the one thing <c>path</c> exists to promise it will
    /// never do. So it goes in the tool's own tree, keyed on the resolved absolute path as well as
    /// the command: re-pointing <c>path</c> elsewhere invalidates it, and two services sharing one
    /// directory keep independent markers, which is correct when their commands differ.
    /// </para>
    /// </remarks>
    public static string LocationFor(
        string serviceName, string repoRoot, string appHostDirectory, bool managedCheckout) =>
        managedCheckout
            ? System.IO.Path.Combine(repoRoot, ".git", FileNameInGitDirectory)
            : System.IO.Path.Combine(
                ToolDirectory.PathIn(appHostDirectory), "prepare", $"{serviceName}.json");

    /// <summary>
    /// The recorded completion, or <see langword="null"/> when there is none to read — absent,
    /// unreadable, or not a marker.
    /// </summary>
    /// <remarks>
    /// A malformed marker runs the step rather than throwing. It should be rare, since a marker is
    /// only ever renamed into place whole, and the fail-safe direction is the same one an
    /// unresolvable commit takes: "cannot tell" must not be allowed to mean "assume done".
    /// </remarks>
    public static PrepareMarker? Read(string markerPath)
    {
        try
        {
            if (!File.Exists(markerPath))
            {
                return null;
            }

            var marker = JsonSerializer.Deserialize<PrepareMarker>(File.ReadAllText(markerPath), SerializerOptions);

            // A file holding "null", or one whose object carries no commandHash, records no
            // completion of anything — there is nothing for a comparison to be about.
            return string.IsNullOrEmpty(marker?.CommandHash) ? null : marker;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Records a completed step, replacing any earlier record.
    /// </summary>
    /// <remarks>
    /// Written to a temporary file in the same directory and renamed over the old one rather than
    /// written in place, so a reader sees the previous record or the new one and never a
    /// half-written file. Two <c>aspire run</c>s over one AppHost directory resolve the same managed
    /// <c>repoRoot</c> and therefore the same marker path, which is the case that has two writers —
    /// and the rename is what keeps "an unreadable marker runs rather than throws" a fallback rather
    /// than a routine.
    /// </remarks>
    public static void Write(
        string markerPath, PrepareMarker marker, string appHostDirectory, bool managedCheckout)
    {
        // For a `path` checkout this is the point the tool directory is acquired at all: an AppHost
        // whose services all use `path` and declare no step should never grow one, and the
        // .gitignore that comes with it is not optional here — without it the marker becomes the one
        // tool-managed file a developer would see listed as untracked in their own repository.
        if (!managedCheckout)
        {
            ToolDirectory.Ensure(appHostDirectory);
        }

        var directory = System.IO.Path.GetDirectoryName(markerPath)!;
        var scratch = System.IO.Path.Combine(directory, $".incoming-prepare-{Guid.NewGuid():N}.json");

        try
        {
            // Inside the try with the write it exists for: creating the directory can fail for the
            // same reasons writing can — a read-only tree, or a '.git' that is a file rather than a
            // directory, which a managed checkout cannot be but a directory handed to this code
            // could be — and an exception escaping here would turn "the completion could not be
            // recorded" into "the service does not start", after the step had already succeeded.
            Directory.CreateDirectory(directory);

            File.WriteAllText(scratch, JsonSerializer.Serialize(marker, SerializerOptions));
            File.Move(scratch, markerPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort, deliberately: the step has already succeeded, and failing the service now
            // would turn "the completion could not be recorded" into "the service does not start".
            // What it costs is a step that runs again on the next start, which every mode's command
            // has to tolerate anyway.
            TryDelete(scratch);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The rename already consumed it, or it cannot be removed. Nothing depends on it.
        }
    }

    /// <summary>
    /// The checkout path as it enters a <c>path</c> marker's key — normalized, so that the same
    /// directory reached by two spellings is one key.
    /// </summary>
    /// <remarks>
    /// Compared ordinally afterwards, which on Windows means a path whose case changed re-runs the
    /// step. That is the safe direction and the cheap one: the remedy is one extra run of a command
    /// that has to be safe to re-run anyway.
    /// </remarks>
    public static string NormalizeCheckoutPath(string repoRoot) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(repoRoot));

    /// <summary>
    /// Whether this record says the step is done, given this run's command, commit and checkout.
    /// </summary>
    /// <param name="commit">
    /// The commit the checkout is on now, or <see langword="null"/> when it could not be determined
    /// — which under <see cref="PrepareMode.OncePerCommit"/> means the step runs, because "cannot
    /// verify" must not mean "assume done". For a <c>path</c> checkout that arises routinely, since
    /// such a directory need not be a git repository at all; for a managed one it should not arise,
    /// that always being a real clone.
    /// </param>
    /// <param name="checkoutPath">
    /// The normalized checkout path for a marker that does not live with the directory it describes
    /// — a <c>path</c> checkout's — or <see langword="null"/> for a managed one, whose location is
    /// already the key.
    /// </param>
    public bool Satisfies(string commandHash, string? commit, PrepareMode mode, string? checkoutPath)
    {
        if (!string.Equals(CommandHash, commandHash, StringComparison.Ordinal))
        {
            return false;
        }

        if (checkoutPath is not null && !string.Equals(Path, checkoutPath, StringComparison.Ordinal))
        {
            return false;
        }

        // `once` records the commit but never consults it, so it stays satisfied as the commit moves
        // under it — which is the whole difference between the two guarded modes.
        return mode != PrepareMode.OncePerCommit
            || (commit is not null && string.Equals(Commit, commit, StringComparison.Ordinal));
    }
}
