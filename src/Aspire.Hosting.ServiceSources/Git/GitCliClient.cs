using System.Text.RegularExpressions;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// An <see cref="IGitClient"/> that drives the <c>git</c> executable on <c>PATH</c>.
/// </summary>
/// <remarks>
/// <para>
/// Shelling out rather than linking a library is what keeps this package free of any
/// source-specific native dependency — the same trade the <c>"kubernetes"</c> source already makes
/// with <c>kubectl</c>. It also means every git operation runs under the developer's own git: their
/// credential helper, their SSH agent, their <c>~/.gitconfig</c>, their proxy settings, with
/// nothing for this package to re-plumb.
/// </para>
/// <para>
/// Stateless, so the concurrent checkouts <see cref="Sources.LocalCheckoutPrefetch"/> starts share
/// nothing.
/// </para>
/// </remarks>
/// <param name="environmentOverrides">
/// Variables to set or (when the value is null) remove from the environment git runs under, in
/// place of the AppHost process's own. Production passes nothing and inherits it. A test passes an
/// isolated one: which credentials git can reach is decided entirely by its environment and
/// config, so without this the suite's results would depend on the credential helper the machine
/// running it happens to have configured.
/// </param>
internal sealed partial class GitCliClient(
    IReadOnlyDictionary<string, string?>? environmentOverrides = null) : IGitClient
{
    private const string UsernameEnvironmentVariable = "SERVICESOURCES_GIT_USERNAME";
    private const string TokenEnvironmentVariable = "SERVICESOURCES_GIT_TOKEN";

    /// <summary>
    /// A <c>credential.helper</c> that answers from
    /// <c>SERVICESOURCES_GIT_USERNAME</c>/<c>SERVICESOURCES_GIT_TOKEN</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The token is read from the environment by the helper itself rather than substituted into
    /// this string, so the secret never appears in the command line of a process — where any user
    /// on the machine could read it out of <c>ps</c>.
    /// </para>
    /// <para>
    /// Deliberately free of backslashes and quotes. Git hands a <c>-c</c> value to the config
    /// parser, which historically read those as escapes, and a mangled helper would fail in a way
    /// that looks like a rejected credential. <c>echo</c> of an unquoted-safe expansion needs
    /// neither.
    /// </para>
    /// <para>
    /// Silent unless it has a token to give, so git falls through to whatever comes next instead of
    /// answering the challenge with an empty password.
    /// </para>
    /// </remarks>
    private const string EnvironmentCredentialHelper =
        "!f() { " +
        "test \"$1\" = get || exit 0; " +
        "test -n \"$" + TokenEnvironmentVariable + "\" || exit 0; " +
        "echo \"username=${" + UsernameEnvironmentVariable + ":-git}\"; " +
        "echo \"password=$" + TokenEnvironmentVariable + "\"; " +
        "}; f";

    /// <summary>
    /// Probed once per process: whether <c>git</c> can be run at all, and the message to report if
    /// it can't. An AppHost resolves many services, and the answer cannot change under it.
    /// </summary>
    private static readonly Lazy<string?> Unavailability =
        new(ProbeUnavailability, LazyThreadSafetyMode.ExecutionAndPublication);

    public void EnsureAvailable()
    {
        if (Unavailability.Value is { } reason)
        {
            throw new ServiceSourcesConfigurationException(reason);
        }
    }

    private static string? ProbeUnavailability()
    {
        try
        {
            return GitCommand.Run(["--version"]).Succeeded
                ? null
                : "'git' is on PATH but 'git --version' failed, so the 'local' source cannot clone or update " +
                  "checkouts. Repair the git installation, or give each service a 'path' override in " +
                  "servicesources.local.json to point at a checkout you manage yourself.";
        }
        catch (GitUnavailableException ex)
        {
            return "The 'local' source clones and updates service repositories with 'git', which was not found on " +
                   $"PATH ({ex.Message}). Install git (2.7 or newer), or give each service a 'path' override in " +
                   "servicesources.local.json to point at a checkout you manage yourself.";
        }
    }

    public void Clone(string repositoryUrl, string destinationPath) =>
        // "--" so a repository URL or a destination that begins with '-' is read as an argument
        // rather than as an option.
        RunRemoteCommand(["clone", "--", repositoryUrl, destinationPath]);

    public void Checkout(string repositoryPath, string reference)
    {
        if (IsSafeReference(reference))
        {
            // A local branch is checked out as itself, so committing on it and pushing works the
            // way the developer expects.
            if (RefExists(repositoryPath, $"refs/heads/{reference}"))
            {
                Run(repositoryPath, ["checkout", reference]);
                return;
            }

            // A branch that so far only exists on origin becomes a local branch tracking it, which
            // is what `git clone` itself does for the default branch and what `git checkout
            // <branch>` does by DWIM. Spelled out rather than left to DWIM so the "no such ref"
            // case below is reached by a decision of ours instead of by pattern-matching git's
            // error text.
            if (RefExists(repositoryPath, $"refs/remotes/origin/{reference}"))
            {
                Run(repositoryPath, ["checkout", "-b", reference, "--track", $"origin/{reference}"]);
                return;
            }

            // A tag or a commit has no branch to be on, so it lands on a detached HEAD. The
            // resolved id is passed rather than the name, so what is checked out is exactly what
            // was probed for.
            if (ResolveRef(repositoryPath, reference) is { } commit)
            {
                Run(repositoryPath, ["checkout", "--detach", commit]);
                return;
            }
        }

        throw new ServiceSourcesConfigurationException(
            $"Ref '{reference}' was not found in repository at '{repositoryPath}'.");
    }

    public void Fetch(string repositoryPath)
    {
        // A checkout with no origin — one made from a local path with the remote since removed, or
        // an unrelated repository the developer put in place — has nothing to fetch from. Probed
        // rather than inferred from a failed fetch, so a genuine fetch failure still surfaces.
        if (GetOriginUrl(repositoryPath) is null)
        {
            return;
        }

        RunRemoteCommand(["-C", repositoryPath, "fetch", "origin"]);
    }

    public bool HasUncommittedChanges(string repositoryPath) =>
        // --untracked-files=no deliberately ignores untracked files: build output (bin/obj) left
        // behind by a plain `dotnet build` shouldn't make an otherwise-clean checkout look
        // permanently dirty.
        Run(repositoryPath, ["status", "--porcelain", "--untracked-files=no"]).FirstLine.Length > 0;

    public bool IsRefCheckedOut(string repositoryPath, string reference)
    {
        var head = TryRun(repositoryPath, ["rev-parse", "--verify", "--quiet", "HEAD"]);
        if (!head.Succeeded)
        {
            // An unborn HEAD: a repository with no commits, so nothing is checked out.
            return false;
        }

        return ResolveCommit(repositoryPath, reference) == head.FirstLine;
    }

    public string? GetOriginUrl(string repositoryPath)
    {
        var result = TryRun(repositoryPath, ["remote", "get-url", "origin"]);
        return result.Succeeded ? result.FirstLine : null;
    }

    /// <summary>
    /// The commit <paramref name="reference"/> names, looked up locally and with no network access,
    /// or <see langword="null"/> if it names nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved in the same order <see cref="Checkout"/> acts on — local branch, then the branch on
    /// origin, then anything else — so the two never disagree about which object a name refers to.
    /// git's own precedence puts tags ahead of branches, which would make a repository holding both
    /// a branch and a tag of one name check out one and compare against the other.
    /// </para>
    /// <para>
    /// <c>^{commit}</c> peels an annotated tag to the commit it points at, so a tag compares equal
    /// to a HEAD sitting on that commit rather than to the tag object's own id.
    /// </para>
    /// </remarks>
    private string? ResolveCommit(string repositoryPath, string reference) =>
        IsSafeReference(reference)
            ? ResolveRef(repositoryPath, $"refs/heads/{reference}")
              ?? ResolveRef(repositoryPath, $"refs/remotes/origin/{reference}")
              ?? ResolveRef(repositoryPath, reference)
            : null;

    private string? ResolveRef(string repositoryPath, string reference)
    {
        var result = TryRun(repositoryPath, ["rev-parse", "--verify", "--quiet", $"{reference}^{{commit}}"]);
        return result.Succeeded ? result.FirstLine : null;
    }

    private bool RefExists(string repositoryPath, string fullyQualifiedRef) =>
        TryRun(repositoryPath, ["rev-parse", "--verify", "--quiet", fullyQualifiedRef]).Succeeded;

    /// <summary>
    /// Whether the reference can be passed to git as a positional argument. A name starting with
    /// '-' would be read as an option instead, and git's own rules forbid one, so rejecting it here
    /// lets the caller report "no such ref" rather than surfacing an option-parsing error.
    /// </summary>
    private static bool IsSafeReference(string reference) =>
        reference.Length > 0 && reference[0] != '-';

    /// <summary>
    /// Runs a git command that must succeed, throwing <see cref="GitCommandFailedException"/> with
    /// git's own stderr if it doesn't.
    /// </summary>
    private GitCommandResult Run(string repositoryPath, IReadOnlyList<string> arguments)
    {
        var result = TryRun(repositoryPath, arguments);
        if (!result.Succeeded)
        {
            throw new GitCommandFailedException(Describe(result));
        }

        return result;
    }

    private GitCommandResult TryRun(string repositoryPath, IReadOnlyList<string> arguments) =>
        GitCommand.Run(["-C", repositoryPath, .. arguments], environmentOverrides);

    /// <summary>
    /// Runs a git command that talks to a remote, applying the credential ladder and translating an
    /// authentication-shaped failure into <see cref="GitAuthenticationFailedException"/>.
    /// </summary>
    /// <remarks>
    /// The ladder is two rungs, and it is the developer's own git that climbs the first one: git
    /// consults every configured <c>credential.helper</c> before the one appended here, so an
    /// existing Git Credential Manager, <c>osxkeychain</c> or <c>libsecret</c> setup wins and
    /// <c>SERVICESOURCES_GIT_TOKEN</c> is only reached when none of them answers. The second rung
    /// exists because git stops at the first helper that answers: if that answer is refused, the
    /// whole command fails without the environment token ever being offered. Re-running with the
    /// configured helpers cleared gives the token its turn.
    /// </remarks>
    private void RunRemoteCommand(IReadOnlyList<string> arguments)
    {
        var result = GitCommand.Run([.. CredentialLadderOptions(), .. arguments], environmentOverrides);
        if (result.Succeeded)
        {
            return;
        }

        if (HasEnvironmentToken && LooksLikeAuthFailure(result.StandardError))
        {
            // Safe to simply re-run: a failed `git clone` removes the directory it created, and a
            // failed `git fetch` leaves the checkout as it was.
            var retry = GitCommand.Run(
                [.. EnvironmentOnlyCredentialOptions(), .. arguments], environmentOverrides);
            if (retry.Succeeded)
            {
                return;
            }

            result = retry;
        }

        if (LooksLikeAuthFailure(result.StandardError))
        {
            var message = Describe(result);
            throw new GitAuthenticationFailedException(
                message,
                new GitCommandFailedException(message),
                ResolvedNoCredentials(result.StandardError));
        }

        throw new GitCommandFailedException(Describe(result));
    }

    /// <summary>
    /// Whether a token is set in the environment git will run under — which is what decides
    /// the second rung, so it has to be read from the same place git reads it.
    /// </summary>
    private bool HasEnvironmentToken =>
        !string.IsNullOrEmpty(
            environmentOverrides is not null
            && environmentOverrides.TryGetValue(TokenEnvironmentVariable, out var overridden)
                ? overridden
                : Environment.GetEnvironmentVariable(TokenEnvironmentVariable));

    /// <summary>
    /// Appends the environment-variable helper after whatever the developer has configured, so it
    /// is consulted only when nothing else answers.
    /// </summary>
    private string[] CredentialLadderOptions() =>
        HasEnvironmentToken ? ["-c", $"credential.helper={EnvironmentCredentialHelper}"] : [];

    /// <summary>
    /// Clears the configured helpers — an empty <c>credential.helper</c> resets the list — and
    /// leaves only the environment-variable one, for the retry after a configured helper's
    /// credential was refused.
    /// </summary>
    private static string[] EnvironmentOnlyCredentialOptions() =>
        ["-c", "credential.helper=", "-c", $"credential.helper={EnvironmentCredentialHelper}"];

    /// <summary>
    /// git's own words for a failure, preferring stderr and falling back to the exit code when a
    /// command fails silently.
    /// </summary>
    private static string Describe(GitCommandResult result)
    {
        var stderr = result.StandardError.Trim();
        return stderr.Length > 0 ? stderr : $"git exited with code {result.ExitCode}.";
    }

    /// <summary>
    /// Whether a failed remote operation looks like a rejected or missing credential, so the caller
    /// can name authentication as the likely cause instead of reporting a generic clone/fetch
    /// failure.
    /// </summary>
    internal static bool LooksLikeAuthFailure(string message)
    {
        // An unverified host key is a trust decision, not a credential: the developer's fix is to
        // add the host to known_hosts, and sending them to check their token instead wastes the
        // diagnosis. git's own message already says exactly what to do.
        if (message.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ResolvedNoCredentials(message)
            || message.Contains("authentication", StringComparison.OrdinalIgnoreCase)
            || message.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
            || message.Contains("access denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid username or password", StringComparison.OrdinalIgnoreCase)
            // What ssh says when every key it had was refused, and the umbrella message git prints
            // after it. Both cover "the repository isn't there" as well as "your key can't see it",
            // which is the same ambiguity the HTTP 404 below carries.
            || message.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
            || message.Contains("correct access rights", StringComparison.OrdinalIgnoreCase)
            || HasHttpStatus(message, "401")
            // A 403 is "authenticated, but not allowed", which over git-on-HTTPS is usually a token
            // missing a scope or an SSO session the developer hasn't authorized — a credential
            // problem, and one worth naming. Being throttled answers with that same status while
            // saying nothing about the credential, so those are excluded: pointing at
            // authentication there sends the developer after the wrong cause.
            || (HasHttpStatus(message, "403") && !LooksLikeThrottling(message))
            // GitHub, GitLab and Azure DevOps all answer an unauthenticated request for a private
            // repository with 404 rather than 401, so as not to leak whether it exists. A remote
            // "not found" is therefore far more often a missing credential than an absent
            // repository — which is exactly the case this detection exists to explain. The caller's
            // message is worded to cover both readings.
            || HasHttpStatus(message, "404")
            || RemoteRepositoryNotFound().IsMatch(message);
    }

    /// <summary>
    /// Whether git got as far as needing a credential and had none: every helper declined and it
    /// fell through to asking a human, which is disabled. That is a different problem from a
    /// credential the host refused, and needs different remediation, so the two are told apart.
    /// </summary>
    internal static bool ResolvedNoCredentials(string message) =>
        message.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)
        || message.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
        || message.Contains("could not read Password", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the message reports a request turned away for coming too often, rather than for the
    /// credential it carried. Only the host's own wording can tell the two apart, so this catches
    /// what the major hosts say when they throttle and leaves the rest reading as a credential
    /// problem.
    /// </summary>
    private static bool LooksLikeThrottling(string message) =>
        message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
        || message.Contains("rate-limit", StringComparison.OrdinalIgnoreCase)
        || message.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)
        || message.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
        || message.Contains("throttl", StringComparison.OrdinalIgnoreCase)
        || message.Contains("try again later", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the message reports the given HTTP status, as git and the hosts word it ("The
    /// requested URL returned error: 404"). Anchoring on the phrase rather than the bare digits
    /// keeps a port number, an object id or a byte count that happens to contain them from being
    /// read as a rejected credential — a false positive costs the developer a wrong diagnosis.
    /// </summary>
    private static bool HasHttpStatus(string message, string statusCode) =>
        message.Contains($"returned error: {statusCode}", StringComparison.OrdinalIgnoreCase)
        || message.Contains($"status code: {statusCode}", StringComparison.OrdinalIgnoreCase)
        || message.Contains($"status code {statusCode}", StringComparison.OrdinalIgnoreCase)
        || message.Contains($"HTTP {statusCode}", StringComparison.OrdinalIgnoreCase)
        || message.Contains($"error {statusCode}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The host's and git's wordings for a repository the request wasn't allowed to see:
    /// "remote: Repository not found." from GitHub, and git's own
    /// "fatal: repository '&lt;url&gt;' not found".
    /// </summary>
    /// <remarks>
    /// Matched as a phrase rather than on a bare "not found" so a local lookup miss —
    /// "Reference 'refs/heads/feature' not found", "object not found" — is not read as a rejected
    /// credential. Note that git words an absent *local* path differently ("repository '/x' does
    /// not exist"), which this deliberately does not match.
    /// </remarks>
    [GeneratedRegex(@"repository\s*(?:'[^']*'\s*)?not found", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteRepositoryNotFound();
}
