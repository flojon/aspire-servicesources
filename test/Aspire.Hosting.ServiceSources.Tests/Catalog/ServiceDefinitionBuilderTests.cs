using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Prepare;

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

        Assert.Equal("https://github.com/example/repo", definition.Repository.Url);
        Assert.Equal("src/Api.csproj", definition.Project);
        Assert.Equal("main", definition.Repository.DefaultRef);
    }

    [Fact]
    public void Build_CheckoutNameIsTheServiceName()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo")
            .Build();

        Assert.Equal("orders", definition.Repository.CheckoutName);
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
    public void WithHttpEndpoint_AfterWithContainer_SetsContainerScheme()
    {
        var definition = new ServiceCatalogBuilder().AddService("payments")
            .WithContainer("nginxdemos/hello", port: 80)
            .WithHttpEndpoint()
            .Build();

        Assert.Equal("http", definition.Container!.Scheme);
    }

    [Fact]
    public void WithHttpsEndpoint_AfterWithContainer_SetsContainerScheme()
    {
        var definition = new ServiceCatalogBuilder().AddService("payments")
            .WithContainer("nginxdemos/hello", port: 80)
            .WithHttpsEndpoint()
            .Build();

        Assert.Equal("https", definition.Container!.Scheme);
    }

    [Fact]
    public void WithHttpsEndpoint_AfterWithKubernetes_SetsKubernetesScheme()
    {
        var definition = new ServiceCatalogBuilder().AddService("payments")
            .WithKubernetes("payments", port: 8080)
            .WithHttpsEndpoint()
            .Build();

        Assert.Equal("https", definition.Kubernetes!.Scheme);
    }

    [Fact]
    public void WithHttpsEndpoint_TargetsOnlyTheMostRecentlyDeclaredSource()
    {
        var definition = new ServiceCatalogBuilder().AddService("payments")
            .WithContainer("nginxdemos/hello", port: 80)
            .WithKubernetes("payments", port: 8080)
            .WithHttpsEndpoint()
            .Build();

        Assert.Null(definition.Container!.Scheme);
        Assert.Equal("https", definition.Kubernetes!.Scheme);
    }

    [Fact]
    public void WithHttpEndpoint_NoSourceDeclaredYet_Throws()
    {
        var chain = new ServiceCatalogBuilder().AddService("payments");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithHttpEndpoint());

        Assert.Contains("payments", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithHttpEndpoint_AfterWithUrlOnly_Throws()
    {
        // WithUrl doesn't carry a Scheme, so it isn't a valid target either.
        var chain = new ServiceCatalogBuilder().AddService("payments").WithUrl("https://a.example");

        Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithHttpEndpoint());
    }

    [Fact]
    public void WithHttpsEndpoint_CalledTwiceForSameBlock_Throws()
    {
        var chain = new ServiceCatalogBuilder().AddService("payments")
            .WithContainer("nginxdemos/hello", port: 80)
            .WithHttpsEndpoint();

        Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithHttpEndpoint());
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

    [Fact]
    public void WithPrepare_SetsCommandWindowsCommandAndMode()
    {
        var definition = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/example/catalog")
            .WithPrepare(["./prepare.sh"], windowsCommand: ["prepare.cmd"], mode: PrepareMode.Once)
            .Build();

        Assert.NotNull(definition.Repository.Prepare);
        Assert.Equal(["./prepare.sh"], definition.Repository.Prepare.Command!);
        Assert.Equal(["prepare.cmd"], definition.Repository.Prepare.WindowsCommand!);
        Assert.Equal("once", definition.Repository.Prepare.Mode);
    }

    /// <summary>
    /// The enum is stored as the spelling the yaml block writes, which is what leaves one
    /// representation for <see cref="PreparePlan"/> to parse whichever file the block came from.
    /// </summary>
    [Fact]
    public void WithPrepare_ModeOmitted_IsTheDefaultModeWritten()
    {
        var definition = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/example/catalog")
            .WithPrepare(["./prepare.sh"])
            .Build();

        Assert.Null(definition.Repository.Prepare!.WindowsCommand);
        Assert.Equal("oncePerCommit", definition.Repository.Prepare.Mode);
    }

    [Fact]
    public void WithPrepare_UndefinedMode_ThrowsNamingTheFourSpellings()
    {
        var chain = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/example/catalog");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithPrepare(["./prepare.sh"], mode: (PrepareMode)99));

        Assert.Contains("catalog", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'oncePerCommit'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'once'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'always'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'never'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one place <see cref="RepositoryDefinition.Prepare"/> is consumed, reached with what
    /// <see cref="ServiceDefinitionBuilder.WithPrepare"/> produced: the block a code catalog declares
    /// resolves into a step exactly as the yaml block it replaces does.
    /// </summary>
    [Fact]
    public void WithPrepare_Metadata_ResolvesIntoAPreparePlanStep()
    {
        var definition = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/example/catalog")
            .WithPrepare(["./prepare.sh", "--full"], mode: PrepareMode.Once)
            .Build();

        var plan = PreparePlan.For(
            "catalog", definition.Repository.Prepare, developer: null, managedCheckout: true, windows: false);

        Assert.NotNull(plan.Step);
        Assert.Equal<string[]>(["./prepare.sh", "--full"], [.. plan.Step!.Command]);
        Assert.Equal(PrepareMode.Once, plan.Step.Mode);
    }

    [Fact]
    public void WithPrepare_CalledTwice_ThrowsNamingServiceAndBlock()
    {
        var chain = new ServiceCatalogBuilder().AddService("catalog")
            .WithRepository("https://github.com/example/catalog")
            .WithPrepare(["a.sh"]);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithPrepare(["b.sh"]));

        Assert.Contains("catalog", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithPrepare", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithPrepare_NotCalled_PrepareStaysNull()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/orders")
            .Build();

        Assert.Null(definition.Repository.Prepare);
    }
}
