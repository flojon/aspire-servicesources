using System.Reflection;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The hosting packages the javascript and java kinds compile against. Core references both with
/// <c>PrivateAssets="all"</c>, so they are absent from a consumer's output unless that AppHost
/// referenced them itself — which is the point: an AppHost inherits neither package, nor the Aspire
/// floors they carry, for a language it does not use.
/// </summary>
/// <remarks>
/// The cost is that a version problem arrives from the runtime rather than from NuGet, the first
/// time a service of that kind resolves, in one of two shapes. Absent entirely, it is a
/// <see cref="FileNotFoundException"/> naming an assembly and a strong name. Present but too old, it
/// may not fail to load at all: a prerelease cut before a release carries that release's assembly
/// version, so it binds and then fails on the member that is not there yet. This turns both into
/// something actionable.
/// <para>
/// <c>buildTransitive/KoalaSoft.Aspire.Hosting.ServiceSources.targets</c> catches the too-old case earlier, at
/// build time, but only for a project that consumes core as a NuGet package. A guest-language
/// AppHost gets core through the <c>ProjectReference</c> the Aspire CLI generates, and a project
/// reference imports no <c>build/</c> targets, so for those this is the only report there is.
/// </para>
/// </remarks>
internal static class GuestLanguagePackages
{
    /// <summary>
    /// Keyed by assembly simple name, which is what a load failure carries. The version is the floor
    /// the same package has in <c>buildTransitive/KoalaSoft.Aspire.Hosting.ServiceSources.targets</c>;
    /// <c>GuestLanguagePackageFloorTests</c> fails if those, these, and the versions the repository
    /// restores against ever part company.
    /// </summary>
    private static readonly Dictionary<string, (string PackageId, string MinimumVersion)> ByAssemblyName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Aspire.Hosting.JavaScript"] = ("Aspire.Hosting.JavaScript", "13.5.2"),
            ["CommunityToolkit.Aspire.Hosting.Java"] = ("CommunityToolkit.Aspire.Hosting.Java", "13.3.0"),
        };

    /// <summary>
    /// Which assembly a kind reaches, keyed by the <c>kind</c> a service's yaml declares. Ordinal,
    /// because kind names are matched case-sensitively wherever else they are read.
    /// </summary>
    /// <remarks>
    /// Needed because a load failure that binds and then fails on a missing member names no
    /// assembly, so the only thing that says which package could possibly be at fault is the kind
    /// of the service that failed. Keyed off the kinds' own constants rather than repeating the
    /// strings, so a renamed kind cannot leave this pointing at a name nothing declares.
    /// </remarks>
    private static readonly Dictionary<string, string> AssemblyByKind = new(StringComparer.Ordinal)
    {
        [JavaScriptLocalKind.KindName] = "Aspire.Hosting.JavaScript",
        [Java.JavaLocalResourceKind.KindName] = "CommunityToolkit.Aspire.Hosting.Java",
    };

    /// <summary>
    /// The floors, for the invariant tests that compare them with the MSBuild ones and with the
    /// versions the repository restores against.
    /// </summary>
    public static IEnumerable<(string PackageId, string MinimumVersion)> Floors =>
        ByAssemblyName.Values;

    /// <summary>
    /// The kind-to-assembly mapping, for the invariant test that checks a kind added later is wired
    /// into both tables rather than only one — half-wired, the too-old report for that kind silently
    /// degrades to the generic message.
    /// </summary>
    public static IEnumerable<(string Kind, string AssemblyName)> KindAssemblies =>
        AssemblyByKind.Select(entry => (entry.Key, entry.Value));

    /// <inheritdoc cref="DescribeMissingPackage(Exception, string, string, Func{string, Version?})"/>
    public static string? DescribeMissingPackage(Exception exception, string serviceName, string kind) =>
        DescribeMissingPackage(exception, serviceName, kind, InstalledVersion);

    /// <summary>
    /// Returns a message naming the package to install or raise, or <see langword="null"/> when
    /// <paramref name="exception"/> is not a version problem with one of these two packages — in
    /// which case the handler failed for its own reasons and the generic report is the honest one.
    /// </summary>
    /// <param name="installedVersion">
    /// What version of a given assembly this process can actually load, or <see langword="null"/> for
    /// none. Injected so the too-old branch is testable from a project where these assemblies are
    /// deliberately absent.
    /// </param>
    internal static string? DescribeMissingPackage(
        Exception exception,
        string serviceName,
        string kind,
        Func<string, Version?> installedVersion)
    {
        if (exception is FileNotFoundException { FileName: { } fileName })
        {
            // "Aspire.Hosting.JavaScript, Version=13.5.2.0, Culture=neutral, PublicKeyToken=..." -
            // the simple name is all that identifies the package.
            var simpleName = fileName.Split(',')[0].Trim();

            return ByAssemblyName.TryGetValue(simpleName, out var missing)
                ? NotInstalledMessage(serviceName, kind, missing)
                : null;
        }

        // A binding that succeeded and then found the wrong shape behind it. Nothing in these names
        // an assembly, so the question has to be turned around: not "what failed to load" but "is
        // anything we need older than we need it". Restricted to the exception types a version
        // mismatch actually produces, so an ordinary bug in a handler is never reported as a
        // packaging problem just because some package happens to be old.
        if (exception is not (TypeLoadException or MissingMemberException or BadImageFormatException))
        {
            return null;
        }

        // Scoped to the failing service's own kind, unlike the branch above: there the exception
        // names the assembly and is the better authority, whereas here the kind is the only thing
        // that says which package could possibly be at fault. A javascript service cannot be failing
        // because the java package is old - it never touches it - and naming one while quoting the
        // other is worse than the generic message, since it sends the reader to change something
        // irrelevant. A kind registered by someone else reaches packages whose floors are not ours
        // to know, so it falls through.
        if (!AssemblyByKind.TryGetValue(kind, out var assemblyName)
            || !ByAssemblyName.TryGetValue(assemblyName, out var needed))
        {
            return null;
        }

        var installed = installedVersion(assemblyName);

        if (installed is null)
        {
            return null;
        }

        // Assembly versions carry a revision component that package versions do not, so compare only
        // the three that both have.
        var comparable = new Version(installed.Major, installed.Minor, Math.Max(installed.Build, 0));

        if (comparable >= Version.Parse(needed.MinimumVersion))
        {
            return null;
        }

        return $"Service '{serviceName}' has kind '{kind}', which needs {needed.PackageId} "
            + $"{needed.MinimumVersion} or newer, but this AppHost has {comparable}. That version "
            + "loaded and then failed on a member it does not carry, which is what a release older "
            + "than the minimum — or a prerelease of it — does rather than failing to load. "
            + $"Raise {needed.PackageId} to {needed.MinimumVersion} or newer.";
    }

    private static string NotInstalledMessage(
        string serviceName, string kind, (string PackageId, string MinimumVersion) package) =>
        $"Service '{serviceName}' has kind '{kind}', which needs the {package.PackageId} "
        + $"package ({package.MinimumVersion} or newer) referenced by the AppHost. "
        + $"KoalaSoft.Aspire.Hosting.ServiceSources references it privately, so that a project "
        + $"declaring no '{kind}' service does not inherit it, which means it is not installed "
        + $"for you: run `dotnet add package {package.PackageId}`. A guest-language AppHost adds "
        + $"\"{package.PackageId}\": \"{package.MinimumVersion}\" to the \"packages\" section of "
        + "aspire.config.json instead.";

    /// <summary>
    /// The version of <paramref name="assemblyName"/> this process can load, or
    /// <see langword="null"/> if it cannot load it at all. Asked only after a failure has already
    /// happened, so the cost of a load attempt does not matter.
    /// </summary>
    private static Version? InstalledVersion(string assemblyName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(assemblyName)).GetName().Version;
        }
        catch (Exception)
        {
            // Absent, unloadable, or blocked - all of which mean there is no installed version to
            // report, and none of which this diagnostic path should turn into a second failure.
            return null;
        }
    }
}
