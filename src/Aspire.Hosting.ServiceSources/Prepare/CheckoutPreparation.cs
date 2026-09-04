using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Prepare;

/// <summary>
/// Runs one service's <c>prepare</c> step against its completed checkout — the marker, the modes,
/// the command resolution and the failure text, in one place called from two.
/// </summary>
/// <remarks>
/// <para>
/// The eager path calls this between <c>GetRepoRoot</c> and the kind dispatch; the deferred path
/// calls it in the background task between the landed clone and <c>ValidateCheckout</c>. Only
/// <em>where</em> it runs differs, and only in what it reports to and what a failure becomes: during
/// composition an exception fails the AppHost, exactly as a bad <c>repository</c> or a missing
/// <c>project</c> already does, while on the deferred path the same exception is caught by the start
/// task and becomes that one service's resource state.
/// </para>
/// <para>
/// A service's step never runs concurrently with itself: a service resolves on exactly one of the
/// two paths and has exactly one task on it. Two services can prepare at the same time, and a
/// command has to tolerate that — what they share is the machine and whatever package caches they
/// use, never a working tree, since managed checkouts are per-service clones and the one arrangement
/// that shares a tree (two services on one <c>path</c>) never defers and so is serial.
/// </para>
/// </remarks>
internal static class CheckoutPreparation
{
    /// <summary>
    /// How many of the command's last output lines a failure quotes.
    /// </summary>
    /// <remarks>
    /// A tail rather than everything: a bootstrap that imports a country-sized extract writes
    /// thousands of lines, and the ones that say why it failed are at the end. The whole of the
    /// output has already been reported line by line as it arrived, so nothing is lost — this is
    /// what the exception message can carry.
    /// </remarks>
    private const int OutputTailLines = 20;

    /// <summary>
    /// Runs <paramref name="step"/> if its mode and marker say it should, and records the completion.
    /// </summary>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The command exited non-zero, or could not be started at all.
    /// </exception>
    public static void Run(
        string serviceName,
        PrepareStep step,
        string repoRoot,
        string appHostDirectory,
        bool managedCheckout,
        IGitClient gitClient,
        IPrepareCommandRunner runner,
        IPrepareOutputSink sink)
    {
        var markerPath = PrepareMarker.LocationFor(serviceName, repoRoot, appHostDirectory, managedCheckout);

        // The path a `path` marker is keyed on as well as the command and the commit: it is the one
        // marker that does not live with the directory it describes, so re-pointing `local.path`
        // elsewhere has to invalidate it, and two services sharing one directory have to keep
        // independent markers.
        var checkoutPath = managedCheckout ? null : PrepareMarker.NormalizeCheckoutPath(repoRoot);

        // Read after the checkout has been reconciled onto its configured ref, so this is the commit
        // the step actually runs against. Not read at all under `always`, which consults no marker
        // and writes none, so there is nothing for it to be compared with or recorded in.
        var commit = step.Mode == PrepareMode.Always ? null : gitClient.GetHeadCommitSha(repoRoot);

        if (ReasonToRun(step, markerPath, commit, checkoutPath) is not { } reason)
        {
            // A decision to skip is not reported. Skipping is the ordinary case — every start after
            // the first — and the marker file already records it.
            return;
        }

        sink.Report($"{Tag(serviceName)} {reason} Running: {step.Describe()}");

        var tail = new Queue<string>(OutputTailLines);
        var exitCode = Launch(serviceName, step, repoRoot, runner, sink, tail);

        if (exitCode != 0)
        {
            throw new ServiceSourcesConfigurationException(FailedMessage(serviceName, step, exitCode, tail));
        }

        // `always` records nothing: it is the mode whose command decides its own work, so a marker
        // would be a claim nothing ever reads. The other two write one only now, on success — a step
        // that failed halfway runs again from the beginning next start, against a checkout holding
        // whatever the first attempt managed to produce, which is why a prepare command has to be
        // safe to re-run under every mode.
        //
        // The commit is recorded even under `once`, which never compares it: it costs a field, and
        // it means a developer who later switches the mode to `oncePerCommit` is matched against the
        // commit the step really ran on rather than forced through one re-run to find out.
        if (step.Mode != PrepareMode.Always)
        {
            PrepareMarker.Write(
                markerPath,
                new PrepareMarker(step.CommandHash, commit, DateTime.UtcNow.ToString("O"), checkoutPath),
                appHostDirectory,
                managedCheckout);
        }
    }

