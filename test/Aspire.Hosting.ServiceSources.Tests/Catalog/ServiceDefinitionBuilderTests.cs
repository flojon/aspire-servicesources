using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Prepare;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class ServiceDefinitionBuilderTests
{
    [Fact]
    public void WithRepository_SetsRepositoryAndDefaultRef()
    {
        var builder = new ServiceCatalogBuilder();
        var definition = builder.AddService("orders")
            .WithRepository("https://github.com/example/repo", defaultRef: "main")
            .WithProject("src/Api.csproj")
            .Build();

        Assert.Equal("https://github.com/example/repo", definition.Repository.Url);
        Assert.Equal("src/Api.csproj", definition.Project);
        Assert.Equal("main", definition.Repository.DefaultRef);
    }

    [Fact]
    public void WithProject_CalledTwice_ThrowsNamingServiceAndBlock()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo")
            .WithProject("src/Api.csproj");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithProject("src/Other.csproj"));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithProject", ex.Message, StringComparison.Ordinal);
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
    public void WithRepository_EmptyUrl_ThrowsNamingService()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithRepository(""));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("repository url is required", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithRepository_WhitespaceUrl_ThrowsNamingService()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithRepository("   "));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("repository url is required", ex.Message, StringComparison.Ordinal);
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
    public void WithDefaultSource_SetsTheField()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo")
            .WithDefaultSource("repository")
            .Build();

        Assert.Equal("repository", definition.DefaultSource);
    }

    [Fact]
    public void WithDefaultSource_CalledTwice_ThrowsNamingServiceAndBlock()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo")
            .WithDefaultSource("repository");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithDefaultSource("url"));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithDefaultSource", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// "local" is retired, not aliased: <c>WithDefaultSource("local")</c> gets the same named
    /// migration a yaml <c>defaultSource: local</c> does, rather than falling through to the generic
    /// "not a valid source" message.
    /// </summary>
    [Fact]
    public void WithDefaultSource_Local_ReportsTheRenameToRepository()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithDefaultSource("local"));

        Assert.Contains("Service 'orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithDefaultSource", ex.Message, StringComparison.Ordinal);
        Assert.Contains("renamed", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'repository'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("not a valid source", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithDefaultSource_InvalidValue_ThrowsNamingTheSixValues()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithDefaultSource("bogus"));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bogus", ex.Message, StringComparison.Ordinal);
        Assert.Contains("repository", ex.Message, StringComparison.Ordinal);
        Assert.Contains("url", ex.Message, StringComparison.Ordinal);
        Assert.Contains("kubernetes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("container", ex.Message, StringComparison.Ordinal);
        Assert.Contains("path", ex.Message, StringComparison.Ordinal);
        Assert.Contains("disabled", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// "disabled" is a valid <c>WithDefaultSource</c> value like every other source — a service can
    /// ship off by default until a developer's own <c>servicesources.local.json</c> opts it back in.
    /// </summary>
    [Fact]
    public void WithDefaultSource_Disabled_SetsTheField()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo")
            .WithDefaultSource("disabled")
            .Build();

        Assert.Equal("disabled", definition.DefaultSource);
    }

    /// <summary>
    /// A null <c>source</c> can reach here without the compiler's help — the AspireExport interop
    /// boundary from a guest language is not type-checked the way a direct C# call is. There is no
    /// explicit null guard before <c>ValidateSourceName</c>'s <c>HashSet&lt;string&gt;.Contains</c>
    /// call, but that is not a gap: <c>HashSet&lt;string&gt;.Contains(null)</c> returns <see
    /// langword="false"/> rather than throwing, so a null value already falls through to the same
    /// domain exception an invalid string gets, not a raw <see cref="ArgumentNullException"/>.
    /// </summary>
    [Fact]
    public void WithDefaultSource_NullValue_ThrowsConfigurationExceptionNotArgumentNullException()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithDefaultSource(null!));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("not a valid source", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithDefaultSource_NotCalled_DefaultSourceStaysNull()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/repo")
            .Build();

        Assert.Null(definition.DefaultSource);
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
    public void WithContainer_SchemeOmitted_SchemeIsNull()
    {
        var definition = new ServiceCatalogBuilder().AddService("payments")
            .WithContainer("nginxdemos/hello", port: 80)
            .Build();

        Assert.Null(definition.Container!.Scheme);
    }

    [Fact]
    public void WithContainer_SchemeGiven_SetsContainerScheme()
    {
        var definition = new ServiceCatalogBuilder().AddService("payments")
            .WithContainer("nginxdemos/hello", port: 80, scheme: "https")
            .Build();

        Assert.Equal("https", definition.Container!.Scheme);
    }

    [Fact]
    public void WithKubernetes_SchemeGiven_SetsKubernetesScheme()
    {
        var definition = new ServiceCatalogBuilder().AddService("payments")
            .WithKubernetes("payments", port: 8080, scheme: "https")
            .Build();

        Assert.Equal("https", definition.Kubernetes!.Scheme);
    }

    [Fact]
    public void WithContainerAndWithKubernetes_SchemesAreIndependent()
    {
        var definition = new ServiceCatalogBuilder().AddService("payments")
            .WithContainer("nginxdemos/hello", port: 80)
            .WithKubernetes("payments", port: 8080, scheme: "https")
            .Build();

        Assert.Null(definition.Container!.Scheme);
        Assert.Equal("https", definition.Kubernetes!.Scheme);
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
            "catalog", PreparePlan.ServiceLabel("catalog"), definition.Repository.Prepare, developer: null,
            managedCheckout: true, windows: false);

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

    [Fact]
    public void WithPath_SetsPath()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithPath("services/orders")
            .WithProject("src/Api.csproj")
            .Build();

        Assert.Equal("services/orders", definition.Path);
        Assert.Equal("src/Api.csproj", definition.Project);
    }

    [Fact]
    public void WithPath_CalledTwice_ThrowsNamingServiceAndBlock()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders")
            .WithPath("services/orders");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => chain.WithPath("services/orders-again"));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithPath", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithPath_BlankValue_Throws()
    {
        var chain = new ServiceCatalogBuilder().AddService("orders");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithPath("   "));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithPath_NotCalled_PathStaysNull()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/orders")
            .Build();

        Assert.Null(definition.Path);
    }

    /// <summary>
    /// Design finding 8: <c>WithPath</c> does not share <c>WithRepository</c>'s
    /// <c>_repositorySource</c> guard — a service naming both is the "combining sources" pattern, not
    /// a repeated-block error. This was a real modeling mistake in an earlier draft of the design.
    /// </summary>
    [Fact]
    public void WithPath_CombinedWithWithRepository_BothResolve()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithRepository("https://github.com/example/orders")
            .WithPath("services/orders")
            .WithProject("src/Api.csproj")
            .Build();

        Assert.Equal("https://github.com/example/orders", definition.Repository.Url);
        Assert.Equal("services/orders", definition.Path);
    }

    [Fact]
    public void WithPath_CombinedWithWithUrlAndWithContainerAndWithKubernetes_AllResolve()
    {
        var definition = new ServiceCatalogBuilder().AddService("orders")
            .WithPath("services/orders")
            .WithProject("src/Api.csproj")
            .WithUrl("https://orders.example.com")
            .WithContainer("company/orders", 8080)
            .WithKubernetes("orders-svc")
            .Build();

        Assert.Equal("services/orders", definition.Path);
        Assert.Equal("https://orders.example.com", definition.Url!.Url);
        Assert.Equal("company/orders", definition.Container!.Image);
        Assert.Equal("orders-svc", definition.Kubernetes!.Service);
    }

    private static ServiceDefinitionBuilder Orders() =>
        new ServiceCatalogBuilder().AddService("orders").WithRepository("https://github.com/example/repo");

    [Fact]
    public void WithLaunchProfile_SetsDefinitionDotnet()
    {
        var definition = Orders().WithLaunchProfile("http").Build();

        Assert.Equal("http", definition.Dotnet!.LaunchProfileName);
        Assert.Equal("dotnet", definition.Kind);
    }

    [Fact]
    public void WithLaunchProfile_AfterBuild_DoesNotChangeBuiltDefinition()
    {
        var chain = Orders().WithLaunchProfile("http");
        var definition = chain.Build();

        chain.ExcludeLaunchProfile();

        Assert.Null(definition.Dotnet!.ExcludeLaunchProfile);
    }

    [Fact]
    public void ExcludeLaunchProfile_SetsExcludeTrue()
    {
        var definition = Orders().ExcludeLaunchProfile().Build();

        Assert.True(definition.Dotnet!.ExcludeLaunchProfile);
    }

    [Fact]
    public void WithLaunchProfile_CalledTwice_ThrowsAlreadyCalled()
    {
        var chain = Orders().WithLaunchProfile("http");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithLaunchProfile("https"));

        Assert.Contains("WithLaunchProfile", ex.Message, StringComparison.Ordinal);
        Assert.Contains("already called", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludeLaunchProfile_CalledTwice_ThrowsAlreadyCalled()
    {
        var chain = Orders().ExcludeLaunchProfile();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => chain.ExcludeLaunchProfile());

        Assert.Contains("ExcludeLaunchProfile", ex.Message, StringComparison.Ordinal);
        Assert.Contains("already called", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithLaunchProfile_ThenWithKindJava_Build_Throws()
    {
        var chain = Orders().WithLaunchProfile("http").WithKind("java");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => chain.Build());

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("java", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithKindJava_ThenWithLaunchProfile_Build_Throws()
    {
        var chain = Orders().WithKind("java").WithLaunchProfile("http");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => chain.Build());

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("java", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WithLaunchProfile_AndExclude_Build_ThrowsNamingBothFields()
    {
        var chain = Orders().WithLaunchProfile("http").ExcludeLaunchProfile();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => chain.Build());

        Assert.Contains("launchProfileName", ex.Message, StringComparison.Ordinal);
        Assert.Contains("excludeLaunchProfile", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void WithLaunchProfile_NullOrBlank_ThrowsImmediately(string? name)
    {
        var chain = Orders();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => chain.WithLaunchProfile(name!));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("WithLaunchProfile", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoLaunchProfileCall_DotnetIsNull() =>
        Assert.Null(Orders().Build().Dotnet);
}
