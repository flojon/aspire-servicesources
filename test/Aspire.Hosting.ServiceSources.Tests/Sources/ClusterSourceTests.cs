using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.PortAllocation;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class ClusterSourceTests
{
    private sealed class FakePortAllocator(int port) : IPortAllocator
    {
        public int AllocatePort() => port;
    }

    private const string ServiceName = "orders";

    private static ServiceMetadata Metadata(string? clusterService = "orders-svc", int? clusterPort = 8080) =>
        new()
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Cluster = clusterService is null ? null : new ClusterMetadata { Service = clusterService, Port = clusterPort },
        };

    private static ServiceDeveloperConfig DevConfig(string? context = "dev-west", string? @namespace = null, int? port = null) =>
        new() { Source = "cluster", Context = context, Namespace = @namespace, Port = port };

    [Fact]
    public void BuildPortForwardArgs_AllFieldsSet_BuildsArgsInOrder()
    {
        var args = ClusterSource.BuildPortForwardArgs(
            ServiceName, Metadata(clusterService: "orders-svc", clusterPort: 8080),
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
        var args = ClusterSource.BuildPortForwardArgs(
            ServiceName, Metadata(), DevConfig(@namespace: null),
            new FakePortAllocator(1), out _, out _);

        Assert.Contains("--namespace", args);
        Assert.Equal("default", args[Array.IndexOf(args, "--namespace") + 1]);
    }

    [Fact]
    public void BuildPortForwardArgs_LocalPortOverride_TakesPrecedenceOverCatalogPort()
    {
        ClusterSource.BuildPortForwardArgs(
            ServiceName, Metadata(clusterPort: 8080), DevConfig(port: 9090),
            new FakePortAllocator(1), out _, out var remotePort);

        Assert.Equal(9090, remotePort);
    }

    [Fact]
    public void BuildPortForwardArgs_LocalPortUnset_FallsBackToCatalogPort()
    {
        ClusterSource.BuildPortForwardArgs(
            ServiceName, Metadata(clusterPort: 8080), DevConfig(port: null),
            new FakePortAllocator(1), out _, out var remotePort);

        Assert.Equal(8080, remotePort);
    }

    [Fact]
    public void BuildPortForwardArgs_NoClusterBlock_ThrowsNamingServiceAndClusterService()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ClusterSource.BuildPortForwardArgs(
                ServiceName, Metadata(clusterService: null), DevConfig(),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("cluster.service", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_MissingContext_ThrowsNamingServiceAndContext()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ClusterSource.BuildPortForwardArgs(
                ServiceName, Metadata(), DevConfig(context: null),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("context", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_EmptyClusterService_ThrowsNamingServiceAndClusterService()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ClusterSource.BuildPortForwardArgs(
                ServiceName, Metadata(clusterService: ""), DevConfig(),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("cluster.service", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_EmptyContext_ThrowsNamingServiceAndContext()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ClusterSource.BuildPortForwardArgs(
                ServiceName, Metadata(), DevConfig(context: ""),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("context", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_PortMissingFromBothPlaces_ThrowsNamingServiceAndPort()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ClusterSource.BuildPortForwardArgs(
                ServiceName, Metadata(clusterPort: null), DevConfig(port: null),
                new FakePortAllocator(1), out _, out _));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("port", ex.Message);
    }

    [Fact]
    public void BuildPortForwardArgs_ValidConfig_DoesNotAllocatePortWhenValidationFailsFirst()
    {
        // Fail-fast config errors must be raised before any port allocation occurs — no point
        // burning an ephemeral port for a call that's going to throw.
        var allocatorCalled = false;
        var allocator = new TrackingPortAllocator(() => allocatorCalled = true, 1);

        Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ClusterSource.BuildPortForwardArgs(
                ServiceName, Metadata(clusterService: null), DevConfig(),
                allocator, out _, out _));

        Assert.False(allocatorCalled);
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