    /// <summary>
    /// Why the step is about to run, or <see langword="null"/> when it is not.
    /// </summary>
    /// <remarks>
    /// One uniform line per decision to run, rather than a warning special-cased to a mode or to a
    /// shape of checkout. That is the whole of what this design says about telling a developer why
    /// their start is slow — including the case where the commit cannot be resolved, which is a fact
    /// about this start rather than a warning bolted onto a mode choice.
    /// </remarks>
    private static string? ReasonToRun(
        PrepareStep step, string markerPath, string? commit, string? checkoutPath)
    {
        if (step.Mode == PrepareMode.Always)
        {
            return "its prepare step runs on every start (mode: always).";
        }

        if (PrepareMarker.Read(markerPath) is not { } marker)
        {
            return "no completed prepare step is recorded for this checkout.";
        }

        if (marker.Satisfies(step.CommandHash, commit, step.Mode, checkoutPath))
        {
            return null;
        }

        if (!string.Equals(marker.CommandHash, step.CommandHash, StringComparison.Ordinal))
        {
            return "its prepare command has changed since it last succeeded.";
        }

        if (checkoutPath is not null && !string.Equals(marker.Path, checkoutPath, StringComparison.Ordinal))
        {
            return "its 'local.path' now points at a different checkout than the one it last prepared.";
        }

        return commit is null
            ? "the commit its checkout is on could not be determined, so a completed prepare step "
              + "cannot be matched against it (mode: oncePerCommit)."
            : "its checkout has moved to another commit since its prepare step last succeeded.";
    }

    private static int Launch(
        string serviceName,
        PrepareStep step,
        string repoRoot,
        IPrepareCommandRunner runner,
        IPrepareOutputSink sink,
        Queue<string> tail)
    {
        var tag = Tag(serviceName);

        try
        {
            return runner.Run(repoRoot, step.Command, line =>
            {
                // Both of the command's streams are read, and a process-backed runner reads them on
                // separate threads, so this callback is re-entered concurrently. The queue is not
                // thread-safe, and reporting is serialized alongside it so two half-lines cannot be
                // interleaved into one.
                lock (tail)
                {
                    sink.Report($"{tag} {line}");

                    tail.Enqueue(line);
                    if (tail.Count > OutputTailLines)
                    {
                        tail.Dequeue();
                    }
                }
            });
        }
        catch (PrepareLaunchException ex)
        {
            throw new ServiceSourcesConfigurationException(LaunchFailedMessage(serviceName, step, ex), ex);
        }
    }

    /// <summary>
    /// The prefix every line about this step carries, so a step's output is attributable when
    /// several services report at once.
    /// </summary>
    private static string Tag(string serviceName) => $"[prepare {serviceName}]";

    private static string FailedMessage(
        string serviceName, PrepareStep step, int exitCode, IEnumerable<string> tail)
    {
        var quoted = tail.ToArray();

        return $"Service '{serviceName}': its prepare step failed. The command '{step.Describe()}' exited with "
            + $"code {exitCode}, so the checkout was left as the command found it and nothing was recorded as "
            + "completed — the step will run again from the beginning on the next start."
            + (quoted.Length == 0
                ? " It wrote no output."
                : $"{Environment.NewLine}Last {(quoted.Length == 1 ? "line" : $"{quoted.Length} lines")} of its "
                  + $"output:{Environment.NewLine}  " + string.Join($"{Environment.NewLine}  ", quoted));
    }

    /// <remarks>
    /// Reported apart from a non-zero exit because it is a different problem with a different fix:
    /// the command never ran, so nothing about the checkout is in question. On Windows with no
    /// <c>windowsCommand</c> declared it is named as the likely cause, that being the one shape of
    /// this failure the configuration can be read off — a POSIX script has no execute bit there and
    /// no interpreter to reach it.
    /// </remarks>
    private static string LaunchFailedMessage(string serviceName, PrepareStep step, PrepareLaunchException ex) =>
        $"Service '{serviceName}': its prepare step could not be started. {ex.Message} The command is "
        + $"'{step.Describe()}', run with the service's checkout as its working directory; its first element has "
        + "to be a path to something executable inside the checkout, or the name of a program on PATH."
        + (step.WindowsWithoutVariant
            ? " This AppHost is running on Windows and the block declares no 'windowsCommand', so the command "
              + "above is the cross-platform one — if it names a POSIX script, add a 'windowsCommand' variant "
              + "beside it, e.g. [\"pwsh\", \"-File\", \"prepare.ps1\"]."
            : "");
}
