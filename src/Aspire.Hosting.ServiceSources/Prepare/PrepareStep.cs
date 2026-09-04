using System.Security.Cryptography;
using System.Text;

namespace Aspire.Hosting.ServiceSources.Prepare;

/// <summary>
/// One service's <c>prepare</c> step, resolved: the platform's command already selected, the mode
/// already parsed, and every path already confined to the checkout. Nothing downstream re-applies a
/// default or re-checks a constraint.
/// </summary>
internal sealed class PrepareStep
{
    private PrepareStep(IReadOnlyList<string> command, PrepareMode mode, bool windowsWithoutVariant)
    {
        Command = command;
        Mode = mode;
        WindowsWithoutVariant = windowsWithoutVariant;
        CommandHash = HashOf(command);
    }

    /// <summary>
    /// The command to run, as argv. There is no shell between us and it.
    /// </summary>
    public IReadOnlyList<string> Command { get; }

    public PrepareMode Mode { get; }

    /// <summary>
    /// A hash of the resolved argv, which the completion marker records.
    /// </summary>
    /// <remarks>
    /// Of the <em>resolved</em> command rather than of the block, so a developer who switches from
    /// Linux to Windows and picks up the <c>windowsCommand</c> variant re-runs rather than
    /// inheriting a completion recorded for a different command. Hashed rather than stored verbatim
    /// because the marker is a record of a decision, not a copy of the configuration — and a
    /// command with a token in an argument should not be left lying in a file because this step ran.
    /// </remarks>
    public string CommandHash { get; }

    /// <summary>
    /// Whether this is the cross-platform command running on Windows because no
    /// <c>windowsCommand</c> variant was declared — which is correct for <c>npm</c>, <c>make</c>,
    /// <c>python</c> and <c>dotnet</c>, and the likely cause when the command turns out not to start
    /// at all. Only an exec failure can tell the two apart, so it is carried this far to be named
    /// there.
    /// </summary>
    public bool WindowsWithoutVariant { get; }

    /// <summary>The command as one readable line, for a log or a failure message.</summary>
    /// <remarks>
    /// An argument containing a space is quoted so that a two-element command cannot be misread as
    /// three. This is for a human to read; nothing parses it back.
    /// </remarks>
    public string Describe() =>
        string.Join(" ", Command.Select(argument =>
            argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument));

    /// <summary>
    /// Validates a command and mode that have already been merged, and confines the first element to
    /// the checkout.
    /// </summary>
    /// <param name="writtenAt">
    /// Which file's block this came from, as a message names it — <c>prepare</c> or
    /// <c>local.prepare</c>.
    /// </param>
    /// <exception cref="ServiceSourcesConfigurationException">
    /// The command is empty, holds a blank first element, or names something outside the checkout.
    /// </exception>
    public static PrepareStep Create(
        string serviceName,
        IReadOnlyList<string> command,
        PrepareMode mode,
        string writtenAt,
        bool windowsWithoutVariant = false)
    {
        if (command.Count == 0)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': {writtenAt}.command is an empty list, so there is no command to run. "
                + "Give it the program to run and its arguments, e.g. [\"./prepare.sh\"] — or set "
                + $"{writtenAt}.mode to 'never' to declare that nothing should run.");
        }

        var program = command[0];

        if (string.IsNullOrWhiteSpace(program))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': the first element of {writtenAt}.command is blank, so it names no "
                + "program. It has to be the program to run — a path inside the checkout, e.g. "
                + "\"./prepare.sh\", or a name resolved through PATH, e.g. \"make\".");
        }

        return new PrepareStep(
            [ConfineProgram(serviceName, program, writtenAt), .. command.Skip(1)], mode, windowsWithoutVariant);
    }

    /// <summary>
    /// Confines a first element that looks like a path to the checkout, and leaves a bare name for
    /// <c>PATH</c>.
    /// </summary>
    /// <remarks>
    /// "Looks like a path" is the shape the developer wrote rather than anything on disk: it starts
    /// with a <c>.</c> or contains a directory separator. That keeps <c>make</c>, <c>bash</c> and
    /// <c>npm</c> working as written while <c>./prepare.sh</c> and <c>scripts/bootstrap</c> are
    /// resolved against the checkout — and the check is lexical, so it holds in front of a clone
    /// that has not happened yet.
    /// <para>
    /// Confined for the reason <c>java.jarPath</c> is: <c>servicesources.yaml</c> is shared team
    /// configuration a developer clones rather than writes, so a climbing or absolute path would run
    /// something from outside the checkout the catalog describes. An absolute path is refused rather
    /// than allowed through as "obviously not in the checkout", because the two are different things
    /// to tell a developer — and because a bare name is how you reach a program that is genuinely
    /// elsewhere.
    /// </para>
    /// </remarks>
    private static string ConfineProgram(string serviceName, string program, string writtenAt)
    {
        if (!LooksLikeAPath(program))
        {
            return program;
        }

        if (CheckoutRelativePath.IsAbsolute(program))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': {writtenAt}.command runs '{program}', which is an absolute path. The "
                + "command has to be a path relative to the service's checkout — it names a script the repository "
                + "commits, not one sitting elsewhere on a developer's machine — or a bare program name resolved "
                + "through PATH.");
        }

        if (CheckoutRelativePath.EscapesRoot(program))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': {writtenAt}.command runs '{program}', which points outside the "
                + "service's checkout. It must stay within the repository.");
        }

        return CheckoutRelativePath.NormalizeSeparators(program);
    }

    /// <summary>
    /// Whether the first element is meant as a path in the checkout rather than as a program on
    /// <c>PATH</c>. Both separators count on every platform, for the reason
    /// <see cref="CheckoutRelativePath"/> counts both.
    /// </summary>
    private static bool LooksLikeAPath(string program) =>
        program.StartsWith('.') || program.Contains('/') || program.Contains('\\');

    /// <summary>
    /// The argv reduced to one value, with a separator no argument can contain so that
    /// <c>["ab", "c"]</c> and <c>["a", "bc"]</c> cannot hash alike.
    /// </summary>
    private static string HashOf(IReadOnlyList<string> command)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\0", command)));

        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
