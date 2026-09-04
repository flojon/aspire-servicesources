namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The tool-owned directory under the AppHost — <c>.servicesources</c> — and the one file it owns
/// unconditionally: the <c>.gitignore</c> that keeps everything inside it out of the AppHost
/// repository's status.
/// </summary>
/// <remarks>
/// Separate from <see cref="Git.LocalGitCheckout"/>, which created this directory for as long as
/// checkouts were the only thing in it. Creating a directory and writing a <c>.gitignore</c> is not
/// git logic, and there are now two callers with nothing else in common: a managed checkout, which
/// also wants the <see cref="Git.CheckoutBuildBarrier"/> written alongside, and a <c>prepare</c>
/// step's marker for a <c>path</c> checkout, which is the one thing here that never clones anything.
/// <para>
/// Called at the point of use rather than up front. An AppHost whose services all use <c>path</c>
/// and declare no <c>prepare</c> step has nothing to keep here, and should not acquire the directory
/// for it.
/// </para>
/// </remarks>
internal static class ToolDirectory
{
    public const string Name = ".servicesources";

    /// <summary>Where the directory lives. A pure function; nothing is created.</summary>
    public static string PathIn(string appHostDirectory) => Path.Combine(appHostDirectory, Name);

    /// <summary>
    /// Creates the directory and its <c>.gitignore</c> if they are not there, and returns the path.
    /// </summary>
    public static string Ensure(string appHostDirectory)
    {
        var dir = PathIn(appHostDirectory);
        Directory.CreateDirectory(dir);

        EnsureGitignore(dir);

        return dir;
    }

    private static void EnsureGitignore(string dir)
    {
        var gitignorePath = Path.Combine(dir, ".gitignore");
        try
        {
            // FileMode.CreateNew is atomic: it fails if the file already exists, which makes
            // this safe against concurrent resolution of multiple services (see
            // Sources.LocalCheckoutPrefetch, which clones them in parallel) racing to create it.
            using var stream = new FileStream(gitignorePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write("*\n!.gitignore\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Already created by a concurrent resolution or a prior run — leave it as-is. A
            // directory that cannot be written to at all reaches here as UnauthorizedAccessException
            // rather than IOException, and it must be tolerated for the same reason
            // CheckoutBuildBarrier tolerates it: what this file buys is a checkout kept out of the
            // AppHost's git status, which is not worth failing service resolution over.
        }
    }
}
