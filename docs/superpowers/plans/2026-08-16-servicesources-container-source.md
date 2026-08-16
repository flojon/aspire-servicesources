# ServiceSources Container Source Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `"container"` `IServiceSource` that resolves `AddService()` against a published container image via Aspire's own `AddContainer()`.

**Architecture:** New `ContainerSource : IServiceSource` resolves `image`/`tag`/`port` from catalog + local-config, then calls `builder.AddContainer(name, image, tag)` (or the 2-arg overload when no tag resolves) and `WithHttpEndpoint(targetPort: port)` — no host `port:`, so Aspire/DCP auto-assigns and proxies it (no `IPortAllocator` needed, unlike `ClusterSource`). The existing `ServiceResource.CreateFacade<TResource>` wraps the returned `IResourceBuilder<ContainerResource>` exactly as it wraps `IResourceBuilder<ProjectResource>`/`IResourceBuilder<ExecutableResource>` today — no change needed there. Config gains a `container` block in the catalog (`image`, `port`, `defaultTag`) and a `tag` field in developer config.

**Tech Stack:** C# / .NET 10 (TargetFramework `net10.0`), Aspire.Hosting 13.4.6, xUnit, YamlDotNet, System.Text.Json. No new NuGet packages.

**Spec:** `docs/superpowers/specs/2026-08-15-servicesources-container-source-design.md`

## Global Constraints

- v1 assumes HTTP only — `WithHttpEndpoint`, no protocol/scheme override (spec "Out of Scope").
- No registry-auth config surface — ambient Docker/Podman credentials only.
- No image digest pinning (`WithImageSHA256`) — tag-based resolution only.
- No `WithImagePullPolicy` / `WithLifetime` overrides — Aspire defaults apply.
- No env var / dependency wiring into the container — consistent with `LocalProjectSource`/`ClusterSource`.
- No auto/fallback source selection referencing `"container"` — separate phase-2 item; `Sources` dictionary lookup stays explicit.
- No local-config override for `port` — it's a fixed property of the image, catalog-only (unlike the cluster source's remote port).
- No `IPortAllocator` — `WithHttpEndpoint(targetPort:)` with no `port:` lets Aspire/DCP assign and proxy the host port itself.
- Runtime errors (no container runtime, image not found, bad tag, registry auth failure, container exits immediately) are **not** thrown at `AddService()` time — they must surface only through the `ContainerResource`'s own state/logs, which falls out naturally from delegating to `AddContainer` and must not be special-cased.
- Config errors (missing/whitespace-only `container.image`, missing `container.port`) **are** thrown at `AddService()` time as `ServiceSourcesConfigurationException`, naming the service and the missing field — same philosophy as the other two sources.
- No automated container/integration test — config-parsing and orchestration/argument-resolution are unit-tested only, per spec's Testing section. Manual smoke test via the demo AppHost is out of scope for this plan.

---

## File Structure

