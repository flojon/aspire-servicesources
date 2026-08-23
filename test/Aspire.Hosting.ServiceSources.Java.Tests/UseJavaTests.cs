using Aspire.Hosting.ApplicationModel;
using static Aspire.Hosting.ServiceSources.Java.Tests.TestHelpers;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

/// <summary>
/// Covers <c>UseJava()</c> end to end through core's own <c>AddService()</c> — a real
/// <c>servicesources.yaml</c> and a real <c>servicesources.local.json</c> — so the
/// <c>kind: java</c> wiring is exercised the way an AppHost actually reaches it, not just through
/// the handler in isolation. <c>AddService()</c> resolves eagerly and returns the real resource, so
/// there is no event to publish: the assertions run straight after the call.
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
    public void UseJava_ThenAddService_AddsAJavaAppForTheCheckout()
    {
        var (appHostDirectory, checkout) = CreateAppHost("""
                  mavenGoal: spring-boot:run
                  port: 8080
            """);
        WriteWrapper(checkout, MavenWrapperName);
        var builder = CreateBuilder(appHostDirectory);
        builder.UseJava();
        builder.AddService("java-api");

        var resource = Assert.IsType<JavaAppExecutableResource>(
            Assert.Single(builder.Resources, r => r.Name == "java-api"));
        Assert.Equal(Path.GetFullPath(checkout), Path.GetFullPath(resource.WorkingDirectory));
    }

    [Fact]
    public void UseJava_ThenAddService_ReturnsTheJavaResourceCarryingItsEndpoint()
    {
        var (appHostDirectory, checkout) = CreateAppHost("""
                  mavenGoal: spring-boot:run
                  port: 8080
            """);
        WriteWrapper(checkout, MavenWrapperName);
        var builder = CreateBuilder(appHostDirectory);
        builder.UseJava();
        var javaApi = builder.AddService("java-api");

        // AddService hands back the very resource the handler created, so the endpoint a consumer
        // resolves through WithReference(...) is the one declared from the java block's `port`.
        Assert.IsType<JavaAppExecutableResource>(javaApi.Resource);
        var endpoint = Assert.Single(javaApi.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(8080, endpoint.TargetPort);
    }

    [Fact]
    public void UseJava_WorkingDirectoryBlock_RootsTheJavaAppUnderTheCheckout()
    {
        var (appHostDirectory, checkout) = CreateAppHost("""
                  workingDirectory: services/api
                  gradleTask: bootRun
                  port: 9000
            """, Path.Combine("services", "api"));
        WriteWrapper(Path.Combine(checkout, "services", "api"), GradleWrapperName);
        var builder = CreateBuilder(appHostDirectory);
        builder.UseJava();
        builder.AddService("java-api");

        var resource = Assert.IsType<JavaAppExecutableResource>(
            Assert.Single(builder.Resources, r => r.Name == "java-api"));
        Assert.Equal(
            Path.GetFullPath(Path.Combine(checkout, "services", "api")),
            Path.GetFullPath(resource.WorkingDirectory));
    }

    [Fact]
    public void UseJava_WrapperPathBlock_RunsTheWrapperTheMonorepoKeepsAtItsRoot()
    {
        var (appHostDirectory, checkout) = CreateAppHost("""
                  workingDirectory: services/catalog
                  gradleTask: bootRun
                  wrapperPath: gradlew
                  port: 8080
            """, Path.Combine("services", "catalog"));
        var wrapper = WriteWrapper(checkout, GradleWrapperName);
        var builder = CreateBuilder(appHostDirectory);
        builder.UseJava();
        builder.AddService("java-api");

        var resource = Assert.IsType<JavaAppExecutableResource>(
            Assert.Single(builder.Resources, r => r.Name == "java-api"));
        Assert.Equal(Path.GetFullPath(wrapper), resource.Command);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(checkout, "services", "catalog")),
            Path.GetFullPath(resource.WorkingDirectory));
    }

    [Fact]
    public void AddService_WrapperMissingFromTheCheckout_ThrowsWithoutAddingAnyResource()
    {
        // No wrapper written into the checkout: the failure a developer would otherwise only see
        // once DCP tried to exec it.
        var (appHostDirectory, _) = CreateAppHost("""
                  gradleTask: bootRun
                  port: 8080
            """);
        var builder = CreateBuilder(appHostDirectory);
        builder.UseJava();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddService("java-api"));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains(GradleWrapperName, ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "java-api");
    }

    [Fact]
    public void AddService_KindJavaWithoutUseJava_ThrowsNamingTheKind()
    {
        var (appHostDirectory, _) = CreateAppHost("""
                  mavenGoal: spring-boot:run
                  port: 8080
            """);
        var builder = CreateBuilder(appHostDirectory);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddService("java-api"));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("java", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "java-api");
    }

    [Fact]
    public void AddService_MalformedJavaBlock_ThrowsWithoutAddingAnyResource()
    {
        var (appHostDirectory, _) = CreateAppHost("""
                  mavenGoal: spring-boot:run
            """);
        var builder = CreateBuilder(appHostDirectory);
        builder.UseJava();

        // Reported from Validate, which core runs before this service touches the app model.
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddService("java-api"));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("port", ex.Message);
        Assert.DoesNotContain(builder.Resources, r => r.Name == "java-api");
    }

    [Fact]
    public void AddService_EmptyJavaBlock_DoesNotClaimTheBlockIsAbsent()
    {
        // 'java:' with nothing under it reaches the handler as the same null an absent key does, so
        // the message has to cover both rather than sending the reader looking for a block they can
        // see in front of them.
        var (appHostDirectory, _) = CreateAppHost("");
        var builder = CreateBuilder(appHostDirectory);
        builder.UseJava();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => builder.AddService("java-api"));

        Assert.Contains("java-api", ex.Message);
        Assert.Contains("missing or empty", ex.Message);
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
