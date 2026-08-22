using Aspire.Hosting.ApplicationModel;
using static Aspire.Hosting.ServiceSources.Java.Tests.TestHelpers;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

/// <summary>
/// Covers <c>UseJava()</c> end to end through core's own <c>AddService()</c> — a real
/// <c>servicesources.yaml</c>, a real <c>servicesources.local.json</c>, and a real
/// <c>BeforeStartEvent</c> — so the <c>kind: java</c> wiring is exercised the way an AppHost
/// actually reaches it, not just through the handler in isolation.
/// </summary>
public class UseJavaTests
{
    /// <summary>
    /// Writes an AppHost directory whose single <c>java-api</c> service is resolved from a checkout
    /// already on disk (the <c>path</c> override), so nothing here needs to clone a repository.
    /// </summary>
    private static (string AppHostDirectory, string Checkout) CreateAppHost(
        string javaBlock, params string[] checkoutSubdirectories)
    {
        var appHostDirectory = CreateTempDirectory();
        var checkout = CreateTempDirectory();
        foreach (var subdirectory in checkoutSubdirectories)
        {
            Directory.CreateDirectory(Path.Combine(checkout, subdirectory));
        }

        File.WriteAllText(Path.Combine(appHostDirectory, "servicesources.yaml"), $"""
            services:
              java-api:
                repository: https://github.com/example/java-api
                kind: java
                java:
            {javaBlock}
            """);
        File.WriteAllText(Path.Combine(appHostDirectory, "servicesources.local.json"), $$"""
            {
              "services": {
                "java-api": { "source": "local", "path": {{System.Text.Json.JsonSerializer.Serialize(checkout)}} }
              }
            }
            """);

        return (appHostDirectory, checkout);
    }

    [Fact]
    public async Task UseJava_ThenAddService_AddsAJavaAppForTheCheckout()
    {
        var (appHostDirectory, checkout) = CreateAppHost("""
                  mavenGoal: spring-boot:run
                  port: 8080
            """);
        var builder = CreateBuilder(appHostDirectory);
        builder.UseJava();
        builder.AddService("java-api");

        await PublishBeforeStartEventAsync(builder);

        var resource = Assert.IsType<JavaAppExecutableResource>(
            Assert.Single(builder.Resources, r => r.Name == "java-api"));
        Assert.Equal(Path.GetFullPath(checkout), Path.GetFullPath(resource.WorkingDirectory));
    }

    [Fact]
    public async Task UseJava_ThenAddService_GivesTheReturnedFacadeTheServicesEndpoint()
    {
        var (appHostDirectory, _) = CreateAppHost("""
                  mavenGoal: spring-boot:run
                  port: 8080
            """);
        var builder = CreateBuilder(appHostDirectory);
        builder.UseJava();
        var javaApi = builder.AddService("java-api");

        await PublishBeforeStartEventAsync(builder);

        // The facade is what a consumer passes to WithReference(...), so the endpoint has to make it
        // across from the real resource core created via the handler.
        var endpoint = Assert.Single(javaApi.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(8080, endpoint.TargetPort);
    }

    [Fact]
    public async Task UseJava_WorkingDirectoryBlock_RootsTheJavaAppUnderTheCheckout()
    {
        var (appHostDirectory, checkout) = CreateAppHost("""
                  workingDirectory: services/api
                  gradleTask: bootRun
                  port: 9000
            """, Path.Combine("services", "api"));
        var builder = CreateBuilder(appHostDirectory);
        builder.UseJava();
        builder.AddService("java-api");

        await PublishBeforeStartEventAsync(builder);

        var resource = Assert.IsType<JavaAppExecutableResource>(
            Assert.Single(builder.Resources, r => r.Name == "java-api"));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(checkout, "services", "api")),
            Path.GetFullPath(resource.WorkingDirectory));
    }

    [Fact]
    public async Task AddService_KindJavaWithoutUseJava_ThrowsNamingTheKind()
    {
        var (appHostDirectory, _) = CreateAppHost("""
                  mavenGoal: spring-boot:run
                  port: 8080
            """);
        var builder = CreateBuilder(appHostDirectory);
        builder.AddService("java-api");

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("java", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "java-api");
    }

    [Fact]
    public async Task AddService_MalformedJavaBlock_ThrowsWithoutAddingAnyResource()
    {
        var (appHostDirectory, _) = CreateAppHost("""
                  mavenGoal: spring-boot:run
            """);
        var builder = CreateBuilder(appHostDirectory);
        builder.UseJava();
        builder.AddService("java-api");

        // Reported from Validate, which core runs before any service touches the app model.
        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("port", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "java-api");
    }

    [Fact]
    public void UseJava_ReturnsTheSameBuilderForChaining()
    {
        var builder = CreateBuilder();

        Assert.Same(builder, builder.UseJava());
    }

    [Fact]
    public void UseJava_CalledTwice_ThrowsRatherThanSilentlyReplacingTheHandler()
    {
        var builder = CreateBuilder();
        builder.UseJava();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.UseJava());

        Assert.Contains("java", ex.Message);
    }

    [Fact]
    public void UseJava_OnTwoBuilders_RegistersIndependentlyOnEach()
    {
        var builderA = CreateBuilder();
        var builderB = CreateBuilder();

        builderA.UseJava();

        // Registration is per-builder, so B must still be free to register the kind itself.
        builderB.UseJava();
    }

    [Fact]
    public void UseJava_RegistersUnderTheJavaKindName()
    {
        var builder = CreateBuilder();
        builder.UseJava();

        // AddLocalKind rejects an already-registered kind, so this failing for "java" — and only for
        // "java" — pins down which kind name UseJava claimed.
        Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddLocalKind("java", new JavaLocalResourceKind()));
        builder.AddLocalKind("java-other", new JavaLocalResourceKind());
    }
}
