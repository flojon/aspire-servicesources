# ServiceSources Cluster Source Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `"cluster"` `IServiceSource` that resolves `AddService()` against a service already running in a Kubernetes dev cluster, via `kubectl port-forward`.

**Architecture:** New `ClusterSource : IServiceSource` allocates a free local port via a new `IPortAllocator` seam, builds a `kubectl port-forward` argument list, and calls `builder.AddExecutable(...)` + `WithHttpEndpoint(...)` — mirroring how `LocalProjectSource` delegates to Aspire's own `AddProject`. The existing `ServiceResource.CreateFacade<TResource>` (already generalized to accept any `IResourceBuilder<TResource>`) wraps the returned `IResourceBuilder<ExecutableResource>` exactly as it wraps `IResourceBuilder<ProjectResource>` today. Config gains a `cluster` block in the catalog (`service`, `port`) and `context`/`namespace`/`port` fields in developer config.

**Tech Stack:** C# / .NET 10 (TargetFramework `net10.0`), Aspire.Hosting 13.4.6, xUnit, YamlDotNet, System.Text.Json. No new NuGet packages — port allocation uses `System.Net.Sockets` from the BCL.

**Spec:** `docs/superpowers/specs/2026-08-13-servicesources-cluster-source-design.md`

## Global Constraints

- v1 assumes HTTP only — no protocol/scheme override (spec "Out of Scope").
- No label-selector/pod targeting — Service name + port only.
- No `kubeconfigPath` config — ambient `kubectl` config only (`--context`/`--namespace` flags only).
- No auto/fallback source selection referencing `"cluster"` — that's a separate phase-2 item; `Sources` dictionary lookup stays explicit.
- No retry/reconnect logic for a dropped port-forward — left entirely to Aspire's executable-resource restart behavior.
- No automated cluster/integration test — config-parsing and orchestration/argument-building are unit-tested only, per spec's Testing section. Manual smoke test via the demo AppHost is out of scope for this plan (follow-up, not a task here).
- Runtime errors (`kubectl` missing, bad context, Service not found, dropped forward) are **not** thrown at `AddService()` time — they must surface only through the `ExecutableResource`'s own state/logs, which falls out naturally from delegating to `AddExecutable` and must not be special-cased.
- Config errors (missing `cluster.service`, missing `context`, `port` resolvable from neither place) **are** thrown at `AddService()` time as `ServiceSourcesConfigurationException`, naming the service and the missing field — same philosophy as milestone 1a.

---

## File Structure

