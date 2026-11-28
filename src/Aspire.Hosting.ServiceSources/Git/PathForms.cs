namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>Lexical path tests that never touch the filesystem.</summary>
internal static class PathForms
{
    /// <summary>
    /// Whether <paramref name="path"/> is a rooted, local path: not UNC, not a <c>\\?\</c> or
    /// <c>\\.\</c> device form, and on Windows carrying a drive.
    /// </summary>
    public static bool IsLocalAbsolute(string path)
    {
        if (!Path.IsPathRooted(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal)
            || path.StartsWith(@"\?", StringComparison.Ordinal)
            || path.StartsWith(@"\.", StringComparison.Ordinal))
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        // "C:foo" and "\foo" are rooted but resolve against the process's current directory or drive.
        return path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
    }
}
