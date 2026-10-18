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

    /// <summary>
    /// Never throws: seeding is a convenience, so any problem is a notice and the next run tries again.
    /// </summary>
    /// <returns>The home worktree when this call copied its config file, otherwise <see langword="null"/>.</returns>
    public static HomeWorktree? EnsureSeeded(IDistributedApplicationBuilder builder)
    {
        try
        {
            return Seed(builder);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ServiceSourcesWarnings.For(builder).AddNotice(Raw.Compose(
                $"Could not finish seeding this linked worktree's ServiceSources files from the main worktree ({Raw.Cause(ex)}). It will be tried again on the next run."));
            return null;
        }
    }

    /// <summary>
    /// Notices for the copied file's directory overrides that would not mean the same thing here.
    /// Never throws, for the same reason as <see cref="EnsureSeeded"/>.
    /// </summary>
    public static void ReportPathOverrides(
        IDistributedApplicationBuilder builder, HomeWorktree home, IEnumerable<KeyValuePair<string, string?>> fileEntries)
    {
        try
        {
            var warnings = ServiceSourcesWarnings.For(builder);
            foreach (var notice in PathOverrideCheck.Inspect(
                Path.Combine(builder.AppHostDirectory, DeveloperConfiguration.FileName),
                home,
                fileEntries,
                key => builder.Configuration[key]))
            {
                warnings.AddNotice(notice);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The copy already happened; a failed inspection must not fail the run.
        }
    }

    private static HomeWorktree? Seed(IDistributedApplicationBuilder builder)
    {
        var worktree = builder.AppHostDirectory;
        var marker = MarkerPath(worktree);

        // The marker, not the files, records that seeding happened, so a copy the developer deletes stays deleted.
        if (File.Exists(marker) || WorktreeHome.TryResolve(builder) is not { } home)
        {
            return null;
        }

        ToolDirectory.Ensure(worktree);

        var warnings = ServiceSourcesWarnings.For(builder);
        var copied = new List<string>();
        var failed = false;
        var sourcesFound = false;

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
                var outcome = CopyIfAbsent(source, destination);
                sourcesFound |= outcome != CopyOutcome.SourceMissing;

                switch (outcome)
                {
                    case CopyOutcome.Copied:
                        copied.Add(display);
                        break;
                    case CopyOutcome.SourceIsLink:
                        warnings.AddNotice(Raw.Compose(
                            $"'{Raw.Escaped(source)}' is a symbolic link, so it was not copied into this linked worktree. Copy its contents to '{Raw.Escaped(destination)}' yourself if this worktree should have them."));
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed = true;
                warnings.AddNotice(Raw.Compose(
                    $"Could not copy '{Raw.Escaped(source)}' into this linked worktree ({Raw.Cause(ex)}). Seeding from the main worktree will be tried again on the next run."));
            }
        }

        if (copied.Count > 0)
        {
            warnings.AddNotice(Raw.Compose(
                $"This AppHost runs from a linked git worktree, so its ServiceSources files were seeded from the main worktree's AppHost at '{Raw.Escaped(home.AppHostDirectory)}': copied {Raw.Join(", ", copied.Select(name => Raw.Escaped(name)))}. They are this worktree's own from now on."));
        }

        // With neither file in the main worktree yet, a later run may still have something to seed.
        if (!failed && sourcesFound)
        {
            WriteMarker(marker, home.AppHostDirectory, copied);
        }

        // Only an entry seeding just copied is inspected: one the developer writes afterwards is deliberate.
        return copied.Contains(DeveloperConfiguration.FileName) ? home : null;
    }

    /// <summary>Copies a regular file to a destination that must not exist yet.</summary>
    private static CopyOutcome CopyIfAbsent(string source, string destination)
    {
        if (File.Exists(destination))
        {
            return CopyOutcome.Nothing;
        }

        if (!File.Exists(source))
        {
            return CopyOutcome.SourceMissing;
        }

        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
        {
            return CopyOutcome.SourceIsLink;
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
            return CopyOutcome.Copied;
        }
        catch (IOException) when (File.Exists(destination))
        {
            // A concurrent AppHost in the same worktree copied it first.
            return CopyOutcome.Nothing;
        }
        finally
        {
            TryDelete(scratch);
        }
    }

    private enum CopyOutcome
    {
        Nothing,
        SourceMissing,
        Copied,
        SourceIsLink,
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
