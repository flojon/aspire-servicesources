namespace Aspire.Hosting.ServiceSources.Git;

internal static class SeparateGitDirectories
{
    /// <summary>
    /// The per-user directory separate git directories live in, or <see langword="null"/> when the
    /// account has no local application data folder (some service accounts).
    /// </summary>
    public static string? DefaultRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "ServiceSources", "git");
    }
}