- `src/Aspire.Hosting.ServiceSources/Config/ContainerMetadata.cs` — new. Catalog-only `{ Image, Port, DefaultTag }` shape, nested under `ServiceMetadata.Container`.
- `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs` — modify. Add `Container` property.
- `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs` — modify. Add `Tag` property.
- `src/Aspire.Hosting.ServiceSources/Sources/ContainerSource.cs` — new. `IServiceSource` implementation; static `ResolveContainerConfig` for unit-testable image/tag/port resolution and fail-fast validation (mirrors `ClusterSource.BuildPortForwardArgs`'s "static method taking all inputs explicitly" pattern).
- `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs` — modify. Register `"container"` in the `Sources` dictionary; extend the XML doc summary to mention the new source.
- `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs` — modify. Add tests for the `container` YAML block.
- `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs` — modify. Add a test for the `tag` JSON field.
- `test/Aspire.Hosting.ServiceSources.Tests/Sources/ContainerSourceTests.cs` — new. Unit tests for image/tag/port resolution, precedence, and fail-fast paths.
- `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs` — modify. Add end-to-end `AddService` tests dispatching to `ContainerSource` (success path + a fail-fast path), matching the existing `AddService_ClusterSource_*`/`AddService_UrlSource_*` tests' shape.

---

## Task 1: Config schema — `container` catalog block and developer-config `tag` field

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/ContainerMetadata.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs`

**Interfaces:**
- Produces: `ServiceMetadata.Container` of type `ContainerMetadata?` (null when the catalog entry has no `container` block).
- Produces: `ContainerMetadata { string Image, int? Port, string? DefaultTag }` — `Image` defaults to `""` like `ServiceMetadata.Repository`/`.Project` (no deserializer-level required-field enforcement anywhere in this codebase; validation happens in `ContainerSource`, Task 2).
- Produces: `ServiceDeveloperConfig.Tag` (`string?`).

- [ ] **Step 1: Write the failing tests for catalog `container` block parsing**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs` (inside the existing `ServiceCatalogLoaderTests` class, after the existing `Load_NoClusterBlock_LeavesClusterNull` test):

```csharp
    [Fact]
    public void Load_ParsesContainerBlockFromYaml()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                container:
                  image: ghcr.io/company/orders
                  port: 8080
                  defaultTag: latest
            """);

        try
        {
            var catalog = ServiceCatalogLoader.Load(path);

            var orders = Assert.Single(catalog.Services);
            Assert.NotNull(orders.Value.Container);
            Assert.Equal("ghcr.io/company/orders", orders.Value.Container.Image);
            Assert.Equal(8080, orders.Value.Container.Port);
            Assert.Equal("latest", orders.Value.Container.DefaultTag);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_NoContainerBlock_LeavesContainerNull()
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

            Assert.Null(catalog.Services["orders"].Container);
        }
        finally
        {
            File.Delete(path);
        }
    }
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceCatalogLoaderTests"`
Expected: FAIL — `ServiceMetadata` has no `Container` member (compile error).

- [ ] **Step 3: Write the failing test for developer-config `tag` parsing**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs` (inside the existing `DeveloperConfigLoaderTests` class):

```csharp
    [Fact]
    public void Load_ParsesTagFromJson()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {
              "services": {
                "orders": { "source": "container", "tag": "v1.4.2" }
              }
            }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Equal("container", config.Services["orders"].Source);
            Assert.Equal("v1.4.2", config.Services["orders"].Tag);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_TagOmitted_LeavesItNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            { "services": { "orders": { "source": "local" } } }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Null(config.Services["orders"].Tag);
        }
        finally
        {
            File.Delete(path);
        }
    }
```

- [ ] **Step 4: Run the new tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~DeveloperConfigLoaderTests"`
Expected: FAIL — `ServiceDeveloperConfig` has no `Tag` member (compile error).

- [ ] **Step 5: Create `ContainerMetadata` and wire it into `ServiceMetadata`**

Create `src/Aspire.Hosting.ServiceSources/Config/ContainerMetadata.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ContainerMetadata
{
    public string Image { get; set; } = "";

    public int? Port { get; set; }

    public string? DefaultTag { get; set; }
}
```

Modify `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs` — add a `Container` property alongside the existing `Cluster`/`Url` properties:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceMetadata
{
    public string Repository { get; set; } = "";

    public string Project { get; set; } = "";

    public string? DefaultRef { get; set; }

    public ClusterMetadata? Cluster { get; set; }

    public UrlMetadata? Url { get; set; }

    public ContainerMetadata? Container { get; set; }
}
```

- [ ] **Step 6: Add `Tag` to `ServiceDeveloperConfig`**

Modify `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs` — add a `Tag` property alongside the existing fields:

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

    public string? Url { get; set; }

    public string? Tag { get; set; }
}
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceCatalogLoaderTests|FullyQualifiedName~DeveloperConfigLoaderTests"`
Expected: PASS (all tests in both files, including pre-existing ones)

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Config/ContainerMetadata.cs \
        src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs \
        src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs
git commit -m "Add container config schema (catalog container block, dev-config tag)"
```

---

## Task 2: `ContainerSource` — image/tag/port resolution and fail-fast config validation

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Sources/ContainerSource.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/ContainerSourceTests.cs`

**Interfaces:**
- Consumes: `ServiceMetadata.Container : ContainerMetadata?`, `ContainerMetadata.Image : string`, `.Port : int?`, `.DefaultTag : string?` (Task 1).
- Consumes: `ServiceDeveloperConfig.Tag : string?` (Task 1).
- Consumes: `ServiceSourcesConfigurationException(string message)` (existing).
- Produces: `internal static (string Image, string? Tag, int Port) ContainerSource.ResolveContainerConfig(string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)` — the testable core, mirroring `ClusterSource.BuildPortForwardArgs`'s "static method taking all inputs explicitly" pattern. Validates `container.image` (non-null, non-whitespace) and `container.port` (non-null), then resolves `Tag` as local.json `tag` → catalog `defaultTag` → `null`.
- Produces: `internal sealed class ContainerSource : IServiceSource` with `Resolve(...)` matching the `IServiceSource` interface exactly (same signature as `LocalProjectSource.Resolve`/`ClusterSource.Resolve`). Task 3 registers this type; Task 3's `AddServiceTests` exercise `Resolve` end to end.

