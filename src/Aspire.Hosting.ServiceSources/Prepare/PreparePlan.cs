using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Prepare;

/// <summary>
/// What composition settles about one service's <c>prepare</c> step: the step to run once its
/// checkout is complete, or — for a <c>path</c> checkout the catalog declared a step for and the
/// developer has not — the notice that asks them to declare one.
/// </summary>
/// <remarks>
/// <para>
/// Settled from configuration alone, with no filesystem access, which is what lets it run in front
/// of the clone and ahead of <c>ShouldDefer</c> — so a typo'd mode, or a command climbing out of the
/// checkout, is a composition-time error on both the eager and the deferred path. That is more than
/// a kind's own <c>Validate</c> can now claim: since #63/#197 it is handed the resolved checkout, so
/// it covers neither the deferred path nor anything before the clone.
/// </para>
/// <para>
/// The split this block can make cleanly is the one #197 forced on kinds and they could not: the
/// mode is an enum, and confining the command is a check on the <em>shape</em> of a relative path,
/// with resolution against the checkout deferred to the moment of execution.
/// </para>
/// </remarks>
/// <param name="Step">
/// The step to run, or <see langword="null"/> when nothing should — no block anywhere, a block that
/// names no command, <c>mode: never</c>, or a <c>path</c> checkout that inherited nothing.
/// </param>
/// <param name="IgnoredCatalogNotice">
/// The notice for a <c>path</c> service whose catalog block was not run and whose developer declared
/// no block of their own, or <see langword="null"/> when there is nothing to say.
/// </param>
internal sealed record PreparePlan(PrepareStep? Step, string? IgnoredCatalogNotice)
{
    public static readonly PreparePlan Nothing = new(null, null);

    /// <summary>How a message names the catalog's block.</summary>
    private const string CatalogBlock = "prepare";

    /// <summary>How a message names the developer's.</summary>
    private const string DeveloperBlock = "local.prepare";

    /// <summary>
    /// Merges the catalog's block and the developer's into the one step that will run.
    /// </summary>
    /// <param name="managedCheckout">
    /// Whether this package owns the checkout directory. A <c>path</c> override means it does not,
    /// and such a service inherits no catalog block — see the remarks.
    /// </param>
    /// <param name="windows">
    /// Whether <see cref="PrepareMetadata.WindowsCommand"/> is the variant to use. A parameter
    /// rather than a call to <see cref="OperatingSystem.IsWindows"/> in here, so the Windows
    /// selection is testable from a Linux run — the same reason
    /// <c>JavaLocalResourceKind.WrapperForPlatform</c> takes one.
    /// </param>
    /// <remarks>
    /// <para>
    /// The developer's block is merged over the catalog's <b>per field</b>, with one exception:
    /// <c>mode</c> overrides if present, anything absent is inherited, and the command pair is
    /// replaced <em>together</em> if the developer supplies either half. Wholesale replacement was
    /// rejected because the two most valuable uses — disabling an inherited step and forcing it to
    /// re-run — would then require restating the command. The pair is exempted from the per-field
    /// rule because splitting it is never what anyone means: a developer who overrides
    /// <c>command</c> and says nothing about <c>windowsCommand</c> would otherwise run their own
    /// command on Linux and the catalog's on Windows.
    /// </para>
    /// <para>
    /// A <c>path</c> service <b>never inherits the catalog's block</b>. Nothing establishes that
    /// <c>path</c> points at the catalog's repository — the directory is validated by
    /// <c>Directory.Exists</c> and by nothing else — so a catalog command like <c>["npm", "ci"]</c>
    /// would run perfectly happily in a tree that has nothing to do with the repository the catalog
    /// names. And it is the developer's working tree, holding their in-flight work, where a
    /// repository's own bootstrap script is entitled to run <c>git clean</c>. Under this rule the
    /// command that runs there was written by whoever chose the directory, which is the only
    /// arrangement that makes that exposure theirs to accept. It is ignored rather than rejected,
    /// because a catalog block is the team's and applies correctly to every developer on a managed
    /// checkout: one developer's local override must not turn a shared catalog field into a failure.
    /// </para>
    /// </remarks>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// Either block names a mode that is not one of the four, or a command that names something
    /// outside the checkout — or, on a <c>path</c> service, a mode with no command to attach it to.
    /// </exception>
    public static PreparePlan For(
        string serviceName,
        PrepareMetadata? catalog,
        PrepareDeveloperConfig? developer,
        bool managedCheckout,
        bool windows)
    {
        // Parsed even where the value is about to be discarded — a mode on a block whose command
        // turns out to be absent, a catalog mode a `path` service ignores — because a value that
        // cannot be a mode is a mistake in a file whichever way resolution goes, and a developer who
        // typed it should hear about it rather than have it silently mean the default.
        var catalogMode = catalog is null ? null : ParseOptional(serviceName, catalog.Mode, CatalogBlock);
        var developerMode = developer is null ? null : ParseOptional(serviceName, developer.Mode, DeveloperBlock);

        return managedCheckout
            ? ForManagedCheckout(serviceName, catalog, developer, catalogMode, developerMode, windows)
            : ForPathCheckout(serviceName, catalog, developer, catalogMode, developerMode, windows);
    }

