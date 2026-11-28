using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// A <c>"local"</c> .NET service whose endpoints come from <c>Kestrel:Endpoints</c> in
/// <c>appsettings.json</c> rather than a launch profile's <c>applicationUrl</c>. The deferred-path
/// warning wiring is covered in <see cref="DeferredCheckoutTests"/>.
/// </summary>
[Trait("IO", "true")]
[Trait("Concurrency", "true")]
public class KestrelEndpointTests
{
    private const string ServiceName = "orders";

    private const string KestrelOnlyAppSettings = """
        { "Kestrel": { "Endpoints": { "Web": { "Url": "http://localhost:5200" } } } }
        """;

    private static ServiceDefinition PathDefinition() =>
        new ServiceMetadata { Path = "services/orders", Project = "Orders.csproj", Kind = LocalKinds.Dotnet }
            .ToDefinition("servicesources.yaml", ServiceName, TestHelpers.EmptyRepositories);

    [Fact]
    public void EagerPath_KestrelConfigOnly_ResolvesEndpointFromAppSettings()
    {
        var appHostDir = TempDirectories.CreateSubdirectory().FullName;
        var serviceDir = Directory.CreateDirectory(Path.Combine(appHostDir, "services", "orders")).FullName;
        File.WriteAllText(Path.Combine(serviceDir, "Orders.csproj"), "<Project />");
        File.WriteAllText(Path.Combine(serviceDir, "appsettings.json"), KestrelOnlyAppSettings);
        var builder = TestHelpers.CreateBuilder(appHostDir);

        new PathSource(new GitCliClient()).Resolve(
            builder, ServiceName, PathDefinition(),
            new() { Source = "path", Repository = new(), Path = new() });

        var resource = Assert.IsAssignableFrom<ProjectResource>(Assert.Single(builder.Resources, r => r.Name == ServiceName));
        var endpoint = Assert.Single(resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(5200, endpoint.Port);
    }

    private static string WriteProject(
        string appSettings, string? environment = null, string? environmentAppSettings = null,
        string? directorySettings = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(TempDirectories.CreateSubdirectory().FullName, "Service")).FullName;
        var projectFile = Path.Combine(dir, "Service.csproj");
        File.WriteAllText(projectFile, "<Project />");
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), appSettings);

        if (environmentAppSettings is not null)
        {
            File.WriteAllText(Path.Combine(dir, $"appsettings.{environment}.json"), environmentAppSettings);
        }

        if (directorySettings is not null)
        {
            File.WriteAllText(Path.Combine(dir, "Service.settings.json"), directorySettings);
        }

        return projectFile;
    }

    private static string? Warn(
        string projectFile, string? environment = null, IReadOnlyList<string>? applicationUrls = null)
    {
        var profile = new LandedLaunchProfile(null, applicationUrls ?? [], new Dictionary<string, string>());

        return DeferredCheckout.LaunchProfileEndpointWarning(
            ServiceName, profile, new ProjectResource(ServiceName),
            LandedKestrelEndpoints.Read(projectFile, environment))?.ToString();
    }

    [Fact]
    public void Warning_KestrelConfigOnly_NamesTheUrlAndSource()
    {
        var warning = Warn(WriteProject(KestrelOnlyAppSettings));

        Assert.NotNull(warning);
        Assert.Contains("http://localhost:5200", warning);
        Assert.Contains("Kestrel:Endpoints", warning);
    }

    [Fact]
    public void Warning_EnvironmentAppSettingsOverrideTheBaseUrl()
    {
        var warning = Warn(
            WriteProject(
                KestrelOnlyAppSettings, "Development",
                """{ "Kestrel": { "Endpoints": { "Web": { "Url": "http://localhost:5300" } } } }"""),
            "Development");

        Assert.NotNull(warning);
        Assert.Contains("http://localhost:5300", warning);
        Assert.DoesNotContain("http://localhost:5200", warning);
    }

    [Fact]
    public void Warning_EnvironmentFileIsIgnoredWhenNoEnvironmentIsSet()
    {
        var warning = Warn(
            WriteProject(
                KestrelOnlyAppSettings, "Development",
                """{ "Kestrel": { "Endpoints": { "Web": { "Url": "http://localhost:5300" } } } }"""));

        Assert.NotNull(warning);
        Assert.Contains("http://localhost:5200", warning);
        Assert.DoesNotContain("http://localhost:5300", warning);
    }

    [Fact]
    public void Read_ProjectDirectorySettingsFile_IsLayeredAfterAppSettings()
    {
        var urls = LandedKestrelEndpoints.Read(
            WriteProject(
                KestrelOnlyAppSettings,
                directorySettings: """{ "Kestrel": { "Endpoints": { "Web": { "Url": "http://localhost:5400" } } } }"""),
            null);

        Assert.Equal(["http://localhost:5400"], urls);
    }

    [Fact]
    public void Read_KeysAreCaseInsensitive()
    {
        var urls = LandedKestrelEndpoints.Read(
            WriteProject("""{ "kestrel": { "endpoints": { "web": { "url": "http://localhost:5200" } } } }"""), null);

        Assert.Equal(["http://localhost:5200"], urls);
    }

    [Theory]
    [InlineData("../../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void Read_EnvironmentNameWithPathSeparators_IsNotUsedToBuildAFilePath(string environment)
    {
        var projectFile = WriteProject(KestrelOnlyAppSettings);
        var outside = Path.Combine(Path.GetDirectoryName(projectFile)!, "..", "evil.json");
        File.WriteAllText(outside, """{ "Kestrel": { "Endpoints": { "X": { "Url": "http://evil:1" } } } }""");

        Assert.Equal(["http://localhost:5200"], LandedKestrelEndpoints.Read(projectFile, environment));
    }

    [Fact]
    public void Warning_KestrelUrlsReplaceApplicationUrlsAndAreDeduplicated()
    {
        var warning = Warn(
            WriteProject(
                """{ "Kestrel": { "Endpoints": { "A": { "Url": "http://localhost:5200" }, "B": { "Url": "http://localhost:5200" } } } }"""),
            applicationUrls: ["https://localhost:7001"]);

        Assert.NotNull(warning);
        Assert.DoesNotContain("7001", warning);
        Assert.Equal(2, warning.Split("http://localhost:5200").Length);
    }

    [Theory]
    [InlineData("http://localhost:5200", ".WithHttpEndpoint()", ".WithHttpsEndpoint()")]
    [InlineData("https://localhost:7001", ".WithHttpsEndpoint()", ".WithHttpEndpoint()")]
    public void Warning_AdvisesTheEndpointCallMatchingTheUrlScheme(string url, string expected, string unexpected)
    {
        var warning = Warn(WriteProject(
            "{ \"Kestrel\": { \"Endpoints\": { \"Web\": { \"Url\": \"" + url + "\" } } } }"));

        Assert.NotNull(warning);
        Assert.Contains(expected, warning);
        Assert.DoesNotContain(unexpected, warning);
    }

    [Fact]
    public void Warning_MixedKestrelSchemes_AdvisesBothCalls()
    {
        var warning = Warn(WriteProject(
            """{ "Kestrel": { "Endpoints": { "Web": { "Url": "http://localhost:5200" }, "Tls": { "Url": "https://localhost:7001" } } } }"""));

        Assert.NotNull(warning);
        Assert.Contains(".WithHttpEndpoint().WithHttpsEndpoint()", warning);
    }

    [Fact]
    public void Warning_KestrelUrlWithSurroundingWhitespace_IsClassifiedByItsScheme()
    {
        var warning = Warn(WriteProject(
            """{ "Kestrel": { "Endpoints": { "Web": { "Url": " https://localhost:7001 " } } } }"""));

        Assert.NotNull(warning);
        Assert.Contains(".WithHttpsEndpoint()", warning);
        Assert.DoesNotContain(".WithHttpEndpoint()", warning);
    }

    [Theory]
    [InlineData("https://localhost:7001", ".WithHttpsEndpoint()", ".WithHttpEndpoint()")]
    [InlineData("http://localhost:5001", ".WithHttpEndpoint()", ".WithHttpsEndpoint()")]
    public void Warning_ApplicationUrl_AdvisesTheEndpointCallMatchingTheUrlScheme(
        string applicationUrl, string expected, string unexpected)
    {
        var warning = Warn(WriteProject("{ }"), applicationUrls: [applicationUrl]);

        Assert.NotNull(warning);
        Assert.Contains(expected, warning);
        Assert.DoesNotContain(unexpected, warning);
    }

    [Fact]
    public void Warning_MixedApplicationUrls_AdvisesBothCalls()
    {
        var warning = Warn(WriteProject("{ }"), applicationUrls: ["https://localhost:7001", "http://localhost:5001"]);

        Assert.NotNull(warning);
        Assert.Contains(".WithHttpEndpoint().WithHttpsEndpoint()", warning);
    }

    [Fact]
    public void Warning_NoKestrelConfig_SaysNothing() =>
        Assert.Null(Warn(WriteProject("{ }")));

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "Kestrel": { "Endpoints": { "A": { "Url": "\ud800" } } } }""")]
    public void Warning_UnreadableAppSettings_SaysNothing(string appSettings) =>
        Assert.Null(Warn(WriteProject(appSettings)));
}