- `src/Aspire.Hosting.ServiceSources/Config/ClusterMetadata.cs` — new. Catalog-only `{ Service, Port }` shape, nested under `ServiceMetadata.Cluster`.
- `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs` — modify. Add `Cluster` property.
- `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs` — modify. Add `Context`, `Namespace`, `Port` properties.
- `src/Aspire.Hosting.ServiceSources/PortAllocation/IPortAllocator.cs` — new. Single-method seam (same shape as `IGitClient`).
- `src/Aspire.Hosting.ServiceSources/PortAllocation/SocketPortAllocator.cs` — new. Real bind-`:0`-and-release implementation.
- `src/Aspire.Hosting.ServiceSources/Sources/ClusterSource.cs` — new. `IServiceSource` implementation; static `BuildPortForwardArgs` for unit-testable arg-building/precedence logic (mirrors `LocalProjectSource.ResolveProjectPath`'s static-method-for-testability pattern).
- `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs` — modify. Register `"cluster"` in the `Sources` dictionary.
- `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs` — modify. Add a test for the `cluster` YAML block.
- `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs` — modify. Add a test for `context`/`namespace`/`port` JSON fields.
- `test/Aspire.Hosting.ServiceSources.Tests/PortAllocation/SocketPortAllocatorTests.cs` — new. Real-socket test that two allocations don't collide and the returned port is bindable.
- `test/Aspire.Hosting.ServiceSources.Tests/Sources/ClusterSourceTests.cs` — new. Fake `IPortAllocator`-driven unit tests for arg-building, precedence, and fail-fast paths.
- `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs` — modify. `AddService_UnknownSource_ThrowsNamingServiceAndSource` currently uses `"cluster"` as its example of an unregistered source; once `"cluster"` is registered this test's premise breaks, so it must be repointed at a still-unregistered source name.

---

## Task 1: Config schema — `cluster` catalog block and developer-config fields

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/ClusterMetadata.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs`

**Interfaces:**
- Produces: `ServiceMetadata.Cluster` of type `ClusterMetadata?` (null when the catalog entry has no `cluster` block).
- Produces: `ClusterMetadata { string Service, int? Port }` — `Service` is required at the YAML level conceptually but the deserializer leaves it `""` by default like other `ServiceMetadata` string fields (matches existing `Repository`/`Project` pattern — no deserializer-level required-field enforcement anywhere in this codebase, validation happens in `ClusterSource`).
- Produces: `ServiceDeveloperConfig.Context` (`string?`), `ServiceDeveloperConfig.Namespace` (`string?`), `ServiceDeveloperConfig.Port` (`int?`).

- [ ] **Step 1: Write the failing test for catalog `cluster` block parsing**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`:

```csharp
    [Fact]
    public void Load_ParsesClusterBlockFromYaml()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                cluster:
                  service: orders-svc
                  port: 8080
            """);

        try
        {
            var catalog = ServiceCatalogLoader.Load(path);

            var orders = Assert.Single(catalog.Services);
            Assert.NotNull(orders.Value.Cluster);
            Assert.Equal("orders-svc", orders.Value.Cluster.Service);
            Assert.Equal(8080, orders.Value.Cluster.Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_NoClusterBlock_LeavesClusterNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
            """);

        try
        {
            var catalog = ServiceCatalogLoader.Load(path);

            Assert.Null(catalog.Services["orders"].Cluster);
        }
        finally
        {
            File.Delete(path);
        }
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceCatalogLoaderTests"`
Expected: FAIL — `ServiceMetadata` has no `Cluster` member (compile error).

- [ ] **Step 3: Write the failing test for developer-config `context`/`namespace`/`port` parsing**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs`:

```csharp
    [Fact]
    public void Load_ParsesClusterFieldsFromJson()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {
              "services": {
                "orders": { "source": "cluster", "context": "dev-west", "namespace": "orders", "port": 8080 }
              }
            }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Equal("cluster", config.Services["orders"].Source);
            Assert.Equal("dev-west", config.Services["orders"].Context);
            Assert.Equal("orders", config.Services["orders"].Namespace);
            Assert.Equal(8080, config.Services["orders"].Port);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_ClusterFieldsOmitted_LeavesThemNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            { "services": { "orders": { "source": "local" } } }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Null(config.Services["orders"].Context);
            Assert.Null(config.Services["orders"].Namespace);
            Assert.Null(config.Services["orders"].Port);
        }
        finally
        {
            File.Delete(path);
        }
    }
```

- [ ] **Step 4: Run the new tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~DeveloperConfigLoaderTests"`
Expected: FAIL — `ServiceDeveloperConfig` has no `Context`/`Namespace`/`Port` members (compile error).

- [ ] **Step 5: Create `ClusterMetadata` and wire it into `ServiceMetadata`**

Create `src/Aspire.Hosting.ServiceSources/Config/ClusterMetadata.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ClusterMetadata
{
    public string Service { get; set; } = "";

    public int? Port { get; set; }
}
```

Modify `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceMetadata
{
    public string Repository { get; set; } = "";

    public string Project { get; set; } = "";

    public string? DefaultRef { get; set; }

    public ClusterMetadata? Cluster { get; set; }
}
```

- [ ] **Step 6: Add `Context`, `Namespace`, `Port` to `ServiceDeveloperConfig`**

