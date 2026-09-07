using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

internal static class TestHelpers
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    /// <summary>
    /// Disables the Generic Host's default <c>reloadOnChange</c> watch on <c>appsettings.json</c>.
    /// A test AppHost never outlives its own method, so nothing needs to see a live edit to that
    /// file — and without this, every builder leaves behind a <c>FileSystemWatcher</c> (one inotify
    /// instance on Linux) that isn't collectible until the builder itself becomes unreachable. See
    /// https://github.com/flojon/aspire-servicesources/issues/287.
    /// </summary>
    private const string DisableConfigReloadArg = "--hostBuilder:reloadConfigOnChange=false";

    public static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [DisableConfigReloadArg],
        });

    /// <summary>
    /// Produces the same untyped object the catalog loader hands a kind handler: whatever
    /// YamlDotNet's dynamic deserialization makes of the service's options block. Building it this
    /// way rather than hand-rolling a <c>Dictionary&lt;object, object&gt;</c> keeps these tests
    /// honest about what a real <c>servicesources.yaml</c> actually produces.
    /// </summary>
    public static object? ParseOptionsBlock(string yaml) => Deserializer.Deserialize<object>(yaml);

    /// <summary>
    /// Creates a directory that stands in for a checked-out repository, with a JavaScript app in
    /// it. Returns the repository root. Pass <paramref name="withPackageJson"/> as <c>false</c> for
    /// the checkout of a plain Node app that has nothing but its entry-point file.
    /// </summary>
    public static string CreateRepo(string? appSubdirectory = null, bool withPackageJson = true)
    {
        var repoRoot = TempDirectories.CreateSubdirectory("servicesources-js-").FullName;
        var appDirectory = appSubdirectory is null ? repoRoot : Path.Combine(repoRoot, appSubdirectory);
        Directory.CreateDirectory(appDirectory);

        if (withPackageJson)
        {
            File.WriteAllText(
                Path.Combine(appDirectory, "package.json"),
                """{ "name": "frontend", "scripts": { "dev": "node server.js", "start": "node server.js" } }""");
        }

        File.WriteAllText(Path.Combine(appDirectory, "server.js"), "");

        return repoRoot;
    }

    public static EndpointAnnotation SingleEndpoint(IResource resource) =>
        Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());

    /// <summary>
    /// Reads back the environment variable name <c>WithHttpEndpoint(env:)</c> stored on the
    /// endpoint. The property is not part of Aspire's public surface, so it is read reflectively —
    /// the lookup is asserted rather than tolerated, so this fails loudly if Aspire renames it
    /// instead of silently reporting "no variable configured".
    /// </summary>
    public static string? TargetPortEnvironmentVariable(EndpointAnnotation endpoint)
    {
        var property = typeof(EndpointAnnotation).GetProperty(
            "TargetPortEnvironmentVariable",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        Assert.NotNull(property);

        return (string?)property.GetValue(endpoint);
    }
}
