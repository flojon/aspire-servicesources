namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The lexical checks that keep a path written in shared team configuration inside the checkout it
/// is read relative to, and rewrite its separators for the platform the AppHost runs on.
/// </summary>
/// <remarks>
/// Lexical rather than resolved, so the verdict does not depend on a checkout being there to
/// resolve against: a value is judged while the clone may not have happened yet — <see
/// cref="Java.JavaLocalResourceKind.ResolveDeferred"/> parses its block for a checkout that has not
/// landed, and a <c>prepare</c> command is validated in front of the clone — and a value pointing
/// outside the repository is a mistake whichever way the checkout went. It also keeps the two
/// failures apart: "points outside the checkout" is a different thing to tell a developer than "is
/// not in the checkout".
/// </remarks>
internal static class CheckoutRelativePath
{
    /// <summary>
    /// Whether <paramref name="path"/> is absolute on <em>any</em> platform, rather than only on this
    /// one. <see cref="Path.IsPathRooted"/> alone is platform-dependent, so a Windows-style value
    /// ('C:\repos\api', '\\server\share') sails past it on Linux/macOS and is then reported as a
    /// directory missing from the checkout instead of as the absolute path it is.
    /// </summary>
    /// <remarks>
    /// An empty path is not absolute, and says so rather than throwing: every caller happens to have
    /// rejected empty before reaching here, but the indexing below is one call site away from being
    /// an <see cref="IndexOutOfRangeException"/> for a caller that has not.
    /// </remarks>
    public static bool IsAbsolute(string path) =>
        path.Length > 0
        && (Path.IsPathRooted(path)
            || path[0] is '/' or '\\'
            || (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])));

    /// <summary>
    /// Whether <paramref name="relativePath"/> climbs above the directory it is relative to, or
    /// contains a segment <see cref="UnusableSegment"/> would name. Both separators count regardless
    /// of platform, so a Windows-style relative value ('..\sibling') is still rejected on
    /// Linux/macOS; a rooted one ('C:\repos') never reaches here, being caught by
    /// <see cref="IsAbsolute"/> first.
    /// </summary>
    /// <remarks>
    /// Counted rather than pattern-matched, so a '..' that a preceding segment pays for
    /// ('src/../Orders.csproj') never leaves the checkout and is not refused. Shares its walk with
    /// <see cref="UnusableSegment"/> through <see cref="Classify"/>, so the two cannot disagree about
    /// which segment a path is refused for: a caller that only calls this still refuses a value
    /// <see cref="UnusableSegment"/> would flag, and one that calls both is told the same first
    /// problem the scan actually hit.
    /// </remarks>
    public static bool EscapesRoot(string relativePath) => Classify(relativePath).Refused;

    /// <summary>
    /// The segment <paramref name="relativePath"/> is refused for being made only of dots and
    /// spaces, or <see langword="null"/> if it is accepted, or refused for climbing out instead.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="EscapesRoot"/> because the two are different things to tell a
    /// developer, the distinction this whole type exists to keep: '../Evil.csproj' is a
    /// <em>quantity</em> mistake — it climbs one level too many, and the fix is to climb less — while
    /// '.../Api.csproj' is a <em>spelling</em> mistake, and the value may well name a file that is
    /// sitting inside the checkout. Telling the second developer their path "points outside the
    /// checkout" states something that is false on the platform they are running on, and names no
    /// segment for them to go and look at — which matters most for '.. ', whose trailing space is
    /// invisible in a terminal and in most editors.
    /// </para>
    /// <para>
    /// Returns the segment rather than a <see langword="bool"/> for that last reason: quoting it
    /// back is what makes an invisible character visible.
    /// </para>
    /// <para>
    /// Reports whichever problem <see cref="Classify"/>'s single left-to-right scan reaches first,
    /// rather than scanning separately for one. A second, independent scan that skipped '.'/'..' and
    /// kept looking for a dots-and-spaces segment could find one <em>after</em> a climb that already
    /// refused the path — 'root/../.../Orders.csproj' climbs out at the leading '..', which a
    /// developer reading left to right hits first, but a scan that skipped past it would report the
    /// unrelated '...' further along instead. One scan, one verdict, avoids that.
    /// </para>
    /// </remarks>
    public static string? UnusableSegment(string relativePath) => Classify(relativePath).UnusableSegment;

    /// <summary>
    /// The single left-to-right scan both <see cref="EscapesRoot"/> and <see cref="UnusableSegment"/>
    /// answer from, so they report the same first problem rather than each finding a different one.
    /// </summary>
    private static (bool Refused, string? UnusableSegment) Classify(string relativePath)
    {
        var depth = 0;
        foreach (var segment in relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (--depth < 0)
                {
                    return (true, null);
                }

                continue;
            }

            // After the two exact tests above, because trimming maps '.', '..' and '...' alike to
            // nothing and the first two have meanings this must not change.
            if (IsOnlyDotsAndSpaces(segment))
            {
                return (true, segment);
            }

            depth++;
        }

        return (false, null);
    }

    /// <summary>
    /// Whether <paramref name="segment"/> — one path component — has nothing left once its trailing
    /// dots and spaces are removed.
    /// </summary>
    /// <remarks>
    /// The primitive under both this type's <see cref="UnusableSegment"/> and the service-name rule
    /// in <see cref="Git.LocalGitCheckout.IsContainedCheckoutDirectoryName"/> (#224). The two rules
    /// stay separate — a path may have many segments, which a service name may not — but they must
    /// not disagree about what a segment made only of dots and spaces <em>is</em>, and sharing the
    /// test is what stops them drifting apart, rather than two comments claiming they agree.
    /// </remarks>
    public static bool IsOnlyDotsAndSpaces(string segment) => segment.TrimEnd('.', ' ').Length == 0;

    /// <summary>
    /// The half of a refusal that is the same wherever a path is turned away for containing a
    /// segment made only of dots and spaces: what the rule is, and what still works.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Shared by the five places that refuse, for the reason
    /// <see cref="Git.LocalGitCheckout.ContainedNameRuleAndRemedy"/> gives for the service-name
    /// rule: messages that each describe the rule in their own words drift apart from it, and that
    /// has already happened once in this codebase.
    /// </para>
    /// <para>
    /// What the rule rests on, stated only as far as it can be defended. Per Microsoft's
    /// <see href="https://learn.microsoft.com/dotnet/standard/io/file-path-formats">path
    /// normalization rules</see>, Windows removes all trailing dots and spaces from a path that does
    /// not end in a separator — from the end of the <em>path</em>, not from every component — and a
    /// segment of three or more dots is otherwise left alone, being "a valid file/directory name".
    /// So the same written value means two things: on Linux and macOS, and mid-path on Windows, such
    /// a segment is an ordinary directory; as the last segment on Windows it is erased, and the path
    /// names the directory above it instead. Refusing it everywhere is what makes shared
    /// configuration mean one thing on every developer's machine — the same reasoning the rest of
    /// this type gives for judging a path lexically rather than resolving it.
    /// </para>
    /// <para>
    /// No claim is made that such a segment lets a path climb out of the checkout. Trimming happens
    /// after '.' and '..' are evaluated, so a trimmed segment is not re-read as a parent reference,
    /// and this repository has no Windows leg to check the point on either way.
    /// </para>
    /// </remarks>
    public static string OnlyDotsAndSpacesRuleAndRemedy =>
        "a segment made only of dots and spaces does not mean the same thing on every platform: it "
        + "is an ordinary directory name on Linux and macOS, while Windows removes trailing dots and "
        + "spaces from the end of a path, so as the last segment it is erased and the path names the "
        + "directory above it instead. Rewrite that segment — if it names a real, committed "
        + "directory, rename the directory itself, not just this value. '.' and '..' are unaffected, "
        + "and a segment with anything left after its trailing dots and spaces ('orders.') is fine.";

    /// <summary>
    /// Rewrites the separators of an accepted relative path for the platform the app host is running
    /// on. The validation above counts <c>'\'</c> as a separator so a Windows-style value is judged
    /// as the path it is, which means such a value is <em>accepted</em> on Linux and macOS too; the
    /// value is then handed to <see cref="Path.Combine(string, string)"/>, where an unrewritten
    /// <c>'services\catalog'</c> would resolve to a single oddly-named directory and be reported as
    /// missing from the checkout. Only <c>'\'</c> needs rewriting: Windows accepts <c>'/'</c> as a
    /// separator, so on Windows this is a no-op.
    /// </summary>
    public static string NormalizeSeparators(string relativePath) =>
        relativePath.Replace('\\', Path.DirectorySeparatorChar);
}
