namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// The key two <c>"path"</c> services must share for their builds to be serialized: an explicit
/// <c>buildGroup</c> name, else the repository the service directory sits in, else the directory itself.
/// </summary>
/// <remarks>
/// Lexical, like the package's other confinement checks: no symlink or junction resolution. An explicit
/// name is <c>g:&lt;name&gt;</c> and a derived one <c>p:&lt;full path&gt;</c>, so a name can never equal a path.
/// </remarks>
internal static class BuildGroupKey
{
    private const string GroupPrefix = "g:";
    private const string PathPrefix = "p:";

    public static IEqualityComparer<string> Comparer { get; } = new KeyComparer();

    public static string For(string serviceDirectory, string? buildGroup, Func<string?> configuredRoot)
    {
        if (buildGroup is not null)
        {
            return GroupPrefix + buildGroup;
        }

        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(serviceDirectory));
        var root = PathSource.ConfinementRootOf(directory, () => ApplicableConfiguredRoot(directory, configuredRoot));

        return PathPrefix + Path.TrimEndingDirectorySeparator(root.Directory);
    }

    // An override outside the configured root must not join it, and a bad root is not this service's mistake to report.
    private static string? ApplicableConfiguredRoot(string directory, Func<string?> configuredRoot)
    {
        try
        {
            if (configuredRoot() is not { } root)
            {
                return null;
            }

            var relative = Path.GetRelativePath(root, directory);
            var outside = Path.IsPathRooted(relative)
                || relative == ".."
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);

            return outside ? null : root;
        }
        catch (ServiceSourcesConfigurationException)
        {
            return null;
        }
    }

    private sealed class KeyComparer : IEqualityComparer<string>
    {
        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        public bool Equals(string? x, string? y) => For(x).Equals(x, y);

        public int GetHashCode(string obj) => For(obj).GetHashCode(obj);

        private static StringComparer For(string? key) =>
            key is not null && key.StartsWith(GroupPrefix, StringComparison.Ordinal) ? StringComparer.Ordinal : PathComparer;
    }
}
