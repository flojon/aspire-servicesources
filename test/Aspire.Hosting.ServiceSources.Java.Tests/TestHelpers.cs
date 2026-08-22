namespace Aspire.Hosting.ServiceSources.Java.Tests;

internal static class TestHelpers
{
    public static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [],
        });

    public static IDistributedApplicationBuilder CreateBuilder() => CreateBuilder(CreateTempDirectory());

    public static string CreateTempDirectory() => Directory.CreateTempSubdirectory().FullName;

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
