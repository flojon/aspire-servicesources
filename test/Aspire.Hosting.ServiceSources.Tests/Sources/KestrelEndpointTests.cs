using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

/// <summary>
/// A <c>"local"</c> .NET service whose endpoints come from <c>Kestrel:Endpoints</c> in
/// <c>appsettings.json</c> rather than a launch profile's <c>applicationUrl</c> (#398).
/// </summary>
[Trait("IO", "true")]
[Trait("Concurrency", "true")]
public class KestrelEndpointTests
{
    private const string ServiceName = "orders";

    private const string KestrelOnlyAppSettings = """
        { "Kestrel": { "Endpoints": { "Web": { "Url": "http://localhost:5200" } } } }
        """;

    private sealed class LandingGitClient(string appSettings, ManualResetEventSlim gate) : IGitClient
    {
        public void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null, string? separateGitDir = null)
        {
            gate.Wait(TimeSpan.FromSeconds(30));
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "Service.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(destinationPath, "appsettings.json"), appSettings);
        }

        public void Checkout(string repositoryPath, string reference)
        {
        }

        public void Fetch(string repositoryPath)
        {
        }

        public bool HasUncommittedChanges(string repositoryPath) => false;

        public bool IsRefCheckedOut(string repositoryPath, string reference) => true;

        public string? GetOriginUrl(string repositoryPath) => null;
    }

    private static ServiceDefinition PathDefinition() =>
        new ServiceMetadata { Path = "services/orders", Project = "Orders.csproj", Kind = LocalKinds.Dotnet }
            .ToDefinition("servicesources.yaml", ServiceName, TestHelpers.EmptyRepositories);

    private static ServiceDefinition RepositoryDefinition() =>
        new ServiceMetadata { Repository = $"https://example.com/{ServiceName}.git", Project = "Service.csproj" }
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

    [Fact]
    public void DeferredPath_KestrelConfigOnly_DeclaresNoEndpointWhileComposing()
    {
        var appHostDir = TempDirectories.CreateSubdirectory().FullName;
        var builder = TestHelpers.CreateBuilder(appHostDir);
        builder.SetCheckoutTiming(CheckoutTiming.Deferred);

        using var gate = new ManualResetEventSlim(false);
        var service = new LocalProjectSource(new LandingGitClient(KestrelOnlyAppSettings, gate)).Resolve(
            builder, ServiceName, RepositoryDefinition(),
            new() { Source = "repository", Repository = new() });

        // The checkout is still cloning, so appsettings.json is unreadable while Aspire composes the
        // project and nothing re-reads it afterwards: the Kestrel endpoint the eager path gets is lost.
        Assert.Empty(service.Resource.Annotations.OfType<EndpointAnnotation>());

        gate.Set();
    }

    private static string WriteProject(string appSettings, string? environmentAppSettings = null, string? environment = null)
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var projectFile = Path.Combine(dir, "Service.csproj");
        File.WriteAllText(projectFile, "<Project />");
        File.WriteAllText(Path.Combine(dir, "appsettings.json"), appSettings);

        if (environmentAppSettings is not null)
        {
            File.WriteAllText(Path.Combine(dir, $"appsettings.{environment}.json"), environmentAppSettings);
            LaunchSettingsFixture.Write(
                dir,
                "{ \"profiles\": { \"Service\": { \"commandName\": \"Project\", "
                + $"\"environmentVariables\": {{ \"DOTNET_ENVIRONMENT\": \"{environment}\" }} }} }} }}");
        }

        return projectFile;
    }

    private static string? Warn(string projectFile)
    {
        var resource = new ProjectResource(ServiceName);
        var profile = LandedLaunchProfile.Read(projectFile, resource);

        return DeferredCheckout.LaunchProfileEndpointWarning(
            ServiceName, profile, resource, LandedKestrelEndpoints.Read(projectFile, profile))?.ToString();
    }

    [Fact]
    public void LaunchProfileEndpointWarning_KestrelConfigOnly_NamesTheUrl()
    {
        var warning = Warn(WriteProject(KestrelOnlyAppSettings));

        Assert.NotNull(warning);
        Assert.Contains("http://localhost:5200", warning);
        Assert.Contains("WithHttpEndpoint", warning);
    }

    [Fact]
    public void LaunchProfileEndpointWarning_EnvironmentAppSettingsOverrideTheBaseUrl()
    {
        var warning = Warn(WriteProject(
            KestrelOnlyAppSettings,
            """{ "Kestrel": { "Endpoints": { "Web": { "Url": "http://localhost:5300" } } } }""",
            "Development"));

        Assert.NotNull(warning);
        Assert.Contains("http://localhost:5300", warning);
        Assert.DoesNotContain("http://localhost:5200", warning);
    }

    [Fact]
    public void LaunchProfileEndpointWarning_NoKestrelConfig_SaysNothing() =>
        Assert.Null(Warn(WriteProject("{ }")));

    [Fact]
    public void LaunchProfileEndpointWarning_UnreadableAppSettings_SaysNothing() =>
        Assert.Null(Warn(WriteProject("{ not json")));
}
