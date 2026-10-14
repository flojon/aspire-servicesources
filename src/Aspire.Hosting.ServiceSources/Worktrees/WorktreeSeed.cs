using System.Text.Json.Nodes;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Messages;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Worktrees;

/// <summary>
/// Copies the main worktree's git-ignored ServiceSources files into a linked worktree, once.
/// </summary>
internal static class WorktreeSeed
{
    public const string MarkerFileName = "worktree-seed.json";

    public static string MarkerPath(string appHostDirectory) =>
        Path.Combine(ToolDirectory.PathIn(appHostDirectory), MarkerFileName);

    /// <summary>Never throws: a seeding problem is a notice, and the next run tries again.</summary>
    public static void EnsureSeeded(IDistributedApplicationBuilder builder)
    {
        try
        {
            Seed(builder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceSourcesWarnings.For(builder).AddNotice(Raw.Compose(
                $"Could not finish seeding this linked worktree's ServiceSources files from the main worktree ({Raw.Cause(ex)}). It will be tried again on the next run."));
        }
    }

    private static void Seed(IDistributedApplicationBuilder builder)
    {
        var worktree = builder.AppHostDirectory;
        var marker = MarkerPath(worktree);

        // The marker, not the files, records that seeding happened, so a copy the developer deletes stays deleted.
        if (File.Exists(marker) || WorktreeHome.TryResolve(builder) is not { } home)
        {
            return;
        }

        ToolDirectory.Ensure(worktree);

        var warnings = ServiceSourcesWarnings.For(builder);
        var copied = new List<string>();
        var failed = false;

        (string Source, string Destination, string Display)[] files =
        [
            (Path.Combine(home.AppHostDirectory, DeveloperConfiguration.FileName),
                Path.Combine(worktree, DeveloperConfiguration.FileName),
                DeveloperConfiguration.FileName),
            (SourceSelectionStore.PathIn(home.AppHostDirectory),
                SourceSelectionStore.PathIn(worktree),
                $"{ToolDirectory.Name}/{SourceSelectionStore.FileName}"),
        ];

        foreach (var (source, destination, display) in files)
        {
            try
            {
                if (CopyIfAbsent(source, destination))
                {
                    copied.Add(display);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed = true;
                warnings.AddNotice(Raw.Compose(
                    $"Could not copy '{Raw.Escaped(source)}' into this linked worktree ({Raw.Cause(ex)}). Seeding from the main worktree will be tried again on the next run."));
            }
        }

        // Only entries seeding just copied: one the developer writes afterwards is deliberate.
        if (copied.Contains(DeveloperConfiguration.FileName))
        {
            foreach (var notice in PathOverrideCheck.Inspect(
                Path.Combine(worktree, DeveloperConfiguration.FileName), home, key => builder.Configuration[key]))
            {
                warnings.AddNotice(notice);
            }
        }

        if (copied.Count > 0)
        {
            warnings.AddNotice(Raw.Compose(
                $"This AppHost runs from a linked git worktree, so its ServiceSources files were seeded from the main worktree's AppHost at '{Raw.Escaped(home.AppHostDirectory)}': copied {Raw.Join(", ", copied.Select(name => Raw.Escaped(name)))}. They are this worktree's own from now on."));
        }

        if (!failed)
        {
            WriteMarker(marker, home.AppHostDirectory, copied);
        }
    }

    /// <summary>Copies a regular file to a destination that must not exist yet.</summary>
    /// <returns>Whether this call created the destination.</returns>
    private static bool CopyIfAbsent(string source, string destination)
    {
        if (File.Exists(destination)
            || !File.Exists(source)
            || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var scratch = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }

            // Moved whole, so a concurrent AppHost never reads a half-written file.
            File.Move(scratch, destination, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(destination))
        {
            // A concurrent AppHost in the same worktree copied it first.
            return false;
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    private static void WriteMarker(string marker, string home, IReadOnlyList<string> copied)
    {
        var json = new JsonObject
        {
            ["home"] = home,
            ["copied"] = new JsonArray([.. copied.Select(name => (JsonNode?)JsonValue.Create(name))]),
        }.ToJsonString();

        try
        {
            using var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(json);
        }
        catch (IOException) when (File.Exists(marker))
        {
            // A concurrent AppHost wrote it first.
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
            // Best effort: a stray scratch file is harmless.
        }
    }
}
