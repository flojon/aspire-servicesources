using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

internal static class TestHelpers
{
    /// <summary>
    /// The <c>repositories</c> argument for a <c>ServiceMetadata.ToDefinition</c> call in a test that
    /// never sets <c>RepositoryRef</c> — shared so those call sites don't each mint their own.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, RepositoryDefinition> EmptyRepositories =
        new Dictionary<string, RepositoryDefinition>();

    public static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [TestBuilderDefaults.DisableConfigReloadArg],
        });

    public static IDistributedApplicationBuilder CreateBuilder() => CreateBuilder(CreateTempDirectory());

    public static string CreateTempDirectory() => TempDirectories.CreateSubdirectory().FullName;

    /// <summary>
    /// The wrapper script names the Community Toolkit's Java integration execs for a Maven goal and
    /// for a Gradle task. Spelled out here rather than read from the production code, so a test
    /// asserting on a wrapper path stays an independent statement about which file has to be there.
    /// </summary>
    public static string MavenWrapperName => OperatingSystem.IsWindows() ? "mvnw.cmd" : "mvnw";

    /// <inheritdoc cref="MavenWrapperName"/>
    public static string GradleWrapperName => OperatingSystem.IsWindows() ? "gradlew.bat" : "gradlew";

    /// <summary>
    /// Creates an empty wrapper script under <paramref name="directory"/>. The checkouts these tests
    /// build are real directories, so a wrapper the resolver is meant to find has to be a real file.
    /// Never executed — nothing here starts the resource.
    /// </summary>
    public static string WriteWrapper(string directory, string wrapperName)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, wrapperName);
        File.WriteAllText(path, "");
        return path;
    }

    /// <summary>
    /// The shape <c>ServiceCatalogLoader</c> hands an <see cref="ILocalResourceKind"/>: the raw yaml
    /// mapping under the service's kind key, as YamlDotNet's untyped
    /// <c>Dictionary&lt;object, object&gt;</c>. Built here rather than by round-tripping real yaml so
    /// these tests exercise the same entry point core uses, without depending on core internals.
    /// </summary>
    public static Dictionary<object, object> Block(params (string Key, object Value)[] entries)
    {
        var block = new Dictionary<object, object>();
        foreach (var (key, value) in entries)
        {
            block[key] = value;
        }

        return block;
    }
}