    private static PreparePlan ForManagedCheckout(
        string serviceName,
        PrepareMetadata? catalog,
        PrepareDeveloperConfig? developer,
        PrepareMode? catalogMode,
        PrepareMode? developerMode,
        bool windows)
    {
        var mode = developerMode ?? catalogMode ?? PrepareModes.Default;

        if (mode == PrepareMode.Never)
        {
            return Nothing;
        }

        // The command pair as a unit: the developer's if they supplied either half of it, the
        // catalog's otherwise.
        var developerSuppliedThePair =
            developer?.Command is not null || developer?.WindowsCommand is not null;

        var command = developerSuppliedThePair ? developer!.Command : catalog?.Command;
        var windowsCommand = developerSuppliedThePair ? developer!.WindowsCommand : catalog?.WindowsCommand;
        var writtenAt = developerSuppliedThePair ? DeveloperBlock : CatalogBlock;

        var selected = SelectPlatform(command, windowsCommand, windows);

        // A block that names no command anywhere. On a managed checkout a mode by itself is the
        // designed way to disable or force an inherited step, so a mode with nothing behind it is a
        // developer whose catalog has no step rather than a mistake to report.
        return selected is null
            ? Nothing
            : new PreparePlan(
                PrepareStep.Create(
                    serviceName, selected, mode, writtenAt, WindowsWithoutVariant(windowsCommand, windows)),
                null);
    }

    private static PreparePlan ForPathCheckout(
        string serviceName,
        PrepareMetadata? catalog,
        PrepareDeveloperConfig? developer,
        PrepareMode? catalogMode,
        PrepareMode? developerMode,
        bool windows)
    {
        var declared = developer?.IsDeclared == true;

        if (!declared)
        {
            // Nothing of the developer's, so nothing runs. Where the catalog declared a command, the
            // notice names it verbatim so it can be copied into the local file — which is what keeps
            // the duplication cheap enough to be the right trade. It repeats on every start until
            // the developer declares a block of their own, which is the only thing that resolves it
            // and so the only thing that silences it.
            // The catalog's mode is consulted even though a `path` service inherits nothing else
            // from the block, because it decides whether there is anything to be told about. A team
            // that has centrally disabled the step with `mode: never` has said that nothing should
            // run anywhere — asking every `path` developer, on every start, to copy in a command the
            // catalog itself has turned off is advice against the catalog's own instruction.
            var inherited = catalogMode == PrepareMode.Never
                ? null
                : SelectPlatform(catalog?.Command, catalog?.WindowsCommand, windows);

            return inherited is null
                ? Nothing
                : new PreparePlan(null, IgnoredCatalogStepNotice(serviceName, inherited));
        }

        var mode = developerMode ?? PrepareModes.Default;

        // Before the command is so much as looked at, exactly as the managed branch checks it:
        // `never` means run nothing, and it means that whether or not a command sits beside it. A
        // developer who declares both has disabled their own step — the same gesture that disables
        // an inherited one — and reading the command first would hand back a step to run in a
        // directory this tool does not own, which is the one thing 'path' plus `never` says will
        // not happen.
        if (mode == PrepareMode.Never)
        {
            return Nothing;
        }

        // The half of the block that cannot stand alone: a mode with no command anywhere in it.
        // Asked of the block as written rather than of the platform selection below, so a developer
        // who declared only a `windowsCommand` is not told about a mode they never set — on POSIX
        // their block simply has nothing to run, which is what the same block does on a managed
        // checkout. Scoped to `path` deliberately: there, a mode has no inherited command to apply
        // to, where for a managed checkout the identical block is the designed way to disable or
        // force one.
        if (developer!.Command is null && developer.WindowsCommand is null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': {DeveloperBlock}.mode is set to "
                + $"'{PrepareModes.Written(mode)}' but {DeveloperBlock}.command is not, and this service resolves "
                + "through 'local.path' — a checkout you manage yourself, which never inherits the catalog's "
                + $"'{CatalogBlock}' block, so there is no command for the mode to apply to. Add "
                + $"{DeveloperBlock}.command, or set the mode to 'never' to declare that nothing should run there.");
        }

