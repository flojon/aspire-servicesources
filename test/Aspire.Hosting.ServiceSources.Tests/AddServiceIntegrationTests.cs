using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.ServiceSources.Tests;

public class AddServiceIntegrationTests
{
    private static string FixtureRepoPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-service.git");

    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        TestHelpers.CreateBuilder(appHostDirectory);

    private static Task PublishBeforeStartEventAsync(IDistributedApplicationBuilder builder) =>
        TestHelpers.PublishBeforeStartEventAsync(builder);

    [Fact]
    public async Task AddService_ManagedClone_ClonesRealRepoAndChecksOutFeatureRef()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var appHostDir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), $$"""
            services:
              orders:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
                defaultRef: main
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), $$"""
            {
              "cacheDirectory": "{{cacheDirectory.Replace("\\", "\\\\")}}",
              "services": { "orders": { "source": "local", "ref": "feature/v2" } }
            }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        // Deferred resolution: nothing is cloned or registered until BeforeStartEvent fires.
        var clonedProjectPath = Path.Combine(cacheDirectory, "sample-service", "SampleProj", "SampleProj.csproj");
        Assert.False(File.Exists(clonedProjectPath));
        Assert.DoesNotContain(builder.Resources, r => r.Name == "orders");

        await PublishBeforeStartEventAsync(builder);

        Assert.True(File.Exists(clonedProjectPath));

        var endpoint = service.GetEndpoint("http");
        Assert.Equal("http", endpoint.EndpointName);

        var realResource = Assert.Single(builder.Resources, r => r.Name == "orders");
        var endpointAnnotation = Assert.Single(
            ((IResource)realResource).Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(5002, endpointAnnotation.Port);
    }
}
