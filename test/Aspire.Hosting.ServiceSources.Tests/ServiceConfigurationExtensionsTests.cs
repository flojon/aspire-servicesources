using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// Covers issue #53: the AppHost applying its own configuration to a resolved service, now through
/// native vocabulary on ServiceResource's own interfaces rather than Configure&lt;T&gt;. Each test
/// drives a real source so the resource under test is the one an AppHost would actually get.
/// </summary>
public class ServiceConfigurationExtensionsTests
{
    private static readonly ServiceDefinition ContainerDefinition = new ServiceMetadata
    {
        Container = new ContainerMetadata { Image = "nginxdemos/hello", Port = 8080 },
    }.ToDefinition("servicesources.yaml", "payments", TestHelpers.EmptyRepositories);

    private static readonly ServiceDefinition KubernetesDefinition = new ServiceMetadata
    {
        Kubernetes = new KubernetesMetadata { Service = "orders", Port = 8080 },
    }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static readonly ServiceDefinition UrlDefinition = new ServiceMetadata
    {
        Url = new UrlMetadata { Url = "https://orders.example.com" },
    }.ToDefinition("servicesources.yaml", "inventory", TestHelpers.EmptyRepositories);

    private sealed class FixedPortAllocator : IPortAllocator
    {
        public bool IsAvailable(int port) => true;

        public int AllocatePort() => 51234;

        /// <remarks>
        /// The service-side source forwards exactly one port, so a call here would be a bug rather
        /// than a case worth standing in for.
        /// </remarks>
        public IReadOnlyList<int> AllocatePorts(int count) => throw new NotSupportedException();
    }

    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    private static IResourceBuilder<ServiceResource> AddContainerService(IDistributedApplicationBuilder builder) =>
        new ContainerSource().Resolve(builder, "payments", ContainerDefinition, new ServiceDeveloperConfig { Source = "container" });

    private static IResourceBuilder<ServiceResource> AddUrlService(IDistributedApplicationBuilder builder) =>
        new UrlSource().Resolve(builder, "inventory", UrlDefinition, new ServiceDeveloperConfig { Source = "url" });

    [Fact]
    public void WithEnvironment_AppliesToTheRealResource()
    {
        var builder = Builder();

        var service = AddContainerService(builder).WithEnvironment("DBUSERNAME", "postgres");

        Assert.NotEmpty(service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
    }

    [Fact]
    public void NativeCalls_ReturnTheSameBuilder_SoCapabilitiesCanBeChained()
    {
        var builder = Builder();
        var service = AddContainerService(builder);

        var returned = service.WithEnvironment("A", "B").WithArgs("--verbose");

        Assert.Same(service.Resource, returned.Resource);
    }

    [Fact]
    public void WaitFor_AppliesToTheRealResource()
    {
        var builder = Builder();
        var dependency = builder.AddResource(new ServiceContainerResource("redis")).WithImage("redis");

        var service = AddContainerService(builder).WaitFor(dependency);

        Assert.NotEmpty(service.Resource.Annotations.OfType<WaitAnnotation>());
    }

    [Fact]
    public void Unwrap_ReturnsATypedBuilderForTheUnderlyingResource()
    {
        var builder = Builder();

        var typed = AddContainerService(builder).Unwrap<ContainerResource>();

        Assert.Equal("payments", typed.Resource.Name);
    }

    [Fact]
    public void Unwrap_MismatchedType_ThrowsNamingTheService()
    {
        var builder = Builder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => AddContainerService(builder).Unwrap<ProjectResource>());

        Assert.Contains("payments", ex.Message);
        Assert.Contains("container", ex.Message);
    }

    [Fact]
    public void WithEnvironment_OnUrlSource_SkipsWithoutThrowing_SoSourceSwitchingKeepsWorking()
    {
        var builder = Builder();
        var callbackRan = false;

        // A developer switching this service to "url" in their own servicesources.local.json must
        // not break a Program.cs they don't own.
        var service = AddUrlService(builder).WithEnvironment("A", () =>
        {
            callbackRan = true;
            return "B";
        });

        Assert.False(callbackRan);
        Assert.NotNull(service);
    }

    [Fact]
    public void WithEnvironment_OnUrlSource_ReportsTheSkip()
    {
        var builder = Builder();

        AddUrlService(builder).WithEnvironment("A", "B");

        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        Assert.Contains("inventory", message);
        Assert.Contains("'url'", message);
        Assert.Contains("servicesources.local.json", message);
    }