Modify `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceDeveloperConfig
{
    public string Source { get; set; } = "";

    public string? Path { get; set; }

    public string? Ref { get; set; }

    public string? Context { get; set; }

    public string? Namespace { get; set; }

    public int? Port { get; set; }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceCatalogLoaderTests|FullyQualifiedName~DeveloperConfigLoaderTests"`
Expected: PASS (all tests in both files, including pre-existing ones)

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Config/ClusterMetadata.cs \
        src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs \
        src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs
git commit -m "Add cluster config schema (catalog cluster block, dev-config context/namespace/port)"
```

---

## Task 2: `IPortAllocator` seam and real socket-based implementation

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/PortAllocation/IPortAllocator.cs`
- Create: `src/Aspire.Hosting.ServiceSources/PortAllocation/SocketPortAllocator.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/PortAllocation/SocketPortAllocatorTests.cs`

**Interfaces:**
- Produces: `IPortAllocator.AllocatePort() : int` — binds an ephemeral local TCP port, reads the assigned port, releases the socket, and returns it. Task 3 (`ClusterSource`) consumes this exact signature via constructor injection, matching how `LocalProjectSource(IGitClient gitClient)` consumes `IGitClient`.

- [ ] **Step 1: Write the failing test**

Create `test/Aspire.Hosting.ServiceSources.Tests/PortAllocation/SocketPortAllocatorTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Aspire.Hosting.ServiceSources.PortAllocation;

namespace Aspire.Hosting.ServiceSources.Tests.PortAllocation;

public class SocketPortAllocatorTests
{
    [Fact]
    public void AllocatePort_ReturnsPortInValidRangeAndBindable()
    {
        var allocator = new SocketPortAllocator();

        var port = allocator.AllocatePort();

        Assert.InRange(port, 1, 65535);

        // The allocator releases its own socket before returning, so the port must be
        // immediately bindable again (modulo the TOCTOU race the design doc accepts).
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
    }

    [Fact]
    public void AllocatePort_CalledTwice_ReturnsDifferentPorts()
    {
        var allocator = new SocketPortAllocator();

        var first = allocator.AllocatePort();
        var second = allocator.AllocatePort();

        Assert.NotEqual(first, second);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~SocketPortAllocatorTests"`
Expected: FAIL — `Aspire.Hosting.ServiceSources.PortAllocation` namespace / `SocketPortAllocator` type does not exist (compile error).

- [ ] **Step 3: Write the interface and implementation**

Create `src/Aspire.Hosting.ServiceSources/PortAllocation/IPortAllocator.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.PortAllocation;

internal interface IPortAllocator
{
    /// <summary>
    /// Allocates a free local TCP port by binding an ephemeral socket, reading the OS-assigned
    /// port, and releasing the socket immediately. There is an inherent TOCTOU race between this
    /// release and whatever later binds the returned port (e.g. <c>kubectl port-forward</c>) —
    /// accepted per the cluster-source design doc.
    /// </summary>
    int AllocatePort();
}
```

Create `src/Aspire.Hosting.ServiceSources/PortAllocation/SocketPortAllocator.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace Aspire.Hosting.ServiceSources.PortAllocation;

internal sealed class SocketPortAllocator : IPortAllocator
{
    public int AllocatePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~SocketPortAllocatorTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/PortAllocation/IPortAllocator.cs \
        src/Aspire.Hosting.ServiceSources/PortAllocation/SocketPortAllocator.cs \
        test/Aspire.Hosting.ServiceSources.Tests/PortAllocation/SocketPortAllocatorTests.cs
git commit -m "Add IPortAllocator seam with a real socket-based implementation"
```

---

