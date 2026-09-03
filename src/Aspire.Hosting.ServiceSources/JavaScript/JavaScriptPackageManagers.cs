namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The values <see cref="JavaScriptKindOptions.PackageManager"/> accepts, one per
/// <c>Aspire.Hosting.JavaScript</c> package-manager modifier.
/// </summary>
internal static class JavaScriptPackageManagers
{
    public const string Npm = "npm";

    public const string Yarn = "yarn";

    public const string Pnpm = "pnpm";

    public const string Bun = "bun";

    public static readonly string[] All = [Npm, Yarn, Pnpm, Bun];
}