    [Fact]
    public void ManyCallsOnOneUrlService_ReportOneAggregatedSkip()
    {
        var builder = Builder();
        var service = AddUrlService(builder);

        // The shape this package is built for: one service carrying a lot of AppHost configuration.
        // Before these were grouped, switching it to an out-of-band source emitted one near-identical
        // warning per call.
        for (var i = 0; i < 25; i++)
        {
            service.WithEnvironment("A", "B");
        }

        service.WaitFor(builder.AddResource(new ServiceContainerResource("migrations")).WithImage("migrate"));

        var message = Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
        // "calls" rather than "WithEnvironment calls": the same message also stands for a consumer's
        // dropped WaitFor, so the tally names each call and the summary only counts them.
        Assert.Contains("26 calls", message);
        Assert.Contains("WithEnvironment ×25", message);
        Assert.Contains("WaitFor/WaitForCompletion", message);
    }

    [Fact]
    public void TwoDifferentUrlServices_ReportSkipsSeparately()
    {
        var builder = Builder();

        AddUrlService(builder).WithEnvironment("A", "B");
        new UrlSource()
            .Resolve(builder, "billing", UrlDefinition, new ServiceDeveloperConfig { Source = "url" })
            .WithEnvironment("A", "B");

        // Grouping is per service, not global — each service names itself and its own remedy.
        Assert.Equal(2, ServiceSourcesWarnings.For(builder).Messages.Count);
    }

    [Fact]
    public void WithEnvironment_OnKubernetesSource_SkipsRatherThanConfiguringThePortForward()
    {
        var builder = Builder();
        var service = new KubernetesSource(new FixedPortAllocator()).Resolve(
            builder, "orders", KubernetesDefinition,
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev" } });

        // The port-forward executable would accept environment variables happily, so skipping has to
        // be driven by the source rather than by a capability check.
        service.WithEnvironment("A", "B");

        Assert.Empty(service.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>());
        Assert.Contains("port-forward", Assert.Single(ServiceSourcesWarnings.For(builder).Messages));
    }

    [Fact]
    public void WaitFor_OnKubernetesSource_StillApplies_BecauseOrderingThePortForwardIsCorrect()
    {
        var builder = Builder();
        var migrations = builder.AddResource(new ServiceContainerResource("migrations")).WithImage("migrate");
        var service = new KubernetesSource(new FixedPortAllocator()).Resolve(
            builder, "orders", KubernetesDefinition,
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev" } });

        // Unlike environment variables, start ordering is not "configuring the wrong process": the
        // port-forward is a real registered executable, and holding it back until migrations finish is
        // exactly what the AppHost asked for. Skipping it lost the ordering silently the moment
        // someone switched a service to "kubernetes".
        service.WaitForCompletion(migrations);

        Assert.NotEmpty(service.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Empty(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void WaitFor_OnUrlSource_StillSkips_BecauseNothingIsRegisteredToOrder()
    {
        var builder = Builder();
        var migrations = builder.AddResource(new ServiceContainerResource("migrations")).WithImage("migrate");

        var service = AddUrlService(builder).WaitForCompletion(migrations);

        // A "url" service's resource is never registered, so there is no process to hold back.
        Assert.Empty(service.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public async Task SkippedConfiguration_IsLoggedAtStartup()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
            services:
              inventory:
                url:
                  url: https://orders.example.com
            """);
        File.WriteAllText(
            Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" } } }""");
        var builder = TestHelpers.CreateBuilderThatCanStart(dir);

        builder.AddService("inventory").WithEnvironment("A", "B");

        // Buffered during composition — there is no logger yet — and flushed here.
        var ex = await Record.ExceptionAsync(() => TestHelpers.PublishBeforeStartEventAsync(builder));

        Assert.Null(ex);
        Assert.Single(ServiceSourcesWarnings.For(builder).Messages);
    }

    [Fact]
    public void Unwrap_OnUrlSource_StillThrows_BecauseItMustReturnABuilder()
    {
        var builder = Builder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => AddUrlService(builder).Unwrap<IResourceWithEnvironment>());

        Assert.Contains("inventory", ex.Message);
        Assert.Contains("'url'", ex.Message);
    }
}
