namespace Aspire.Hosting.ServiceSources.Prepare;

/// <summary>
/// How often a <c>prepare</c> step runs. All four answer one question, and the guards nest:
/// <see cref="Once"/> is <see cref="OncePerCommit"/> minus the commit, <see cref="Always"/> is
/// <see cref="Once"/> minus the command.
/// </summary>
internal enum PrepareMode
{
    /// <summary>
    /// Re-runs when either the command or the checked-out commit moves. For a bootstrap defined by
    /// a script the repository commits: a team bumping <c>prepare.sh</c> moves the commit, and every
    /// developer picks the new one up.
    /// </summary>
    OncePerCommit,

    /// <summary>
    /// Re-runs only when the command changes, so it stays satisfied as the commit moves under it.
    /// For an expensive bootstrap whose behaviour is fixed independently of the repository — a jar
    /// pinned by the filename in the catalog, a country-sized extract that has nothing to do with
    /// the commit.
    /// </summary>
    Once,

    /// <summary>
    /// Runs on every start, consulting no marker. For an incremental script that decides its own
    /// work — which is the shape this design asks for rather than approximating, since <c>make</c>,
    /// Gradle, MSBuild and <c>npm ci</c> answer "is this up to date" with real dependency graphs
    /// behind them.
    /// </summary>
    Always,

    /// <summary>
    /// Runs nothing and writes nothing. How a developer opts out of a step the catalog declared,
    /// without editing shared team configuration.
    /// </summary>
    Never,
}

/// <summary>
/// Reading a <see cref="PrepareMode"/> out of either file, in one wording.
/// </summary>
internal static class PrepareModes
{
    /// <summary>
    /// What an unspecified <c>mode</c> means.
    /// </summary>
    /// <remarks>
    /// <see cref="PrepareMode.OncePerCommit"/> is the default on which failure is worse for someone
    /// who has not read the design. Its wrong answer is an unexpected re-run after the developer's
    /// own commit: annoying, immediately visible, and fixed by writing <c>once</c>.
    /// <see cref="PrepareMode.Once"/>'s wrong answer is running last month's artifact after the team
    /// updated the bootstrap: invisible, surfacing later as a confusing runtime failure. Visible
    /// slowness beats silent wrongness.
    /// <para>
    /// It is also why the default is not <em>called</em> <c>once</c>: a mode by that name which
    /// silently re-runs whenever HEAD moves is a name that lies, and the mode a developer reaches
    /// for when they want the step to run exactly one time should be the one called <c>once</c>.
    /// </para>
    /// </remarks>
    public const PrepareMode Default = PrepareMode.OncePerCommit;

    /// <summary>The four spellings, in the order the modes are documented in.</summary>
    private static readonly (string Written, PrepareMode Mode)[] Spellings =
    [
        ("oncePerCommit", PrepareMode.OncePerCommit),
        ("once", PrepareMode.Once),
        ("always", PrepareMode.Always),
        ("never", PrepareMode.Never),
    ];

    /// <summary>
    /// Parses <paramref name="written"/>, or returns <see cref="Default"/> when nothing was written.
    /// </summary>
    /// <param name="writtenAt">
    /// Where the value came from, as the message shows it — <c>prepare.mode</c> for the catalog,
    /// <c>local.prepare.mode</c> for the developer's file — so a reader knows which of the two files
    /// to open.
    /// </param>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// <paramref name="written"/> is not one of the four.
    /// </exception>
    public static PrepareMode Parse(string serviceName, string? written, string writtenAt)
    {
        if (written is null)
        {
            return Default;
        }

        // Case-insensitively, because a value can arrive from an environment variable, where a
        // developer writing ONCEPERCOMMIT is not making a mistake about the mode.
        foreach (var (spelling, mode) in Spellings)
        {
            if (string.Equals(written, spelling, StringComparison.OrdinalIgnoreCase))
            {
                return mode;
            }
        }

        throw new ServiceSourcesConfigurationException(
            $"Service '{serviceName}': {writtenAt} is '{written}', which is not a mode. Set it to one of "
            + string.Join(", ", Spellings.Select(s => $"'{s.Written}'"))
            + $" — or leave it out for '{Written(Default)}'.");
    }

    /// <summary>How <paramref name="mode"/> is spelled in a file, for a message that names one.</summary>
    public static string Written(PrepareMode mode) =>
        Spellings.First(spelling => spelling.Mode == mode).Written;
}
