namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Receives the lines a running <c>git</c> command writes to its progress stream, as it writes
/// them.
/// </summary>
/// <remarks>
/// Asking for one is what turns progress on: git suppresses it when stderr is not a terminal — and
/// a redirected child process never is — so the commands that can report it pass <c>--progress</c>
/// only when there is a sink to report to. See <see cref="CheckoutProgress"/> for the
/// implementation a deferred checkout watches through.
/// </remarks>
internal interface IGitProgressSink
{
    /// <summary>
    /// One line of git's progress stream, with the padding git writes to erase the previous line
    /// removed and any URL userinfo already redacted. Called from the thread draining git's stderr,
    /// several times a second during a large clone, so an implementation must not block.
    /// </summary>
    void Report(string line);
}
