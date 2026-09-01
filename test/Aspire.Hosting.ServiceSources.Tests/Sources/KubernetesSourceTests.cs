using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using IPortAllocator = Aspire.Hosting.ServiceSources.PortAllocation.IPortAllocator;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class KubernetesSourceTests
{
    private sealed class FakePortAllocator(int port) : IPortAllocator
    {
        public int AllocatePort() => port;
    }

    private const string ServiceName = "orders";

    private static ServiceMetadata Metadata(
        string? kubernetesService = "orders-svc", int? kubernetesPort = 8080, string? scheme = null) =>
        new()
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Kubernetes = kubernetesService is null
                ? null
                : new KubernetesMetadata { Service = kubernetesService, Port = kubernetesPort, Scheme = scheme },
        };

    private static ServiceDeveloperConfig DevConfig(
        string? context = "dev-west", string? @namespace = null, int? port = null, string? scheme = null) =>
        new() { Source = "kubernetes", Context = context, Namespace = @namespace, Port = port, Scheme = scheme };

    [Fact]
    public void BuildPortForwardArgs_AllFieldsSet_BuildsArgsInOrder()
    {
        var args = KubernetesSource.BuildPortForwardArgs(
            ServiceName, Metadata(kubernetesService: "orders-svc", kubernetesPort: 8080),
            DevConfig(context: "dev-west", @namespace: "orders", port: null),
            new FakePortAllocator(54321), out var localPort, out var remotePort);

        Assert.Equal(54321, localPort);
        Assert.Equal(8080, remotePort);
        Assert.Equal(
            ["port-forward", "svc/orders-svc", "54321:8080", "--context", "dev-west", "--namespace", "orders"],
            args);
    }

    [Fact]
    public void BuildPortForwardArgs_NamespaceOmitted_DefaultsToDefaultNamespace()
    {
        var args = KubernetesSource.BuildPortForwardArgs(
            ServiceName, Metadata(), DevConfig(@namespace: null),
            new FakePortAllocator(1), out _, out _);

        Assert.Contains("--namespace", args);
        Assert.Equal("default", args[Array.IndexOf(args, "--namespace") + 1]);
    }

    [Fact]
    public void BuildPortForwardArgs_LocalPortOverride_TakesPrecedenceOverCatalogPort()
    {
        KubernetesSource.BuildPortForwardArgs(
            ServiceName, Metadata(kubernetesPort: 8080), DevConfig(port: 9090),
            new FakePortAllocator(1), out _, out var remotePort);

        Assert.Equal(9090, remotePort);
    }

    [Fact]
    public void BuildPortForwardArgs_LocalPortUnset_FallsBackToCatalogPort()
    {
        KubernetesSource.BuildPortForwardArgs(
            ServiceName, Metadata(kubernetesPort: 8080), DevConfig(port: null),
            new FakePortAllocator(1), out _, out var remotePort);

        Assert.Equal(8080, remotePort);
    }

    [Fact]
    public void BuildPortForwardArgs_NoKubernetesBlock_ThrowsNamingServiceAndKubernetesService()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            KubernetesSource.BuildPortForwardArgs(
                ServiceName, Metadata(kubernetesService: null), DevConfig(),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("kubernetes.service", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_MissingContext_ThrowsNamingServiceAndContext()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            KubernetesSource.BuildPortForwardArgs(
                ServiceName, Metadata(), DevConfig(context: null),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("context", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_EmptyKubernetesService_ThrowsNamingServiceAndKubernetesService()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            KubernetesSource.BuildPortForwardArgs(
                ServiceName, Metadata(kubernetesService: ""), DevConfig(),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("kubernetes.service", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_EmptyContext_ThrowsNamingServiceAndContext()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            KubernetesSource.BuildPortForwardArgs(
                ServiceName, Metadata(), DevConfig(context: ""),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("context", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_PortMissingFromBothPlaces_ThrowsNamingServiceAndPort()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            KubernetesSource.BuildPortForwardArgs(
                ServiceName, Metadata(kubernetesPort: null), DevConfig(port: null),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("port", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_ZeroPort_ThrowsNamingServiceAndPort()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            KubernetesSource.BuildPortForwardArgs(
                ServiceName, Metadata(kubernetesPort: 0), DevConfig(port: null),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("port", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_PortAboveValidRange_ThrowsNamingServiceAndPort()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            KubernetesSource.BuildPortForwardArgs(
                ServiceName, Metadata(kubernetesPort: 8080), DevConfig(port: 70000),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("port", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_PortOutOfRange_DoesNotAllocatePortWhenValidationFailsFirst()
    {
        var allocatorCalled = false;
        var allocator = new TrackingPortAllocator(() => allocatorCalled = true, 1);

        Assert.Throws<ServiceSourcesConfigurationException>(() =>
            KubernetesSource.BuildPortForwardArgs(
                ServiceName, Metadata(kubernetesPort: 70000), DevConfig(),
                allocator, out _, out _));

        Assert.False(allocatorCalled);
    }

    [Fact]
    public void BuildPortForwardArgs_ValidConfig_DoesNotAllocatePortWhenValidationFailsFirst()
    {
        // Fail-fast config errors must be raised before any port allocation occurs — no point
        // burning an ephemeral port for a call that's going to throw.
        var allocatorCalled = false;
        var allocator = new TrackingPortAllocator(() => allocatorCalled = true, 1);

        Assert.Throws<ServiceSourcesConfigurationException>(() =>
            KubernetesSource.BuildPortForwardArgs(
                ServiceName, Metadata(kubernetesService: null), DevConfig(),
                allocator, out _, out _));

        Assert.False(allocatorCalled);
    }

    [Fact]
    public void Resolve_NoScheme_NamesTheEndpointHttp()
    {
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

        var service = new KubernetesSource(new FakePortAllocator(54321))
            .Resolve(builder, ServiceName, Metadata(), DevConfig());

        var endpoint = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("http", endpoint.Name);
        Assert.Equal("http", endpoint.UriScheme);
    }

    [Fact]
    public void Resolve_HttpsScheme_NamesTheEndpointHttpsSoConsumersCanAskForItByScheme()
    {
        // A kubectl port-forward is a byte-transparent TCP tunnel, so TLS terminates at the pod and
        // https://localhost:<localPort> genuinely works. Naming the endpoint "http" regardless left
        // consumers with an http:// URL that a TLS listener rejects (#160).
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

        var service = new KubernetesSource(new FakePortAllocator(54321))
            .Resolve(builder, ServiceName, Metadata(scheme: "https"), DevConfig());

        var endpoint = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("https", endpoint.Name);
        Assert.Equal("https", endpoint.UriScheme);
        Assert.Equal("https", service.GetEndpoint("https").EndpointName);
    }

    [Fact]
    public void Resolve_DeveloperScheme_TakesPrecedenceOverCatalogScheme()
    {
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

        var service = new KubernetesSource(new FakePortAllocator(54321))
            .Resolve(builder, ServiceName, Metadata(scheme: "http"), DevConfig(scheme: "https"));

        var endpoint = Assert.Single(service.Resource.Annotations.OfType<EndpointAnnotation>());
        Assert.Equal("https", endpoint.Name);
    }

    [Fact]
    public void Resolve_UnsupportedScheme_ThrowsNamingServiceAndScheme()
    {
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new KubernetesSource(new FakePortAllocator(54321))
                .Resolve(builder, ServiceName, Metadata(scheme: "grpc"), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("grpc", ex.Message);
    }

    [Fact]
    public void Resolve_UnsupportedScheme_DoesNotAllocatePort()
    {
        // The scheme is config validation like every check inside BuildPortForwardArgs, so it is
        // resolved before that call reaches its port allocation rather than after it.
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);
        var allocatorCalled = false;
        var allocator = new TrackingPortAllocator(() => allocatorCalled = true, 54321);

        Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new KubernetesSource(allocator).Resolve(builder, ServiceName, Metadata(scheme: "grpc"), DevConfig()));

        Assert.False(allocatorCalled);
    }

    [Fact]
    public void Resolve_MissingKubernetesBlock_ReportsThatRatherThanTheScheme()
    {
        // Scheme resolution must not run ahead of the checks that name a missing kubernetes block:
        // the block is the more fundamental problem and the one worth reporting.
        var builder = TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            new KubernetesSource(new FakePortAllocator(54321))
                .Resolve(builder, ServiceName, Metadata(kubernetesService: null, scheme: "grpc"), DevConfig()));

        Assert.Contains("kubernetes.service", ex.Message);
    }

    private sealed class TrackingPortAllocator(Action onAllocate, int port) : IPortAllocator
    {
        public int AllocatePort()
        {
            onAllocate();
            return port;
        }
    }
}
