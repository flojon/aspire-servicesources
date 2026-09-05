namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The lexical checks that keep a path written in shared team configuration inside the checkout it
/// is read relative to, and rewrite its separators for the platform the AppHost runs on.
/// </summary>
/// <remarks>
/// Lexical rather than resolved, so the verdict does not depend on a checkout being there to
/// resolve against: a value is judged while the clone may not have happened yet — a deferred
/// service's <c>project</c> is turned into a path for a checkout that has not landed — and a value
/// pointing outside the repository is a mistake whichever way the checkout went. It also keeps the
/// two failures apart: "points outside the checkout" is a different thing to tell a developer than
/// "is not in the checkout".
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
    /// Whether <paramref name="relativePath"/> climbs above the directory it is relative to. Both
    /// separators count regardless of platform, so a Windows-style relative value ('..\sibling') is
    /// still rejected on Linux/macOS; a rooted one ('C:\repos') never reaches here, being caught by
    /// <see cref="IsAbsolute"/> first.
    /// </summary>
    public static bool EscapesRoot(string relativePath)
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
                    return true;
                }
            }
            else
            {
                depth++;
            }
        }

        return false;
    }

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
