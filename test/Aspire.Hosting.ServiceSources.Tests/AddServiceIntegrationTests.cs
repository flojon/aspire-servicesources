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

    private static int? PortOf(IDistributedApplicationBuilder builder, string serviceName)
    {
        var realResource = Assert.Single(builder.Resources, r => r.Name == serviceName);
        var endpointAnnotation = Assert.Single(
            ((IResource)realResource).Annotations.OfType<EndpointAnnotation>());
        return endpointAnnotation.Port;
    }

    [Fact]
    public async Task AddService_ManagedClone_ClonesRealRepoAndChecksOutFeatureRef()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), $$"""
            services:
              orders:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
                defaultRef: main
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            {
              "services": { "orders": { "source": "local", "ref": "feature/v2" } }
            }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        // Deferred resolution: nothing is cloned or registered until BeforeStartEvent fires.
        var clonedProjectPath = Path.Combine(appHostDir, ".servicesources", "checkouts", "orders", "SampleProj", "SampleProj.csproj");
        Assert.False(File.Exists(clonedProjectPath));
        Assert.DoesNotContain(builder.Resources, r => r.Name == "orders");

        await PublishBeforeStartEventAsync(builder);

        Assert.True(File.Exists(clonedProjectPath));

        var endpoint = service.GetEndpoint("http");
        Assert.Equal("http", endpoint.EndpointName);
        Assert.Equal(5002, PortOf(builder, "orders"));
    }

    [Fact]
    public async Task AddService_TwoServicesSameRepoDifferentRefs_BothResolveIndependently()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), $$"""
            services:
              orders-main:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
              orders-v2:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            {
              "services": {
                "orders-main": { "source": "local", "ref": "main" },
                "orders-v2": { "source": "local", "ref": "feature/v2" }
              }
            }
            """);

        var builder = CreateBuilder(appHostDir);

        builder.AddService("orders-main");
        builder.AddService("orders-v2");

        await PublishBeforeStartEventAsync(builder);

        Assert.Equal(5001, PortOf(builder, "orders-main"));
        Assert.Equal(5002, PortOf(builder, "orders-v2"));
    }

    [Fact]
    public async Task AddService_TwoServicesSameRepoSameRef_BothResolveIndependently()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), $$"""
            services:
              orders-a:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
              orders-b:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            {
              "services": {
                "orders-a": { "source": "local", "ref": "main" },
                "orders-b": { "source": "local", "ref": "main" }
              }
            }
            """);

        var builder = CreateBuilder(appHostDir);

        builder.AddService("orders-a");
        builder.AddService("orders-b");

        await PublishBeforeStartEventAsync(builder);

        Assert.Equal(5001, PortOf(builder, "orders-a"));
        Assert.Equal(5001, PortOf(builder, "orders-b"));

        Assert.True(File.Exists(Path.Combine(appHostDir, ".servicesources", "checkouts", "orders-a", "SampleProj", "SampleProj.csproj")));
        Assert.True(File.Exists(Path.Combine(appHostDir, ".servicesources", "checkouts", "orders-b", "SampleProj", "SampleProj.csproj")));
    }
}
