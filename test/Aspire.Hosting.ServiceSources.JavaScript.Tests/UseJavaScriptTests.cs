using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

/// <summary>
/// End-to-end cover for the package's single entry point: a <c>kind: javascript</c> service in a
/// real <c>servicesources.yaml</c>, resolved through <c>AddService</c>. The developer config points
/// <c>path</c> at an existing directory, so these exercise the whole path without needing git.
/// </summary>
public class UseJavaScriptTests
{
    private static string CreateAppHost(string repoRoot, string catalogOptions = "")
    {
        var appHostDir = Directory.CreateTempSubdirectory("servicesources-js-apphost-").FullName;

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), $"""
            services:
              frontend:
                repository: https://example.com/frontend
                kind: javascript
            {catalogOptions}
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), $$"""
            {
              "services": { "frontend": { "source": "local", "local": { "path": {{System.Text.Json.JsonSerializer.Serialize(repoRoot)}} } } }
            }
            """);

        return appHostDir;
    }

    [Fact]
    public void ResolvesAJavaScriptServiceToTheRealRegisteredResource()
    {
        var repoRoot = TestHelpers.CreateRepo();
        var builder = TestHelpers.CreateBuilder(CreateAppHost(repoRoot));

        builder.UseJavaScript();
        var service = builder.AddService("frontend");

        // AddService hands back the resource Aspire actually runs, already in the app model — the
        // handler's own return value, carried straight through.
        var resource = Assert.IsType<JavaScriptAppResource>(service.Resource);
        Assert.Contains(builder.Resources, r => ReferenceEquals(r, service.Resource));
        Assert.Equal("frontend", resource.Name);
        Assert.Equal(repoRoot, resource.WorkingDirectory);

        Assert.Equal("http", service.GetEndpoint("http").EndpointName);
        Assert.Equal("http", TestHelpers.SingleEndpoint(service.Resource).Name);
    }

    [Fact]
    public void ReadsTheServicesOwnOptionsBlock()
    {
        var repoRoot = TestHelpers.CreateRepo("web");
        var appHostDir = CreateAppHost(repoRoot, """
                javascript:
                  appType: vite
                  appDirectory: web
                  packageManager: pnpm
                  port: 4321
            """);
        var builder = TestHelpers.CreateBuilder(appHostDir);

        builder.UseJavaScript();
        builder.AddService("frontend");

        var resource = Assert.Single(builder.Resources.OfType<ViteAppResource>());
        Assert.Equal(Path.Combine(repoRoot, "web"), resource.WorkingDirectory);
        Assert.Equal("pnpm", resource.Annotations.OfType<JavaScriptPackageManagerAnnotation>().Last().ExecutableName);
        Assert.Equal(4321, TestHelpers.SingleEndpoint(resource).Port);
    }

    [Fact]
    public void ABadOptionsBlockFailsBeforeTheResourceIsCreated()
    {
        var repoRoot = TestHelpers.CreateRepo();
        var appHostDir = CreateAppHost(repoRoot, """
                javascript:
                  runScrip: dev
            """);
        var builder = TestHelpers.CreateBuilder(appHostDir);

        builder.UseJavaScript();

        // Reported from the handler's Validate, which core calls before Resolve — so the service
        // fails without leaving a half-created resource behind.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("frontend"));

        Assert.Contains("runScrip", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r is JavaScriptAppResource);
    }

    [Fact]
    public void WithoutUseJavaScriptTheKindIsUnregistered()
    {
        var repoRoot = TestHelpers.CreateRepo();
        var builder = TestHelpers.CreateBuilder(CreateAppHost(repoRoot));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("frontend"));

        Assert.Contains("javascript", ex.Message);
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void RegisteringTwiceOnTheSameBuilderIsRejected()
    {
        var builder = TestHelpers.CreateBuilder(
            Directory.CreateTempSubdirectory("servicesources-js-apphost-").FullName);

        builder.UseJavaScript();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.UseJavaScript());
        Assert.Contains("already registered", ex.Message);
    }
}
