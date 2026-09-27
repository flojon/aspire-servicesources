using Aspire.Hosting.ApplicationModel;
using static Aspire.Hosting.ServiceSources.Java.Tests.TestHelpers;
using Xunit;

namespace Aspire.Hosting.ServiceSources.Java.Tests;

/// <summary>
/// Covers <c>Unwrap&lt;JavaAppExecutableResource&gt;(configure)</c> through a real
/// <c>AddService()</c>, the shape <c>docs/kinds.md</c> recommends for reaching the rest of the Java
/// integration.
/// </summary>
[Trait("IO", "true")]
public class JavaUnwrapTests
{
    private static IDistributedApplicationBuilder CreateAppHost(string localJson)
    {
        var appHostDirectory = CreateTempDirectory();

        File.WriteAllText(Path.Combine(appHostDirectory, "servicesources.yaml"), """
            services:
              java-api:
                repository: https://github.com/example/java-api
                kind: java
                java:
                  mavenGoal: spring-boot:run
                  port: 8080
                container:
                  image: example/java-api
                  port: 8080
            """);
        File.WriteAllText(Path.Combine(appHostDirectory, "servicesources.local.json"), localJson);

        return CreateBuilder(appHostDirectory);
    }

    [Fact]
    public void Unwrap_Delegate_OnAPathJavaService_ConfiguresTheJavaResource()
    {
        var checkout = CreateTempDirectory();
        WriteWrapper(checkout, MavenWrapperName);
        var builder = CreateAppHost($$"""
            {
              "services": {
                "java-api": { "source": "path", "path": { "path": {{System.Text.Json.JsonSerializer.Serialize(checkout)}} } }
              }
            }
            """);
        JavaAppExecutableResource? configured = null;

        builder.AddService("java-api")
            .Unwrap<JavaAppExecutableResource>(java => configured = java.Resource)
            .WithEnvironment("A", "B");

        Assert.NotNull(configured);
        Assert.Same(configured, Assert.Single(builder.Resources, r => r.Name == "java-api"));
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void Unwrap_Delegate_OnAJavaServiceSwitchedToContainer_SkipsWithAWarning()
    {
        var builder = CreateAppHost("""
            { "services": { "java-api": { "source": "container" } } }
            """);
        var callbackRan = false;

        var ex = Record.Exception(() => builder.AddService("java-api")
            .Unwrap<JavaAppExecutableResource>(_ => callbackRan = true));

        Assert.Null(ex);
        Assert.False(callbackRan);
        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("Unwrap<JavaAppExecutableResource>", message);
        Assert.Contains("'container'", message);
    }
}
