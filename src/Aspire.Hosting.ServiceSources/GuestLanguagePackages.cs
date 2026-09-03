namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The hosting packages the javascript and java kinds compile against. Core references both with
/// <c>PrivateAssets="all"</c>, so they are absent from a consumer's output unless that AppHost
/// referenced them itself — which is the point: an AppHost inherits neither package, nor the Aspire
/// floors they carry, for a language it does not use.
/// </summary>
/// <remarks>
/// The cost is that the failure arrives from the runtime rather than from NuGet, as a
/// <see cref="FileNotFoundException"/> naming an assembly and a strong name, the first time a
/// service of that kind resolves. This turns it into something actionable.
/// <para>
/// <c>build/KoalaSoft.Aspire.Hosting.ServiceSources.targets</c> catches the too-old case earlier,
/// at build time, but only for a project that consumes core as a NuGet package. A guest-language
/// AppHost gets core through the <c>ProjectReference</c> the Aspire CLI generates, and a project
/// reference imports no <c>build/</c> targets, so for those this is the only report there is.
/// </para>
/// </remarks>
internal static class GuestLanguagePackages
{
    /// <summary>
    /// Keyed by assembly simple name, which is what a load failure carries. The version is the
    /// floor the same package has in <c>build/KoalaSoft.Aspire.Hosting.ServiceSources.targets</c>;
    /// <c>GuestLanguagePackageFloorTests</c> fails if the two part company.
    /// </summary>
    private static readonly Dictionary<string, (string PackageId, string MinimumVersion)> ByAssemblyName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Aspire.Hosting.JavaScript"] = ("Aspire.Hosting.JavaScript", "13.5.2"),
            ["CommunityToolkit.Aspire.Hosting.Java"] = ("CommunityToolkit.Aspire.Hosting.Java", "13.3.0"),
        };

    /// <summary>
    /// The floors, for the invariant test that compares them with the MSBuild ones.
    /// </summary>
    public static IEnumerable<(string PackageId, string MinimumVersion)> Floors =>
        ByAssemblyName.Values;

    /// <summary>
    /// Returns a message naming the package to install, or <see langword="null"/> when
    /// <paramref name="exception"/> is not a failure to load one of these two assemblies — in which
    /// case the handler failed for its own reasons and the generic report is the honest one.
    /// </summary>
    public static string? DescribeMissingPackage(Exception exception, string serviceName, string kind)
    {
        if (exception is not FileNotFoundException { FileName: { } fileName })
        {
            return null;
        }

        // "Aspire.Hosting.JavaScript, Version=13.5.2.0, Culture=neutral, PublicKeyToken=..." - the
        // simple name is all that identifies the package.
        var simpleName = fileName.Split(',')[0].Trim();

        if (!ByAssemblyName.TryGetValue(simpleName, out var package))
        {
            return null;
        }

        return $"Service '{serviceName}' has kind '{kind}', which needs the {package.PackageId} "
            + $"package ({package.MinimumVersion} or newer) referenced by the AppHost. "
            + $"KoalaSoft.Aspire.Hosting.ServiceSources references it privately, so that a project "
            + $"declaring no '{kind}' service does not inherit it, which means it is not installed "
            + $"for you: run `dotnet add package {package.PackageId}`. A guest-language AppHost adds "
            + $"\"{package.PackageId}\": \"{package.MinimumVersion}\" to the \"packages\" section of "
            + "aspire.config.json instead.";
    }
}
