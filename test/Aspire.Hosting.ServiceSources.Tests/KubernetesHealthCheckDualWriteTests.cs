using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Sources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ServiceSourcesOverloadProbe;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;

namespace Aspire.Hosting.ServiceSources.Tests;

/// <summary>
/// A health check on a <c>"kubernetes"</c> service reaches the port-forward's real resource, and
/// stays skipped for every other out-of-band source. Assertions read the real resource's own
/// collection, since the facade alone cannot tell a dual-write from a facade-only write.
/// </summary>
[Trait("IO", "true")]
public class KubernetesHealthCheckDualWriteTests
{
    private const string Skipped = "WithHealthCheck/WithHttpHealthCheck";

    private sealed class DerivedHealthCheck() : HealthCheckAnnotation("derived");

    private static readonly ServiceDefinition KubernetesDefinition = new ServiceMetadata
    {
        Kubernetes = new KubernetesMetadata { Service = "orders", Port = 8080 },
    }.ToDefinition("servicesources.yaml", "orders", TestHelpers.EmptyRepositories);

    private static readonly ServiceDefinition UrlDefinition = new ServiceMetadata
    {
        Url = new UrlMetadata { Url = "https://orders.example.com" },
    }.ToDefinition("servicesources.yaml", "inventory", TestHelpers.EmptyRepositories);

    private static readonly ServiceDefinition BareDefinition =
        new ServiceMetadata().ToDefinition("servicesources.yaml", "billing", TestHelpers.EmptyRepositories);

    private sealed class FixedPortAllocator : IPortAllocator
    {
        public bool IsAvailable(int port) => true;

        public int AllocatePort() => 51234;

        public IReadOnlyList<int> AllocatePorts(int count) => throw new NotSupportedException();
    }

    private static IDistributedApplicationBuilder Builder() =>
        TestHelpers.CreateBuilder(TempDirectories.CreateSubdirectory().FullName);

    private static IResourceBuilder<ServiceResource> Kubernetes(IDistributedApplicationBuilder builder) =>
        new KubernetesSource(new FixedPortAllocator()).Resolve(
            builder, "orders", KubernetesDefinition,
            new ServiceDeveloperConfig { Source = "kubernetes", Kubernetes = new() { Context = "dev" } });

    private static ResourceAnnotationCollection RealAnnotations(IResourceBuilder<ServiceResource> service) =>
        ((ServiceResourceBuilder)service).Real!.Resource.Annotations;

    private static IReadOnlyList<string> Warnings(IDistributedApplicationBuilder builder) =>
        ServiceSourcesWarnings.For(builder).Messages;

    [Fact]
    public void WithHttpHealthCheck_OnKubernetes_AddsOneHealthCheckAnnotationToRealAndFacade_AndRecordsNoSkip()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        service.WithHttpHealthCheck("/healthz");

        var onReal = Assert.Single(
            RealAnnotations(service).OfType<HealthCheckAnnotation>(), a => a.Key.Contains("/healthz"));
        Assert.StartsWith("orders_", onReal.Key);
        Assert.EndsWith("_check", onReal.Key);
        Assert.Same(
            onReal,
            Assert.Single(service.Resource.Annotations.OfType<HealthCheckAnnotation>(), a => a.Key.Contains("/healthz")));
        Assert.Empty(Warnings(builder));
    }

    [Fact]
    public void WithHealthCheck_OnKubernetes_WithUserKey_Applies()
    {
        var builder = Builder();
        builder.Services.AddHealthChecks().AddCheck("mine", () => HealthCheckResult.Healthy());
        var service = Kubernetes(builder);

        service.WithHealthCheck("mine");

        var onReal = Assert.Single(RealAnnotations(service).OfType<HealthCheckAnnotation>(), a => a.Key == "mine");
        Assert.Same(
            onReal,
            Assert.Single(service.Resource.Annotations.OfType<HealthCheckAnnotation>(), a => a.Key == "mine"));
        Assert.Empty(Warnings(builder));
    }

    [Fact]
    public void WithHealthCheck_ReusingTheTunnelKey_ThrowsAndLeavesTheTunnelCheckInPlace()
    {
        var builder = Builder();
        var service = Kubernetes(builder);
        var key = KubernetesSource.HealthCheckKey("orders");

        Assert.Throws<DistributedApplicationException>(() => service.WithHealthCheck(key));

        Assert.Single(RealAnnotations(service).OfType<HealthCheckAnnotation>(), a => a.Key == key);
    }

    [Fact]
    public void WithHttpHealthCheck_OnUrl_IsSkipped_NamedByTheMethod()
    {
        var builder = Builder();
        var service = new UrlSource().Resolve(
            builder, "inventory", UrlDefinition, new ServiceDeveloperConfig { Source = "url" });

        service.WithHttpHealthCheck("/healthz");

        Assert.Contains(Skipped, Assert.Single(Warnings(builder)));
        Assert.Empty(service.Resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Fact]
    public void HealthCheck_OnDisabledAndPlaceholder_IsSkipped()
    {
        var disabledBuilder = Builder();
        var disabled = new DisabledSource().Resolve(
            disabledBuilder, "billing", BareDefinition, new ServiceDeveloperConfig { Source = "disabled" });
        disabled.WithHealthCheck("k");

        Assert.Contains(Skipped, Assert.Single(Warnings(disabledBuilder)));
        Assert.Empty(disabled.Resource.Annotations.OfType<HealthCheckAnnotation>());

        var placeholderBuilder = Builder();
        var placeholder = UnconfiguredSource.Resolve(placeholderBuilder, "billing", BareDefinition);
        placeholder.WithHealthCheck("k");

        Assert.Contains(Skipped, Assert.Single(Warnings(placeholderBuilder)));
        Assert.Empty(placeholder.Resource.Annotations.OfType<HealthCheckAnnotation>());
    }

    [Fact]
    public void ASubclassOfHealthCheckAnnotation_AddedToAKubernetesService_IsSkipped()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        service.WithAnnotation(new DerivedHealthCheck());

        Assert.NotEmpty(Warnings(builder));
        Assert.Empty(RealAnnotations(service).OfType<DerivedHealthCheck>());
    }

    [Fact]
    public void AHealthCheckAnnotationAddedDirectlyToTheFacade_OnKubernetes_IsForwardedToTheReal()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        ServiceResourceBuilder.ForwardingAnnotationsAddedBy(service, () =>
        {
            service.Resource.Annotations.Add(new HealthCheckAnnotation("k"));
            return service;
        });

        Assert.Single(RealAnnotations(service).OfType<HealthCheckAnnotation>(), a => a.Key == "k");
        Assert.Empty(Warnings(builder));
    }

    [Fact]
    public void WithHttpCommand_OnKubernetes_StillSkips()
    {
        var builder = Builder();
        var service = Kubernetes(builder);

        OverloadProbe.CallWithCommand(
            service, "restart", "Restart", _ => Task.FromResult(new ExecuteCommandResult { Success = true }));

        Assert.Contains(nameof(ResourceCommandAnnotation), Assert.Single(Warnings(builder)));
        Assert.Empty(RealAnnotations(service).OfType<ResourceCommandAnnotation>());
    }

    [Fact]
    public async Task TheFacadeBoundEndpointReference_ResolvesToTheAllocatedUrl()
    {
        var builder = Builder();
        var service = Kubernetes(builder);
        var endpoint = Assert.Single(RealAnnotations(service).OfType<EndpointAnnotation>());
        endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "localhost", 51234);

        var url = await service.GetEndpoint(endpoint.Name).GetValueAsync();

        Assert.Equal($"{endpoint.UriScheme}://localhost:51234", url);
    }
}
