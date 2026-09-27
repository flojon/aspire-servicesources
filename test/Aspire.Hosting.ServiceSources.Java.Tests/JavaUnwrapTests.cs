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
    private const string JavaWithContainer = """
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
        """;

    private static IDistributedApplicationBuilder CreateAppHost(string localJson, string catalog = JavaWithContainer)
    {
        var appHostDirectory = CreateTempDirectory();

        File.WriteAllText(Path.Combine(appHostDirectory, "servicesources.yaml"), catalog);
        File.WriteAllText(Path.Combine(appHostDirectory, "servicesources.local.json"), localJson);

        return CreateBuilder(appHostDirectory);
    }

    private static string LocalPathJson(string serviceName, string checkout) => $$"""
        {
          "services": {
            "{{serviceName}}": { "source": "local", "local": { "path": {{System.Text.Json.JsonSerializer.Serialize(checkout)}} } }
          }
        }
        """;

    private sealed class UndeclaredTypeKind : ILocalResourceKind
    {
        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            builder.AddResource(new UndeclaredTypeResource(serviceName, repoRoot));
    }

    private sealed class UndeclaredTypeResource(string name, string workingDirectory)
        : ExecutableResource(name, "tool", workingDirectory), IResourceWithServiceDiscovery;

    private sealed class MisdeclaredTypeKind : ILocalResourceKind
    {
        public Type ResourceType => typeof(ContainerResource);

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            builder.AddResource(new UndeclaredTypeResource(serviceName, repoRoot));
    }

    private sealed class NullTypeKind : ILocalResourceKind
    {
        public Type ResourceType => null!;

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            builder.AddResource(new UndeclaredTypeResource(serviceName, repoRoot));
    }

    private sealed class FaultingTypeKind : ILocalResourceKind
    {
        public Type ResourceType => throw new InvalidOperationException("broken");

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException();
    }

    /// <summary>A java kind as it behaves in an AppHost that does not reference the Java package.</summary>
    private sealed class JavaPackageMissingKind : ILocalResourceKind
    {
        public Type ResourceType => throw new FileNotFoundException(
            "Could not load file or assembly.",
            "CommunityToolkit.Aspire.Hosting.Java, Version=13.3.0.0, Culture=neutral, PublicKeyToken=null");

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void AddService_OnAContainerSourcedJavaServiceWithoutTheJavaPackage_StillResolves()
    {
        var builder = CreateAppHost("""
            { "services": { "java-api": { "source": "container" } } }
            """);
        builder.AddLocalKind("java", new JavaPackageMissingKind());

        var ex = Record.Exception(() => builder.AddService("java-api").Unwrap<ProjectResource>(_ => { }));

        // Installing the package would let 'local' produce a Java resource, so this skips, not throws.
        Assert.Null(ex);
        Assert.Contains("Unwrap<ProjectResource>", Assert.Single(ServiceSourcesWarnings.For(builder).Messages));
    }

    [Fact]
    public void Unwrap_Delegate_ForATypeNoDeclaredSourceProduces_ThrowsOnAnOutOfBandSourceToo()
    {
        var builder = CreateAppHost("""
            { "services": { "java-api": { "source": "disabled" } } }
            """);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("java-api")
            .Unwrap<ProjectResource>(_ => { }));

        Assert.Contains("Unwrap<ProjectResource> can never apply", ex.Message);
    }

    [Fact]
    public void Unwrap_Delegate_ImpossibleTypeMessage_MarksOutOfBandSourcesAndNamesPublicTypes()
    {
        var builder = CreateAppHost("""
            { "services": { "java-api": { "source": "container" } } }
            """, JavaWithContainer + """

                kubernetes:
                  service: java-api-svc
                  port: 8080
            """);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("java-api")
            .Unwrap<ProjectResource>(_ => { }));

        Assert.Contains("'kubernetes' (out of band)", ex.Message);
        Assert.Contains($"'container' ({nameof(ContainerResource)})", ex.Message);
    }

    [Fact]
    public void AddService_OnAKindWhoseResourceTypeIsNull_Throws()
    {
        var checkout = CreateTempDirectory();
        var builder = CreateAppHost(LocalPathJson("tool", checkout), """
            services:
              tool:
                repository: https://github.com/example/tool
                kind: nulltype
            """);
        builder.AddLocalKind("nulltype", new NullTypeKind());

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("tool"));

        Assert.Contains("returned null", ex.Message);
        Assert.Contains("'nulltype'", ex.Message);
    }

    [Fact]
    public void Unwrap_Delegate_OnAKindWhoseResourceTypeFaults_NamesTheServiceAndKind()
    {
        var builder = CreateAppHost("""
            { "services": { "tool": { "source": "container" } } }
            """, """
            services:
              tool:
                repository: https://github.com/example/tool
                kind: faulting
                container:
                  image: example/tool
                  port: 8080
            """);
        builder.AddLocalKind("faulting", new FaultingTypeKind());

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("tool")
            .Unwrap<ProjectResource>(_ => { }));

        Assert.Contains("'tool'", ex.Message);
        Assert.Contains("'faulting'", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public void AddService_OnAKindWhoseResourceIsNotItsDeclaredType_Throws()
    {
        var checkout = CreateTempDirectory();
        var builder = CreateAppHost(LocalPathJson("tool", checkout), """
            services:
              tool:
                repository: https://github.com/example/tool
                kind: misdeclared
            """);
        builder.AddLocalKind("misdeclared", new MisdeclaredTypeKind());

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("tool"));

        Assert.Contains(nameof(ILocalResourceKind.ResourceType), ex.Message);
        Assert.Contains(nameof(ContainerResource), ex.Message);
    }

    [Fact]
    public void Unwrap_Delegate_ForATypeNoDeclaredSourceProduces_Throws()
    {
        var builder = CreateAppHost("""
            { "services": { "java-api": { "source": "container" } } }
            """);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("java-api")
            .Unwrap<ProjectResource>(_ => { }));

        Assert.Contains("Unwrap<ProjectResource> can never apply", ex.Message);
        Assert.Contains($"'local' ({nameof(JavaAppExecutableResource)})", ex.Message);
        Assert.Contains("'container'", ex.Message);
    }

    [Fact]
    public void Unwrap_Delegate_OnTheCatalogsDefaultContainerSource_WarnsBecauseLocalWouldApply()
    {
        // No developer override at all: a mismatch under the catalog's own default is still a
        // source choice, not a mistake, because switching to 'local' makes the call apply.
        var builder = CreateAppHost("""{ "services": {} }""", JavaWithContainer.Replace(
            "    kind: java", "    defaultSource: container\n    kind: java", StringComparison.Ordinal));
        var callbackRan = false;

        builder.AddService("java-api").Unwrap<JavaAppExecutableResource>(_ => callbackRan = true);

        Assert.False(callbackRan);
        Assert.Contains("Unwrap<JavaAppExecutableResource>", Assert.Single(ServiceSourcesWarnings.For(builder).Messages));
    }

    [Fact]
    public void Unwrap_Delegate_OnAKindThatDeclaresNoType_NeverThrows()
    {
        var checkout = CreateTempDirectory();
        var builder = CreateAppHost(LocalPathJson("tool", checkout), """
            services:
              tool:
                repository: https://github.com/example/tool
                kind: undeclared
            """);
        builder.AddLocalKind("undeclared", new UndeclaredTypeKind());

        var ex = Record.Exception(() => builder.AddService("tool").Unwrap<ProjectResource>(_ => { }));

        Assert.Null(ex);
        Assert.Contains("Unwrap<ProjectResource>", Assert.Single(ServiceSourcesWarnings.For(builder).Messages));
    }

    [Fact]
    public void Unwrap_Delegate_OnALocalJavaService_ConfiguresTheJavaResource()
    {
        var checkout = CreateTempDirectory();
        WriteWrapper(checkout, MavenWrapperName);
        var builder = CreateAppHost($$"""
            {
              "services": {
                "java-api": { "source": "local", "local": { "path": {{System.Text.Json.JsonSerializer.Serialize(checkout)}} } }
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