## Task 3: `ClusterSource` — argument-building, precedence, and fail-fast config validation

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Sources/ClusterSource.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/ClusterSourceTests.cs`

**Interfaces:**
- Consumes: `IPortAllocator.AllocatePort() : int` (Task 2).
- Consumes: `ServiceMetadata.Cluster : ClusterMetadata?`, `ClusterMetadata.Service : string`, `ClusterMetadata.Port : int?` (Task 1).
- Consumes: `ServiceDeveloperConfig.Context : string?`, `.Namespace : string?`, `.Port : int?` (Task 1).
- Consumes: `ServiceSourcesConfigurationException(string message)` (existing).
- Produces: `internal static string[] ClusterSource.BuildPortForwardArgs(string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config, IPortAllocator portAllocator, out int localPort, out int remotePort)` — the testable core, mirroring `LocalProjectSource.ResolveProjectPath`'s "static method taking all inputs explicitly" pattern. Returns the full `kubectl` argument array in order: `["port-forward", "svc/<service>", "<local>:<remote>", "--context", "<context>", "--namespace", "<namespace>"]`.
- Produces: `internal sealed class ClusterSource(IPortAllocator portAllocator) : IServiceSource` with `Resolve(...)` matching the `IServiceSource` interface exactly (same signature as `LocalProjectSource.Resolve`). Task 4 registers this type; Task 5's `AddServiceTests`/integration tests exercise `Resolve` end to end.

- [ ] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.Tests/Sources/ClusterSourceTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ClusterSourceTests"`
Expected: FAIL — `ClusterSource` type does not exist (compile error).

- [ ] **Step 3: Write `ClusterSource`**

Create `src/Aspire.Hosting.ServiceSources/Sources/ClusterSource.cs`:

```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.PortAllocation;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class ClusterSource(IPortAllocator portAllocator) : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var args = BuildPortForwardArgs(serviceName, metadata, config, portAllocator, out var localPort, out var remotePort);

        var executableBuilder = builder
            .AddExecutable($"{serviceName}-portforward", "kubectl", builder.AppHostDirectory, args)
            .WithHttpEndpoint(port: localPort, targetPort: remotePort);

        return ServiceResource.CreateFacade(builder, serviceName, executableBuilder);
    }

    internal static string[] BuildPortForwardArgs(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        IPortAllocator portAllocator,
        out int localPort,
        out int remotePort)
    {
        if (metadata.Cluster is null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' source is 'cluster' but servicesources.yaml has no cluster.service entry.");
        }

        if (config.Context is null)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': source 'cluster' requires 'context' in servicesources.local.json.");
        }

        remotePort = config.Port ?? metadata.Cluster.Port ?? throw new ServiceSourcesConfigurationException(
            $"Service '{serviceName}': no 'port' configured for source 'cluster' — set it in " +
            "servicesources.local.json or servicesources.yaml's cluster.port.");

        var @namespace = config.Namespace ?? "default";

        localPort = portAllocator.AllocatePort();

        return
        [
            "port-forward",
            $"svc/{metadata.Cluster.Service}",
            $"{localPort}:{remotePort}",
            "--context",
            config.Context,
            "--namespace",
            @namespace,
        ];
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ClusterSourceTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/ClusterSource.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Sources/ClusterSourceTests.cs
git commit -m "Add ClusterSource argument-building and fail-fast config validation"
```

---

## Task 4: Wire `ClusterSource` into `AddService()`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`
- Test (new, same file): `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`

**Interfaces:**
- Consumes: `ClusterSource(IPortAllocator)` (Task 3), `SocketPortAllocator` (Task 2).
- Consumes: `ServiceResource.CreateFacade<TResource>` (already generalized — no change needed).

- [ ] **Step 1: Fix the now-invalid `AddService_UnknownSource` test premise**

`AddService_UnknownSource_ThrowsNamingServiceAndSource` in `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs` currently uses `"cluster"` as an example of a source that isn't implemented. Once `"cluster"` is registered (this task), that test's premise is false and it will start failing for the wrong reason (or silently start exercising `ClusterSource` instead of the unknown-source path). Update it to use a source name that stays unregistered:

