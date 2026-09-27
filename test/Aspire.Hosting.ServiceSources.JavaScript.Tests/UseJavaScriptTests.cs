using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript;
using Aspire.Hosting.ServiceSources;
using Xunit;

namespace Aspire.Hosting.ServiceSources.JavaScript.Tests;

/// <summary>
/// End-to-end cover for the package's single entry point: a <c>kind: javascript</c> service in a
/// real <c>servicesources.yaml</c>, resolved through <c>AddService</c>. The developer config points
/// <c>path</c> at an existing directory, so these exercise the whole path without needing git.
/// </summary>
[Trait("IO", "true")]
public class UseJavaScriptTests
{
    private static string CreateAppHost(string repoRoot, string catalogOptions = "")
    {
        var appHostDir = TempDirectories.CreateSubdirectory("servicesources-js-apphost-").FullName;

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), $"""
            services:
              frontend:
                repository: https://example.com/frontend
                kind: javascript
            {catalogOptions}
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), $$"""
            {
              "services": { "frontend": { "source": "repository", "local": { "path": {{System.Text.Json.JsonSerializer.Serialize(repoRoot)}} } } }
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

        // service.Resource is the ServiceResource facade; the real, registered JavaScriptAppResource
        // carries the same name and is what DCP actually runs.
        var resource = Assert.IsType<JavaScriptAppResource>(
            Assert.Single(builder.Resources, r => r.Name == "frontend"));
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

    /// <summary>
    /// <c>javascript</c> is a built-in kind — <c>AddService</c> resolves it via
    /// <c>LocalKindRegistry</c>'s own fallback whether or not <c>UseJavaScript()</c> was ever called,
    /// the same way <c>dotnet</c> always has been.
    /// </summary>
    [Fact]
    public void WithoutUseJavaScriptTheKindStillResolves()
    {
        var repoRoot = TestHelpers.CreateRepo();
        var builder = TestHelpers.CreateBuilder(CreateAppHost(repoRoot));

        var service = builder.AddService("frontend");

        var resource = Assert.IsType<JavaScriptAppResource>(
            Assert.Single(builder.Resources, r => r.Name == "frontend"));
        Assert.Equal(repoRoot, resource.WorkingDirectory);
        Assert.Equal("http", service.GetEndpoint("http").EndpointName);
    }

    [Fact]
    public void RegisteringTwiceOnTheSameBuilderIsRejected()
    {
        var builder = TestHelpers.CreateBuilder(
            TempDirectories.CreateSubdirectory("servicesources-js-apphost-").FullName);

        builder.UseJavaScript();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.UseJavaScript());
        Assert.Contains("already registered", ex.Message);
    }

    /// <summary>
    /// The "path" source reuses the non-dotnet kind dispatch completely unchanged (design finding 1
    /// — <c>ILocalResourceKind</c>/<c>LocalKindRegistry</c> never learn the directory wasn't cloned):
    /// a <c>javascript</c>-kind service resolves through <c>"path"</c> exactly as it does through
    /// <c>"repository"</c>, with no clone and no <c>local.path</c> override involved.
    /// </summary>
    [Fact]
    public void AddService_PathSourceJavaScriptKind_ResolvesTheRealRegisteredResource()
    {
        var appHostDir = TempDirectories.CreateSubdirectory("servicesources-js-path-").FullName;
        var appDir = Path.Combine(appHostDir, "services", "frontend");
        Directory.CreateDirectory(appDir);
        File.WriteAllText(
            Path.Combine(appDir, "package.json"),
            """{ "name": "frontend", "scripts": { "dev": "node server.js", "start": "node server.js" } }""");
        File.WriteAllText(Path.Combine(appDir, "server.js"), "");

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              frontend:
                path: services/frontend
                kind: javascript
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "frontend": { "source": "path" } } }
            """);

        var builder = TestHelpers.CreateBuilder(appHostDir);
        builder.UseJavaScript();

        var service = builder.AddService("frontend");

        var resource = Assert.IsType<JavaScriptAppResource>(
            Assert.Single(builder.Resources, r => r.Name == "frontend"));
        Assert.Equal(appDir, resource.WorkingDirectory);
        Assert.Equal("http", service.GetEndpoint("http").EndpointName);
    }
}
