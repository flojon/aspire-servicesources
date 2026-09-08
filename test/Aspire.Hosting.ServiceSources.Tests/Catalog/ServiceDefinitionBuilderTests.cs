using Aspire.Hosting.ServiceSources.Catalog;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class ServiceDefinitionBuilderTests
{
    [Fact]
    public void WithRepository_SetsRepositoryProjectAndDefaultRef()
    {
        var builder = new ServiceCatalogBuilder();
        var definition = builder.AddService("orders")
            .WithRepository("https://github.com/example/repo", project: "src/Api.csproj", defaultRef: "main")
            .Build();

        Assert.Equal("https://github.com/example/repo", definition.Repository);
        Assert.Equal("src/Api.csproj", definition.Project);
        Assert.Equal("main", definition.DefaultRef);
    }

    [Fact]
    public void WithRepository_ProjectOmitted_DefaultsToEmpty()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo")
            .Build();

        Assert.Equal("", definition.Project);
    }

    [Fact]
    public void WithRepository_CalledTwice_ThrowsNamingServiceAndBlock()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithRepository("https://github.com/example/other"));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithRepository", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithUrl_SetsUrlBlock()
    {
        var definition = new ServiceCatalogBuilder().AddService("inventory")
            .WithUrl("https://httpbin.org")
            .Build();

        Assert.NotNull(definition.Url);
        Assert.Equal("https://httpbin.org", definition.Url.Url);
    }

    [Fact]
    public void WithUrl_CalledTwice_Throws()
    {
        var chain = new ServiceCatalogBuilder().AddService("inventory").WithUrl("https://a.example");

        Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithUrl("https://b.example"));
    }

    [Fact]
    public void WithContainer_SetsContainerBlock()
    {
        var definition = new ServiceCatalogBuilder().AddService("payments")
            .WithContainer("nginxdemos/hello", port: 80, defaultTag: "latest")
            .Build();

        Assert.NotNull(definition.Container);
        Assert.Equal("nginxdemos/hello", definition.Container.Image);
        Assert.Equal(80, definition.Container.Port);
        Assert.Equal("latest", definition.Container.DefaultTag);
    }

    [Fact]
    public void WithContainer_CalledTwice_Throws()
    {
        var chain = new ServiceCatalogBuilder().AddService("payments").WithContainer("a", 80);

        Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithContainer("b", 8080));
    }

    [Fact]
    public void WithKubernetes_SetsKubernetesBlock()
    {
        var definition = new ServiceCatalogBuilder().AddService("payments")
            .WithKubernetes("payments", port: 8080)
            .Build();

        Assert.NotNull(definition.Kubernetes);
        Assert.Equal("payments", definition.Kubernetes.Service);
        Assert.Equal(8080, definition.Kubernetes.Port);
    }

    [Fact]
    public void WithKubernetes_CalledTwice_Throws()
    {
        var chain = new ServiceCatalogBuilder().AddService("payments").WithKubernetes("payments");

        Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithKubernetes("other"));
    }

    [Fact]
    public void WithContainerAndWithKubernetes_OnOneEntry_BothSet()
    {
        // Design finding 4: one entry may carry every source at once.
        var definition = new ServiceCatalogBuilder().AddService("payments")
            .WithContainer("nginxdemos/hello", port: 80)
            .WithKubernetes("payments", port: 8080)
            .Build();

        Assert.NotNull(definition.Container);
        Assert.NotNull(definition.Kubernetes);
    }

    [Fact]
    public void WithKind_SetsKindAndOptions()
    {
        var options = new Dictionary<string, object> { ["mavenGoal"] = "spring-boot:run" };

        var definition = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/spring-projects/spring-petclinic")
            .WithKind("java", options)
            .Build();

        Assert.Equal("java", definition.Kind);
        Assert.Same(options, definition.KindOptions);
    }

    [Fact]
    public void WithKind_NoOptionsGiven_KindOptionsIsNull()
    {
        var definition = new ServiceCatalogBuilder().AddService("svc")
            .WithRepository("https://example.com/repo")
            .WithKind("custom")
            .Build();

        Assert.Equal("custom", definition.Kind);
        Assert.Null(definition.KindOptions);
    }

    [Fact]
    public void WithKind_CalledTwice_Throws()
    {
        var chain = new ServiceCatalogBuilder().AddService("svc")
            .WithRepository("https://example.com/repo")
            .WithKind("java");

        Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithKind("javascript"));
    }
}
