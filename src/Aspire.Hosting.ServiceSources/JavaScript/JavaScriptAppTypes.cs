namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The values <see cref="JavaScriptKindOptions.AppType"/> accepts, one per
/// <c>Aspire.Hosting.JavaScript</c> entry point.
/// </summary>
internal static class JavaScriptAppTypes
{
    /// <summary>Runs a <c>package.json</c> script via <c>AddJavaScriptApp</c>. The default.</summary>
    public const string JavaScript = "javascript";

    /// <summary>Runs a Vite dev server via <c>AddViteApp</c>.</summary>
    public const string Vite = "vite";

    /// <summary>Runs a Next.js dev server via <c>AddNextJsApp</c>.</summary>
    public const string NextJs = "nextjs";

    /// <summary>Runs a script file directly with <c>node</c> via <c>AddNodeApp</c>.</summary>
    public const string Node = "node";

    /// <summary>Runs a script file directly with <c>bun</c> via <c>AddBunApp</c>.</summary>
    public const string Bun = "bun";

    public static readonly string[] All = [JavaScript, Vite, NextJs, Node, Bun];

    /// <summary>
    /// The app types whose integration takes a file to execute (<see cref="JavaScriptKindOptions.ScriptPath"/>)
    /// rather than the name of a <c>package.json</c> script.
    /// </summary>
    public static bool RunsAScriptFile(string appType) => appType is Node or Bun;

    /// <summary>
    /// The app types whose integration adds and binds its own HTTP endpoint, leaving this package
    /// nothing to inject a port environment variable for.
    /// </summary>
    public static bool BindsItsOwnPort(string appType) => appType is Vite or NextJs;
}