- [ ] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.Tests/Sources/ContainerSourceTests.cs`:

```csharp
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class ContainerSourceTests
{
    private const string ServiceName = "orders";

    private static ServiceMetadata Metadata(string? image = "ghcr.io/company/orders", int? port = 8080, string? defaultTag = null) =>
        new()
        {
            Repository = "https://github.com/company/orders",
            Project = "Orders.csproj",
            Container = image is null ? null : new ContainerMetadata { Image = image, Port = port, DefaultTag = defaultTag },
        };

    private static ServiceDeveloperConfig DevConfig(string? tag = null) =>
        new() { Source = "container", Tag = tag };

    [Fact]
    public void ResolveContainerConfig_NoTagAnywhere_ReturnsNullTag()
    {
        var (image, tag, port) = ContainerSource.ResolveContainerConfig(
            ServiceName, Metadata(image: "ghcr.io/company/orders", port: 8080, defaultTag: null), DevConfig(tag: null));

        Assert.Equal("ghcr.io/company/orders", image);
        Assert.Null(tag);
        Assert.Equal(8080, port);
    }

    [Fact]
    public void ResolveContainerConfig_LocalTagOverride_TakesPrecedenceOverCatalogDefaultTag()
    {
        var (_, tag, _) = ContainerSource.ResolveContainerConfig(
            ServiceName, Metadata(defaultTag: "latest"), DevConfig(tag: "v1.4.2"));

        Assert.Equal("v1.4.2", tag);
    }

    [Fact]
    public void ResolveContainerConfig_LocalTagUnset_FallsBackToCatalogDefaultTag()
    {
        var (_, tag, _) = ContainerSource.ResolveContainerConfig(
            ServiceName, Metadata(defaultTag: "latest"), DevConfig(tag: null));

        Assert.Equal("latest", tag);
    }

    [Fact]
    public void ResolveContainerConfig_NoContainerBlock_ThrowsNamingServiceAndImage()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ContainerSource.ResolveContainerConfig(ServiceName, Metadata(image: null), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("container.image", ex.Message);
    }

    [Fact]
    public void ResolveContainerConfig_EmptyImage_ThrowsNamingServiceAndImage()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ContainerSource.ResolveContainerConfig(ServiceName, Metadata(image: ""), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("container.image", ex.Message);
    }

    [Fact]
    public void ResolveContainerConfig_WhitespaceImage_ThrowsNamingServiceAndImage()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ContainerSource.ResolveContainerConfig(ServiceName, Metadata(image: "   "), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("container.image", ex.Message);
    }

    [Fact]
    public void ResolveContainerConfig_MissingPort_ThrowsNamingServiceAndPort()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            ContainerSource.ResolveContainerConfig(ServiceName, Metadata(port: null), DevConfig()));

        Assert.Contains(ServiceName, ex.Message);
        Assert.Contains("container.port", ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ContainerSourceTests"`
Expected: FAIL — `ContainerSource` type does not exist (compile error).

- [ ] **Step 3: Write `ContainerSource`**

Create `src/Aspire.Hosting.ServiceSources/Sources/ContainerSource.cs`:

```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class ContainerSource : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var (image, tag, port) = ResolveContainerConfig(serviceName, metadata, config);

        var containerBuilder = tag is null
            ? builder.AddContainer(serviceName, image)
            : builder.AddContainer(serviceName, image, tag);

        containerBuilder.WithHttpEndpoint(targetPort: port);

        return ServiceResource.CreateFacade(builder, serviceName, containerBuilder);
    }

    internal static (string Image, string? Tag, int Port) ResolveContainerConfig(
        string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        if (metadata.Container is null || string.IsNullOrWhiteSpace(metadata.Container.Image))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' source is 'container' but servicesources.yaml has no container.image entry.");
        }

        var port = metadata.Container.Port ?? throw new ServiceSourcesConfigurationException(
            $"Service '{serviceName}' source is 'container' but servicesources.yaml has no container.port entry.");

        var tag = config.Tag ?? metadata.Container.DefaultTag;

        return (metadata.Container.Image, tag, port);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ContainerSourceTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/ContainerSource.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Sources/ContainerSourceTests.cs
git commit -m "Add ContainerSource image/tag/port resolution and fail-fast config validation"
```

---

## Task 3: Wire `ContainerSource` into `AddService()`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`

**Interfaces:**
- Consumes: `ContainerSource` (Task 2).
- Consumes: `ServiceResource.CreateFacade<TResource>` (already generalized — no change needed).

- [ ] **Step 1: Write the failing tests for `AddService` dispatching to `ContainerSource`**

Add to `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`, inside the `AddServiceTests` class (near the other `AddService_*Source_*` tests):

```csharp
    [Fact]
    public void AddService_ContainerSource_AddsContainerAndReturnsFacade()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
                container:
                  image: ghcr.io/company/orders
                  port: 8080
                  defaultTag: latest
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "container" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        Assert.Contains(builder.Resources, r => r.Name == "orders");
        Assert.DoesNotContain(builder.Resources, r => ReferenceEquals(r, service.Resource));

        var container = Assert.IsType<ContainerResource>(
            Assert.Single(builder.Resources, r => r.Name == "orders"));
        Assert.Equal("ghcr.io/company/orders", container.Annotations.OfType<ContainerImageAnnotation>().Single().Image);

        var endpoint = service.GetEndpoint("http");
        Assert.Equal("http", endpoint.EndpointName);
    }

    [Fact]
    public void AddService_ContainerSourceMissingImage_ThrowsNamingServiceAndImage()
    {
        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), """
            { "services": { "orders": { "source": "container" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("container.image", ex.Message);
    }
```

`ContainerResource`/`ContainerImageAnnotation` live in `Aspire.Hosting.ApplicationModel`, already imported by this file's existing `using Aspire.Hosting.ApplicationModel;`.

- [ ] **Step 2: Run the tests to verify the new ones fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~AddServiceTests"`
Expected: `AddService_ContainerSource_AddsContainerAndReturnsFacade` and `AddService_ContainerSourceMissingImage_ThrowsNamingServiceAndImage` FAIL with `ServiceSourcesConfigurationException: Service 'orders' has source 'container', which is not implemented yet.` All pre-existing tests in this file still PASS.

- [ ] **Step 3: Register `ContainerSource` in `Sources`**

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
        ["url"] = new UrlSource(),
        ["container"] = new ContainerSource(),
    };

    // ... rest of the file unchanged, except the XML doc summary below ...
}
```

Update the `AddService` XML doc `<summary>` to add a clause for the new source, following the existing sentence's pattern (each source gets one clause ending in "(the `"..."` source)"):

```
    /// ...; or a fixed, already-known URL — e.g. a Kubernetes ingress or any other reachable
    /// HTTP endpoint — with no underlying resource for Aspire to run (the <c>"url"</c> source);
    /// or a published container image run locally via Aspire's own <c>AddContainer(...)</c>,
    /// with image pull and lifecycle managed entirely by Aspire's own container-runtime
    /// integration (the <c>"container"</c> source).
