namespace Aspire.Hosting.ServiceSources.Tests;

internal static class SampleServiceFixture
{
    private static readonly string[] PlumbingFiles = ["packed-refs", "HEAD", "config"];

    /// <summary>
    /// The bare fixture repository, after checking its plumbing files still have LF endings.
    /// </summary>
    public static string RepoPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-service.git");

            foreach (var name in PlumbingFiles)
            {
                var file = Path.Combine(path, name);
                // CRLF turns every ref name git parses into 'name\r', which surfaces as "ref not found".
                if (File.Exists(file) && File.ReadAllBytes(file).Contains((byte)'\r'))
                {
                    throw new InvalidOperationException(
                        $"Fixture file '{file}' has CRLF line endings, so git cannot read its refs. " +
                        "Run 'git checkout --force -- test/**/Fixtures' and rebuild.");
                }
            }

            return path;
        }
    }
}