        var command = SelectPlatform(developer.Command, developer.WindowsCommand, windows);

        // Nothing for this platform. A block carrying only the variant for the other one is a
        // deliberate statement — "on Windows run this, and I have no POSIX command" — so it runs
        // there and does nothing here, which is how the managed branch reads the same block.
        return command is null
            ? Nothing
            : new PreparePlan(
                PrepareStep.Create(
                    serviceName, command, mode, DeveloperBlock,
                    WindowsWithoutVariant(developer.WindowsCommand, windows)),
                null);
    }

    /// <summary>
    /// The command for the platform this AppHost is running on:
    /// <see cref="PrepareMetadata.WindowsCommand"/> replaces <see cref="PrepareMetadata.Command"/>
    /// on Windows, and with none set the command runs there unchanged.
    /// </summary>
    private static IReadOnlyList<string>? SelectPlatform(
        string[]? command, string[]? windowsCommand, bool windows) =>
        windows ? windowsCommand ?? command : command;

    /// <summary>
    /// Whether the command about to run is the cross-platform one on Windows, with no variant of its
    /// own — the likely cause when a command turns out not to start at all.
    /// </summary>
    private static bool WindowsWithoutVariant(string[]? windowsCommand, bool windows) =>
        windows && windowsCommand is null;

    private static PrepareMode? ParseOptional(string serviceName, string? written, string block) =>
        written is null ? null : PrepareModes.Parse(serviceName, written, $"{block}.mode");

    /// <remarks>
    /// Deliberately not the hard error that <c>ref</c> plus <c>path</c> is. Those are both the
    /// developer's own fields, so combining them is the developer contradicting themselves in a
    /// single file; a catalog <c>prepare</c> block is the team's and applies correctly to every
    /// developer on a managed checkout.
    /// </remarks>
    private static string IgnoredCatalogStepNotice(string serviceName, IReadOnlyList<string> command) =>
        $"Service '{serviceName}': its catalog entry declares a '{CatalogBlock}' step, which was not run — "
        + "this service resolves through 'local.path', a checkout you manage yourself, and nothing runs a "
        + "command in a directory this tool does not own unless you asked for it there. Nothing establishes "
        + "that the directory is even a checkout of the repository the catalog names. To run it, copy it into "
        + $"{DeveloperConfiguration.FileName}: \"{serviceName}\": {{ ..., \"local\": {{ \"{CatalogBlock}\": "
        + $"{{ \"command\": [{string.Join(", ", command.Select(argument => $"\"{argument}\""))}] }} }} }} — or "
        + $"declare {{ \"{CatalogBlock}\": {{ \"mode\": \"never\" }} }} to say that nothing should run there. "
        + "Either one silences this notice; it repeats on every start until one of them is there.";
}