```

(Only the `using` list, the `Sources` dictionary initializer, and the XML doc summary change — the `AddService` method body is untouched.)

- [ ] **Step 4: Run the full test suite to verify everything passes**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests`
Expected: PASS — all tests, including the untouched `LocalProjectSourceTests`, `ClusterSourceTests`, `UrlSourceTests`, `ServiceResourceTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs \
        test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs
git commit -m "Register ContainerSource under 'container' in AddService()"
```

---

## Self-Review Notes

- **Spec coverage:** Architecture (Task 2's `ResolveContainerConfig` + `Resolve`, Task 3's dispatch), Config Schema (Task 1), Resolution Flow steps 1–6 (Task 2's resolve-then-`AddContainer`-then-`WithHttpEndpoint`-then-facade sequence, Task 3's dispatch), Error Handling config-vs-runtime split (Task 2 throws only on missing/blank image or missing port; runtime errors are never touched because `Resolve` delegates to `AddContainer` and does nothing else), Testing section (Tasks 1–2 cover config parsing and orchestration/precedence; Task 3 covers end-to-end dispatch; no integration test added, matching "No automated container/integration test in v1"). Out-of-scope items (non-HTTP endpoints, registry auth config, digest pinning, pull-policy/lifetime overrides, env var wiring, auto-source-selection) are intentionally absent from every task.
- **Placeholder scan:** No TBD/TODO markers; every step has literal code.
- **Type consistency:** `ResolveContainerConfig` signature is identical everywhere it's referenced (Task 2 defines it, Task 2's own tests call it — Task 3 doesn't call it directly, only `Resolve`). `ContainerMetadata`, `ServiceDeveloperConfig.Tag` are defined once each (Task 1) and consumed with matching names/types in Task 2.
- **No `IPortAllocator` seam introduced** — deliberately, per spec's "Why no `IPortAllocator`" section; `ContainerSource`'s constructor takes no dependencies, unlike `ClusterSource(IPortAllocator)`.