```csharp
    [Fact]
    public void AddService_UnknownSource_ThrowsNamingServiceAndSource()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "docker" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("docker", ex.Message);
    }
```

- [ ] **Step 2: Write the failing test for `AddService` dispatching to `ClusterSource`**

Add to `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`:

```csharp
    [Fact]
    public void AddService_ClusterSource_AddsPortForwardExecutableAndReturnsFacade()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
                cluster:
                  service: orders-svc
                  port: 8080
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "cluster", "context": "dev-west", "namespace": "orders-ns" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        Assert.Contains(builder.Resources, r => r.Name == "orders-portforward");
        Assert.DoesNotContain(builder.Resources, r => ReferenceEquals(r, service.Resource));

        var endpoint = service.GetEndpoint("http");
        Assert.Equal("http", endpoint.EndpointName);
    }

    [Fact]
    public void AddService_ClusterSourceMissingContext_ThrowsNamingServiceAndContext()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
                cluster:
                  service: orders-svc
                  port: 8080
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "cluster" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("context", ex.Message);
    }
```

Add `using Aspire.Hosting.ApplicationModel;` to the top of the file if not already present (needed for `GetEndpoint`).

- [ ] **Step 3: Run the tests to verify the new ones fail and the renamed one still passes**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~AddServiceTests"`
Expected: `AddService_UnknownSource_ThrowsNamingServiceAndSource` PASS (it doesn't depend on `ClusterSource` existing). `AddService_ClusterSource_AddsPortForwardExecutableAndReturnsFacade` and `AddService_ClusterSourceMissingContext_ThrowsNamingServiceAndContext` FAIL with `ServiceSourcesConfigurationException: Service 'orders' has source 'cluster', which is not implemented yet.`

- [ ] **Step 4: Register `ClusterSource` in `Sources`**

Modify `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`:

```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.PortAllocation;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources;

public static class ServiceSourcesBuilderExtensions
{
    private static readonly Dictionary<string, IServiceSource> Sources = new()
    {
        ["local"] = new LocalProjectSource(new LibGit2SharpGitClient()),
        ["cluster"] = new ClusterSource(new SocketPortAllocator()),
    };

    // ... rest of the file unchanged ...
}
```

(Only the `using` list and the `Sources` dictionary initializer change — the `AddService` method body is untouched.)

- [ ] **Step 5: Run the full test suite to verify everything passes**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests`
Expected: PASS — all tests, including the untouched `LocalProjectSourceTests`, `ServiceResourceTests`, and `AddServiceIntegrationTests`.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs \
        test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs
git commit -m "Register ClusterSource under 'cluster' in AddService()"
```

---

## Self-Review Notes

- **Spec coverage:** Architecture (Tasks 2–4), Config Schema (Task 1), Resolution Flow steps 1–7 (Task 3's `BuildPortForwardArgs` + `Resolve`, Task 4's dispatch), Error Handling config-vs-runtime split (Task 3 throws only on config gaps; runtime errors are never touched because `Resolve` delegates to `AddExecutable` and does nothing else), Testing section (Tasks 1–3 cover config parsing and orchestration/precedence via fake allocator; no integration test added, matching "No automated cluster/integration test in v1"). Out-of-scope items (protocol override, pod selectors, kubeconfigPath, auto-source-selection, retry/reconnect) are intentionally absent from every task.
- **Placeholder scan:** No TBD/TODO markers; every step has literal code.
- **Type consistency:** `BuildPortForwardArgs` signature is identical everywhere it's referenced (Task 3 defines it, Task 3's own tests call it — Task 4 doesn't call it directly, only `Resolve`). `ClusterMetadata`, `ServiceDeveloperConfig.Context/Namespace/Port`, and `IPortAllocator.AllocatePort()` are defined once each (Task 1, Task 2) and consumed with matching names/types in Task 3.
