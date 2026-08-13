# Aspire.Hosting.ServiceSources — Milestone 1a Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `Aspire.Hosting.ServiceSources`, a package where `builder.AddService("orders")` in an Aspire AppHost resolves to a local project — either a developer-managed checkout (`path` in config) or a package-managed git clone — without ever touching the AppHost's `.csproj`/`.sln`.

**Architecture:** `AddService()` loads two config files from the AppHost directory (a committed YAML catalog, a gitignored per-developer JSON file), dispatches by `source` value to an `IServiceSource` implementation (only `"local"` exists in this milestone), which resolves a real project path (developer-managed dir, or clone-then-checkout via LibGit2Sharp into a cache dir) and calls Aspire's own `AddProject(name, path)`. The result is wrapped in a `ServiceResource` facade — a class implementing the marker interface `IResourceWithServiceDiscovery`, built via `builder.CreateResourceBuilder()` so it is **never registered** in Aspire's resource model, with the real resource's `EndpointAnnotation`s copied onto it so `GetEndpoint()`/`WithReference()` resolve identically to the real resource. Aspire's own `AddProject` handles building the out-of-graph project via its companion `-rebuilder` resource — this package does no building itself.

**Tech Stack:** .NET 10 / C# 13, `Aspire.Hosting` 13.4.6, `YamlDotNet` 18.1.0, `LibGit2Sharp` 0.32.0, xUnit.

**Spec:** [docs/superpowers/specs/2026-08-09-servicesources-design.md](../specs/2026-08-09-servicesources-design.md) — approved design this plan implements. All facts below (API shapes, package behavior) were independently re-verified against the real installed packages while writing this plan (see per-task notes), not copied blind from the spec.

## Global Constraints

- Target framework: `net10.0` only (no multi-targeting in this milestone — matches the spike's validated environment; the spec's own confirmations are all against .NET 10).
- Pin exact package versions: `Aspire.Hosting` `13.4.6` (main library), `Aspire.Hosting.AppHost` `13.4.6` (demo AppHost only), `YamlDotNet` `18.1.0`, `LibGit2Sharp` `0.32.0`.
- `Nullable` and `ImplicitUsings` enabled on every project (the `dotnet new classlib`/`xunit` net10.0 defaults already set these).
- No config file walk-up: `servicesources.yaml`/`servicesources.local.json` are read only from `builder.AppHostDirectory` itself.
- No automatic pull/update of an already-cloned repo in v1 — if the cache directory for a repo already exists, leave it untouched (no git operations at all).
- No build step and no build-serialization lock in this package — `AddProject(name, path)` alone is sufficient; Aspire's own `-rebuilder` companion resource handles building.
- `source` values other than `"local"` must fail with an exception naming the service and the unimplemented source value — no silent fallback.
- All fail-fast errors throw `ServiceSourcesConfigurationException` and name both the service and the exact failed step (missing config entry, missing ref, missing project file, unimplemented source).
- Public API surface is deliberately minimal: only `ServiceResource` (the facade type) and the `AddService` extension method are `public`. Every config model, loader, git abstraction, and source implementation is `internal`, exposed to the test project via `InternalsVisibleTo`.

---

## File Structure

```
aspire-service-discovery/
  ServiceSources.sln
  src/Aspire.Hosting.ServiceSources/
    Aspire.Hosting.ServiceSources.csproj
    AssemblyInfo.cs                        # InternalsVisibleTo the test project
    ServiceSourcesConfigurationException.cs
    ServiceResource.cs                     # public facade + internal CreateFacade factory
    IServiceSource.cs                      # internal extensibility seam
    ServiceSourcesBuilderExtensions.cs     # public AddService() entry point
    Config/
      ServiceMetadata.cs                   # one servicesources.yaml entry
      ServiceCatalog.cs                    # whole servicesources.yaml
      ServiceCatalogLoader.cs              # YAML -> ServiceCatalog
      ServiceDeveloperConfig.cs            # one servicesources.local.json service entry
      DeveloperConfigFile.cs               # whole servicesources.local.json
      DeveloperConfigLoader.cs             # JSON -> DeveloperConfigFile
      ServiceSourcesConfigCache.cs         # per-builder cache + fail-fast lookup
    Git/
      IGitClient.cs                        # internal git seam
      LibGit2SharpGitClient.cs             # real implementation
    Sources/
      LocalProjectSource.cs                # the one IServiceSource in this milestone
  test/Aspire.Hosting.ServiceSources.Tests/
    Aspire.Hosting.ServiceSources.Tests.csproj
    Config/
      ServiceCatalogLoaderTests.cs
      DeveloperConfigLoaderTests.cs
      ServiceSourcesConfigCacheTests.cs
    Git/
      LibGit2SharpGitClientTests.cs
    Sources/
      LocalProjectSourceTests.cs
    ServiceResourceTests.cs
    AddServiceTests.cs
    AddServiceIntegrationTests.cs
    Fixtures/
      sample-service.git/                  # committed bare repo fixture
  samples/
    DemoAppHost/
      DemoAppHost.csproj
      Program.cs
      servicesources.yaml
      servicesources.local.json.example    # copy to servicesources.local.json (gitignored) to run
    SampleService/
      SampleService.csproj
      Program.cs
  .gitignore
```

---

### Task 1: Solution and project scaffolding

**Files:**
- Create: `ServiceSources.sln`
- Create: `src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj`
- Create: `src/Aspire.Hosting.ServiceSources/AssemblyInfo.cs`
- Create: `test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj`
- Create: `.gitignore`

**Interfaces:**
- Produces: a buildable solution with the main library referencing `Aspire.Hosting` 13.4.6, `YamlDotNet` 18.1.0, `LibGit2Sharp` 0.32.0, and a test project (xUnit) that references the main library and can see its internals.

This is pure scaffolding — verified below by building, not by a red/green test cycle.

- [ ] **Step 1: Create the main library project**

```bash
mkdir -p src/Aspire.Hosting.ServiceSources
cd src/Aspire.Hosting.ServiceSources
dotnet new classlib -n Aspire.Hosting.ServiceSources -o .
rm Class1.cs
dotnet add package Aspire.Hosting --version 13.4.6
dotnet add package YamlDotNet --version 18.1.0
dotnet add package LibGit2Sharp --version 0.32.0
cd ../..
```

The generated `Aspire.Hosting.ServiceSources.csproj` already targets `net10.0` with `Nullable`/`ImplicitUsings` enabled (the SDK's default template) — do not hand-edit these properties.

- [ ] **Step 2: Add the InternalsVisibleTo assembly attribute**

`src/Aspire.Hosting.ServiceSources/AssemblyInfo.cs`:
```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Aspire.Hosting.ServiceSources.Tests")]
```

- [ ] **Step 3: Create the test project and reference the main library**

```bash
mkdir -p test/Aspire.Hosting.ServiceSources.Tests
cd test/Aspire.Hosting.ServiceSources.Tests
dotnet new xunit -n Aspire.Hosting.ServiceSources.Tests -o .
rm UnitTest1.cs
dotnet add reference ../../src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj
cd ../..
```

`ProjectReference` brings `Aspire.Hosting`/`YamlDotNet`/`LibGit2Sharp` into the test project transitively — no separate `dotnet add package` calls needed there.

- [ ] **Step 4: Create the solution file and add both projects**

```bash
dotnet new sln -n ServiceSources
dotnet sln add src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj
dotnet sln add test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj
```

- [ ] **Step 5: Add `.gitignore`**

`.gitignore`:
```
bin/
obj/
servicesources.local.json
```

(The last line covers the real per-developer config file everywhere it appears, including under `samples/DemoAppHost/` — only `servicesources.local.json.example` is committed there, per Task 10.)

- [ ] **Step 6: Build the solution to verify wiring**

Run: `dotnet build`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)` for both projects.

- [ ] **Step 7: Commit**

```bash
git add ServiceSources.sln src/ test/ .gitignore
git commit -m "Scaffold ServiceSources solution, main library, and test project"
```

---

### Task 2: Service catalog config — `ServiceMetadata`/`ServiceCatalog` + YAML loader

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/ServiceSourcesConfigurationException.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Config/ServiceCatalog.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`

**Interfaces:**
- Produces: `ServiceSourcesConfigurationException(string message)` (public, used by every later task for fail-fast errors). `internal sealed class ServiceMetadata { string Repository; string Project; string? DefaultRef; }`. `internal sealed class ServiceCatalog { Dictionary<string, ServiceMetadata> Services; }`. `internal static class ServiceCatalogLoader { static ServiceCatalog Load(string path); }`.

Verified against real YamlDotNet 18.1.0: plain mutable-property classes deserialize correctly with `CamelCaseNamingConvention` (`defaultRef` YAML key maps to `DefaultRef` C# property).

- [ ] **Step 1: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`:
```csharp
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

public class ServiceCatalogLoaderTests
{
    [Fact]
    public void Load_ParsesServicesFromYaml()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
                defaultRef: main
            """);

        try
        {
            var catalog = ServiceCatalogLoader.Load(path);

            var orders = Assert.Single(catalog.Services);
            Assert.Equal("orders", orders.Key);
            Assert.Equal("https://github.com/company/orders", orders.Value.Repository);
            Assert.Equal("src/Orders.Api/Orders.Api.csproj", orders.Value.Project);
            Assert.Equal("main", orders.Value.DefaultRef);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ThrowsNamingPath()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceCatalogLoader.Load("/no/such/servicesources.yaml"));

        Assert.Contains("/no/such/servicesources.yaml", ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile (the types don't exist yet)**

Run: `dotnet test --filter FullyQualifiedName~ServiceCatalogLoaderTests`
Expected: build error — `ServiceCatalogLoader`, `ServiceSourcesConfigurationException` etc. do not exist.

- [ ] **Step 3: Write the exception type**

`src/Aspire.Hosting.ServiceSources/ServiceSourcesConfigurationException.cs`:
```csharp
namespace Aspire.Hosting.ServiceSources;

public sealed class ServiceSourcesConfigurationException(string message) : Exception(message);
```

- [ ] **Step 4: Write the config models**

`src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs`:
```csharp
namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceMetadata
{
    public string Repository { get; set; } = "";

    public string Project { get; set; } = "";

    public string? DefaultRef { get; set; }
}
```

`src/Aspire.Hosting.ServiceSources/Config/ServiceCatalog.cs`:
```csharp
namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceCatalog
{
    public Dictionary<string, ServiceMetadata> Services { get; set; } = new();
}
```

- [ ] **Step 5: Write the loader**

`src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs`:
```csharp
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceCatalogLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static ServiceCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service catalog file not found at '{path}'. Expected a 'servicesources.yaml' file in the AppHost project directory.");
        }

        using var reader = new StreamReader(path);
        return Deserializer.Deserialize<ServiceCatalog>(reader) ?? new ServiceCatalog();
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ServiceCatalogLoaderTests`
Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 7: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceSourcesConfigurationException.cs src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs src/Aspire.Hosting.ServiceSources/Config/ServiceCatalog.cs src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs
git commit -m "Add ServiceCatalog YAML config loading"
```

---

### Task 3: Developer config — `ServiceDeveloperConfig`/`DeveloperConfigFile` + JSON loader

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigFile.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigLoader.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs`

**Interfaces:**
- Consumes: `ServiceSourcesConfigurationException` (Task 2).
- Produces: `internal sealed class ServiceDeveloperConfig { string Source; string? Path; string? Ref; }`. `internal sealed class DeveloperConfigFile { string? CacheDirectory; Dictionary<string, ServiceDeveloperConfig> Services; }`. `internal static class DeveloperConfigLoader { static DeveloperConfigFile Load(string path); }`.

Verified against real `System.Text.Json` with `JsonNamingPolicy.CamelCase`: `cacheDirectory`/`path`/`ref` JSON keys map correctly onto the PascalCase C# properties.

- [ ] **Step 1: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs`:
```csharp
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

public class DeveloperConfigLoaderTests
{
    [Fact]
    public void Load_ParsesServicesFromJson()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            {
              "cacheDirectory": "~/.servicesources/repos",
              "services": {
                "orders": { "source": "local" },
                "payments": { "source": "local", "path": "/home/dev/code/payments", "ref": "feature/new-checkout" }
              }
            }
            """);

        try
        {
            var config = DeveloperConfigLoader.Load(path);

            Assert.Equal("~/.servicesources/repos", config.CacheDirectory);
            Assert.Equal(2, config.Services.Count);
            Assert.Equal("local", config.Services["orders"].Source);
            Assert.Null(config.Services["orders"].Path);
            Assert.Equal("/home/dev/code/payments", config.Services["payments"].Path);
            Assert.Equal("feature/new-checkout", config.Services["payments"].Ref);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingFile_ThrowsNamingPath()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => DeveloperConfigLoader.Load("/no/such/servicesources.local.json"));

        Assert.Contains("/no/such/servicesources.local.json", ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test --filter FullyQualifiedName~DeveloperConfigLoaderTests`
Expected: build error — the types don't exist yet.

- [ ] **Step 3: Write the config models**

`src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs`:
```csharp
namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class ServiceDeveloperConfig
{
    public string Source { get; set; } = "";

    public string? Path { get; set; }

    public string? Ref { get; set; }
}
```

`src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigFile.cs`:
```csharp
namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class DeveloperConfigFile
{
    public string? CacheDirectory { get; set; }

    public Dictionary<string, ServiceDeveloperConfig> Services { get; set; } = new();
}
```

- [ ] **Step 4: Write the loader**

`src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigLoader.cs`:
```csharp
using System.Text.Json;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class DeveloperConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static DeveloperConfigFile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ServiceSourcesConfigurationException(
                $"Developer config file not found at '{path}'. Expected a 'servicesources.local.json' file (gitignored) in the AppHost project directory.");
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<DeveloperConfigFile>(json, Options) ?? new DeveloperConfigFile();
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~DeveloperConfigLoaderTests`
Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigFile.cs src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigLoader.cs test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigLoaderTests.cs
git commit -m "Add DeveloperConfigFile JSON config loading"
```

---

### Task 4: `ServiceSourcesConfigCache` — per-builder caching, fail-fast lookup, cache directory resolution

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceSourcesConfigCacheTests.cs`

**Interfaces:**
- Consumes: `ServiceCatalogLoader.Load` (Task 2), `DeveloperConfigLoader.Load` (Task 3), `ServiceSourcesConfigurationException` (Task 2).
- Produces: `internal static class ServiceSourcesConfigCache { static (ServiceMetadata Metadata, ServiceDeveloperConfig DeveloperConfig) ResolveService(IDistributedApplicationBuilder builder, string serviceName); static string GetCacheDirectory(IDistributedApplicationBuilder builder); }`.

**Verified API facts** (checked by reflecting on the real `Aspire.Hosting.dll` 13.4.6 and by running real code against it, not assumed from the spec):
- `IDistributedApplicationBuilder.AppHostDirectory` is a public `string` property.
- Tests can get a real `IDistributedApplicationBuilder` pointed at an arbitrary directory via `DistributedApplication.CreateBuilder(new DistributedApplicationOptions { ProjectDirectory = someDir, Args = [] })` — confirmed `builder.AppHostDirectory` then equals `someDir` exactly. This means every test in this plan that needs a builder uses this pattern instead of any hand-rolled fake — there is no need to mock `IDistributedApplicationBuilder` anywhere in this codebase.
- Caching per builder instance uses `ConditionalWeakTable<IDistributedApplicationBuilder, T>` — safe because `IDistributedApplicationBuilder` instances are long-lived for the process lifetime of an AppHost run and each test creates its own instance.

- [ ] **Step 1: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceSourcesConfigCacheTests.cs`:
```csharp
using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

public class ServiceSourcesConfigCacheTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [],
        });

    private static string CreateAppHostDirectory(string yaml, string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), yaml);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), json);
        return dir;
    }

    [Fact]
    public void ResolveService_ReturnsMetadataAndDeveloperConfig_WhenPresentInBothFiles()
    {
        var dir = CreateAppHostDirectory(
            """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
            """,
            """{ "services": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var (metadata, developerConfig) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("https://github.com/company/orders", metadata.Repository);
        Assert.Equal("local", developerConfig.Source);
    }

    [Fact]
    public void ResolveService_ServiceMissingFromCatalog_ThrowsNamingService()
    {
        var dir = CreateAppHostDirectory(
            "services: {}",
            """{ "services": { "orders": { "source": "local" } } }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("servicesources.yaml", ex.Message);
    }

    [Fact]
    public void ResolveService_ServiceMissingFromDeveloperConfig_ThrowsNamingService()
    {
        var dir = CreateAppHostDirectory(
            """
            services:
              orders:
                repository: https://github.com/company/orders
                project: src/Orders.Api/Orders.Api.csproj
            """,
            """{ "services": {} }""");

        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("servicesources.local.json", ex.Message);
    }

    [Fact]
    public void GetCacheDirectory_ExpandsTildeToHomeDirectory()
    {
        var dir = CreateAppHostDirectory(
            "services: {}",
            """{ "cacheDirectory": "~/.servicesources/repos", "services": {} }""");

        var builder = CreateBuilder(dir);

        var cacheDirectory = ServiceSourcesConfigCache.GetCacheDirectory(builder);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".servicesources/repos"), cacheDirectory);
    }

    [Fact]
    public void GetCacheDirectory_DefaultsWhenNotConfigured()
    {
        var dir = CreateAppHostDirectory("services: {}", """{ "services": {} }""");
        var builder = CreateBuilder(dir);

        var cacheDirectory = ServiceSourcesConfigCache.GetCacheDirectory(builder);

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".servicesources/repos"), cacheDirectory);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test --filter FullyQualifiedName~ServiceSourcesConfigCacheTests`
Expected: build error — `ServiceSourcesConfigCache` does not exist yet.

- [ ] **Step 3: Write the implementation**

`src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs`:
```csharp
using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceSourcesConfigCache
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, LoadedConfig> Cache = new();

    public static (ServiceMetadata Metadata, ServiceDeveloperConfig DeveloperConfig) ResolveService(
        IDistributedApplicationBuilder builder, string serviceName)
    {
        var loaded = Cache.GetValue(builder, static b => LoadedConfig.Load(b.AppHostDirectory));

        if (!loaded.Catalog.Services.TryGetValue(serviceName, out var metadata))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' was not found in 'servicesources.yaml'.");
        }

        if (!loaded.DeveloperConfig.Services.TryGetValue(serviceName, out var developerConfig))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}' was not found in 'servicesources.local.json'.");
        }

        return (metadata, developerConfig);
    }

    public static string GetCacheDirectory(IDistributedApplicationBuilder builder)
    {
        var loaded = Cache.GetValue(builder, static b => LoadedConfig.Load(b.AppHostDirectory));
        var configured = loaded.DeveloperConfig.CacheDirectory ?? "~/.servicesources/repos";
        return ExpandHome(configured);
    }

    private static string ExpandHome(string path)
    {
        if (!path.StartsWith('~'))
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, path.TrimStart('~', '/', '\\'));
    }

    private sealed class LoadedConfig
    {
        public required ServiceCatalog Catalog { get; init; }

        public required DeveloperConfigFile DeveloperConfig { get; init; }

        public static LoadedConfig Load(string appHostDirectory)
        {
            var catalog = ServiceCatalogLoader.Load(Path.Combine(appHostDirectory, "servicesources.yaml"));
            var developerConfig = DeveloperConfigLoader.Load(Path.Combine(appHostDirectory, "servicesources.local.json"));
            return new LoadedConfig { Catalog = catalog, DeveloperConfig = developerConfig };
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ServiceSourcesConfigCacheTests`
Expected: `Passed! - Failed: 0, Passed: 5`

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceSourcesConfigCacheTests.cs
git commit -m "Add per-builder config caching and fail-fast service lookup"
```

---

### Task 5: `IGitClient` + `LibGit2SharpGitClient`

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Git/LibGit2SharpGitClient.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs`

**Interfaces:**
- Consumes: `ServiceSourcesConfigurationException` (Task 2).
- Produces: `internal interface IGitClient { void Clone(string repositoryUrl, string destinationPath); void Checkout(string repositoryPath, string reference); }` and `internal sealed class LibGit2SharpGitClient : IGitClient`.

**Verified against real LibGit2Sharp 0.32.0, against real local repos (no network):**
- `Repository.Clone(sourceUrl, destinationPath)` works with a plain local filesystem path as `sourceUrl` (no `file://` prefix needed).
- `Repository.Init(dir)` creates a repo whose default branch is named `master`, not `main` — the test fixture below captures the actual default branch name at creation time rather than assuming `main`, to avoid a false dependency on git's global `init.defaultBranch` setting.
- The checkout logic below (local branch → remote-tracking branch promoted to a local tracking branch → tag → raw commit SHA → throw) was run against a real repo with a tag (`v1.0.0`) and a branch that exists only as `origin/feature/x` after clone, and correctly resolves both, and correctly throws `ServiceSourcesConfigurationException` for an unknown ref.

- [ ] **Step 1: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/Git/LibGit2SharpGitClientTests.cs`:
```csharp
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Git;
using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Tests.Git;

public class LibGit2SharpGitClientTests
{
    private static string CreateOriginRepo()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        Repository.Init(dir);

        using var repo = new Repository(dir);
        var defaultBranchName = repo.Head.FriendlyName;
        File.WriteAllText(Path.Combine(dir, "file.txt"), "main content");
        Commands.Stage(repo, "file.txt");

        var signature = new Signature("test", "test@test.com", DateTimeOffset.Now);
        repo.Commit("main commit", signature, signature);
        repo.ApplyTag("v1.0.0");

        var featureBranch = repo.CreateBranch("feature/x");
        Commands.Checkout(repo, featureBranch);
        File.WriteAllText(Path.Combine(dir, "file.txt"), "feature content");
        Commands.Stage(repo, "file.txt");
        repo.Commit("feature commit", signature, signature);

        Commands.Checkout(repo, defaultBranchName);

        return dir;
    }

    [Fact]
    public void Clone_CopiesRepositoryToDestination()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");

        new LibGit2SharpGitClient().Clone(origin, destination);

        Assert.True(File.Exists(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public void Checkout_Tag_UpdatesWorkingTreeToTaggedCommit()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        client.Checkout(destination, "v1.0.0");

        Assert.Equal("main content", File.ReadAllText(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public void Checkout_RemoteOnlyBranch_CreatesLocalTrackingBranchAndUpdatesWorkingTree()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        client.Checkout(destination, "feature/x");

        Assert.Equal("feature content", File.ReadAllText(Path.Combine(destination, "file.txt")));
    }

    [Fact]
    public void Checkout_UnknownRef_ThrowsNamingRef()
    {
        var origin = CreateOriginRepo();
        var destination = Path.Combine(Directory.CreateTempSubdirectory().FullName, "clone");
        var client = new LibGit2SharpGitClient();
        client.Clone(origin, destination);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => client.Checkout(destination, "does-not-exist"));

        Assert.Contains("does-not-exist", ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test --filter FullyQualifiedName~LibGit2SharpGitClientTests`
Expected: build error — `IGitClient`/`LibGit2SharpGitClient` do not exist yet.

- [ ] **Step 3: Write the interface and implementation**

`src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs`:
```csharp
namespace Aspire.Hosting.ServiceSources.Git;

internal interface IGitClient
{
    void Clone(string repositoryUrl, string destinationPath);

    void Checkout(string repositoryPath, string reference);
}
```

`src/Aspire.Hosting.ServiceSources/Git/LibGit2SharpGitClient.cs`:
```csharp
using LibGit2Sharp;

namespace Aspire.Hosting.ServiceSources.Git;

internal sealed class LibGit2SharpGitClient : IGitClient
{
    public void Clone(string repositoryUrl, string destinationPath)
    {
        Repository.Clone(repositoryUrl, destinationPath);
    }

    public void Checkout(string repositoryPath, string reference)
    {
        using var repo = new Repository(repositoryPath);

        var branch = repo.Branches[reference] ?? repo.Branches[$"origin/{reference}"];
        if (branch is not null)
        {
            if (!branch.IsRemote)
            {
                Commands.Checkout(repo, branch);
                return;
            }

            var localBranch = repo.CreateBranch(reference, branch.Tip);
            repo.Branches.Update(localBranch, b => b.TrackedBranch = branch.CanonicalName);
            Commands.Checkout(repo, localBranch);
            return;
        }

        var tag = repo.Tags[reference];
        if (tag is not null)
        {
            Commands.Checkout(repo, tag.Target.Sha);
            return;
        }

        var commit = repo.Lookup<Commit>(reference);
        if (commit is not null)
        {
            Commands.Checkout(repo, commit.Sha);
            return;
        }

        throw new ServiceSourcesConfigurationException(
            $"Ref '{reference}' was not found in repository at '{repositoryPath}'.");
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~LibGit2SharpGitClientTests`
Expected: `Passed! - Failed: 0, Passed: 4`

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Git/ test/Aspire.Hosting.ServiceSources.Tests/Git/
git commit -m "Add IGitClient and LibGit2Sharp-backed implementation"
```

---

### Task 6: `LocalProjectSource.ResolveProjectPath` — path resolution logic

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs` (this task writes only the static `ResolveProjectPath` method and its private helper; the `IServiceSource.Resolve` instance method that calls Aspire's `AddProject` is added in Task 8)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`

**Interfaces:**
- Consumes: `ServiceMetadata`, `ServiceDeveloperConfig` (Tasks 2–3), `IGitClient` (Task 5), `ServiceSourcesConfigurationException` (Task 2).
- Produces: `internal static string LocalProjectSource.ResolveProjectPath(ServiceMetadata metadata, ServiceDeveloperConfig config, string cacheDirectory, IGitClient gitClient)`. This is the pure, fully unit-testable core of the resolution flow — no Aspire builder involved.

This is the resolution-flow logic from the spec (steps 3–6): `path` override skips git entirely; otherwise compute `<cacheDirectory>/<repo-name>`, clone only if that directory doesn't already exist, checkout the resolved ref (`config.Ref` → `metadata.DefaultRef` → skip entirely) only right after a fresh clone, then require the resolved `.csproj` to exist.

- [ ] **Step 1: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`:
```csharp
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class LocalProjectSourceTests
{
    private sealed class FakeGitClient : IGitClient
    {
        public List<(string RepositoryUrl, string DestinationPath)> ClonedRepos { get; } = [];

        public List<(string RepositoryPath, string Reference)> CheckedOutRefs { get; } = [];

        public void Clone(string repositoryUrl, string destinationPath)
        {
            ClonedRepos.Add((repositoryUrl, destinationPath));
            Directory.CreateDirectory(destinationPath);
            File.WriteAllText(Path.Combine(destinationPath, "Orders.csproj"), "<Project />");
        }

        public void Checkout(string repositoryPath, string reference)
        {
            CheckedOutRefs.Add((repositoryPath, reference));
        }
    }

    private static ServiceMetadata Metadata(string repository = "https://github.com/company/orders", string project = "Orders.csproj", string? defaultRef = null) =>
        new() { Repository = repository, Project = project, DefaultRef = defaultRef };

    private static ServiceDeveloperConfig DevConfig(string? path = null, string? @ref = null) =>
        new() { Source = "local", Path = path, Ref = @ref };

    [Fact]
    public void ResolveProjectPath_PathIsSet_UsesItDirectlyWithoutTouchingGit()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        var projectPath = LocalProjectSource.ResolveProjectPath(
            Metadata(project: "Orders.csproj"), DevConfig(path: repoDir), "/unused/cache", gitClient);

        Assert.Equal(Path.Combine(repoDir, "Orders.csproj"), projectPath);
        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_ClonesIntoCacheDirectoryUnderRepoName()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        var projectPath = LocalProjectSource.ResolveProjectPath(
            Metadata(repository: "https://github.com/company/orders"), DevConfig(), cacheDirectory, gitClient);

        var (repositoryUrl, destinationPath) = Assert.Single(gitClient.ClonedRepos);
        Assert.Equal("https://github.com/company/orders", repositoryUrl);
        Assert.Equal(Path.Combine(cacheDirectory, "orders"), destinationPath);
        Assert.Equal(Path.Combine(destinationPath, "Orders.csproj"), projectPath);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_UsesDeveloperRefOverCatalogDefaultRef()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            Metadata(defaultRef: "main"), DevConfig(@ref: "feature/x"), cacheDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("feature/x", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_FallsBackToCatalogDefaultRefWhenDeveloperRefUnset()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            Metadata(defaultRef: "main"), DevConfig(@ref: null), cacheDirectory, gitClient);

        var (_, reference) = Assert.Single(gitClient.CheckedOutRefs);
        Assert.Equal("main", reference);
    }

    [Fact]
    public void ResolveProjectPath_CacheMiss_NoRefConfigured_SkipsCheckout()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            Metadata(defaultRef: null), DevConfig(@ref: null), cacheDirectory, gitClient);

        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_CacheHit_DoesNotCloneOrCheckout()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var repoDir = Path.Combine(cacheDirectory, "orders");
        Directory.CreateDirectory(repoDir);
        File.WriteAllText(Path.Combine(repoDir, "Orders.csproj"), "<Project />");
        var gitClient = new FakeGitClient();

        LocalProjectSource.ResolveProjectPath(
            Metadata(defaultRef: "main"), DevConfig(), cacheDirectory, gitClient);

        Assert.Empty(gitClient.ClonedRepos);
        Assert.Empty(gitClient.CheckedOutRefs);
    }

    [Fact]
    public void ResolveProjectPath_ProjectFileMissing_ThrowsNamingProjectAndRoot()
    {
        var repoDir = Directory.CreateTempSubdirectory().FullName;

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() =>
            LocalProjectSource.ResolveProjectPath(
                Metadata(project: "src/Missing.csproj"), DevConfig(path: repoDir), "/unused/cache", new FakeGitClient()));

        Assert.Contains("src/Missing.csproj", ex.Message);
        Assert.Contains(repoDir, ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test --filter FullyQualifiedName~LocalProjectSourceTests`
Expected: build error — `LocalProjectSource` does not exist yet.

- [ ] **Step 3: Write the implementation**

`src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`:
```csharp
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class LocalProjectSource
{
    internal static string ResolveProjectPath(
        ServiceMetadata metadata, ServiceDeveloperConfig config, string cacheDirectory, IGitClient gitClient)
    {
        string repoRoot;

        if (config.Path is not null)
        {
            repoRoot = config.Path;
        }
        else
        {
            var repoName = GetRepositoryName(metadata.Repository);
            repoRoot = Path.Combine(cacheDirectory, repoName);

            if (!Directory.Exists(repoRoot))
            {
                gitClient.Clone(metadata.Repository, repoRoot);

                var reference = config.Ref ?? metadata.DefaultRef;
                if (reference is not null)
                {
                    gitClient.Checkout(repoRoot, reference);
                }
            }
        }

        var projectPath = Path.Combine(repoRoot, metadata.Project);
        if (!File.Exists(projectPath))
        {
            throw new ServiceSourcesConfigurationException(
                $"Project file '{metadata.Project}' was not found under '{repoRoot}'.");
        }

        return projectPath;
    }

    private static string GetRepositoryName(string repositoryUrl)
    {
        var trimmed = repositoryUrl.TrimEnd('/');
        var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return lastSegment.EndsWith(".git") ? lastSegment[..^4] : lastSegment;
    }
}
```

Note: this class becomes `internal sealed class LocalProjectSource(IGitClient gitClient) : IServiceSource` with an added instance `Resolve` method in Task 8 — this task only needs the static method, so no constructor yet.

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~LocalProjectSourceTests`
Expected: `Passed! - Failed: 0, Passed: 7`

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs
git commit -m "Add LocalProjectSource path resolution (cache, path override, ref precedence)"
```

---

### Task 7: `ServiceResource` facade

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/ServiceResource.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/ServiceResourceTests.cs`

**Interfaces:**
- Produces: `public sealed class ServiceResource : Resource, IResourceWithServiceDiscovery` and `internal static IResourceBuilder<IResourceWithServiceDiscovery> ServiceResource.CreateFacade(IDistributedApplicationBuilder builder, string name, IResourceBuilder<ProjectResource> realResource)`.

**Verified against the real `Aspire.Hosting.dll` 13.4.6 by compiling and running real code (not just reading the design doc):**
- `Aspire.Hosting.ApplicationModel.Resource` is `abstract` with a `protected Resource(string name)` constructor — subclassing it from another assembly and calling `base(name)` compiles and works.
- `Aspire.Hosting.IResourceWithServiceDiscovery` (note: **not** in the `ApplicationModel` namespace) is a pure marker interface extending only `IResourceWithEndpoints`/`IResource`, exactly as the design doc states.
- `IDistributedApplicationBuilder.CreateResourceBuilder<T>(T resource)` does **not** add the resource to `builder.Resources` — confirmed by comparing `Resources.Count` before/after.
- `IResourceBuilder<T>` is declared `IResourceBuilder<out T>` (covariant), so a `IResourceBuilder<ServiceResource>` converts implicitly to `IResourceBuilder<IResourceWithServiceDiscovery>` — no cast needed.
- Copying `EndpointAnnotation` instances from the real resource's `Annotations` collection onto the facade's `Annotations` collection makes `facade.GetEndpoint(name)` resolve the **same annotation instance** as `realResource.GetEndpoint(name)` — confirmed with `ReferenceEquals`.
- `consumer.WithReference(facade)` compiles and succeeds at model-build time (no DCP run needed to prove this).
- `AddProject(name, path)` requires the target `.csproj` to actually exist on disk at builder-construction time — it throws `Aspire.Hosting.DistributedApplicationException` immediately if not, which is exactly why `LocalProjectSource.ResolveProjectPath` (Task 6) must produce a real, existing path before this facade is ever built.

- [ ] **Step 1: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/ServiceResourceTests.cs`:
```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.Tests;

public class ServiceResourceTests
{
    [Fact]
    public void CreateFacade_IsNotRegisteredInBuilderResources()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var realProject = builder.AddProject("orders", CreateFakeCsproj());
        var resourcesBeforeFacade = builder.Resources.Count;

        ServiceResource.CreateFacade(builder, "orders", realProject);

        Assert.Equal(resourcesBeforeFacade, builder.Resources.Count);
    }

    [Fact]
    public void CreateFacade_CopiesEndpointAnnotationsFromRealResource()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var realProject = builder.AddProject("orders", CreateFakeCsproj())
            .WithHttpEndpoint(name: "http", port: 5001);

        var facade = ServiceResource.CreateFacade(builder, "orders", realProject);

        var realEndpoint = realProject.Resource.Annotations.OfType<EndpointAnnotation>().Single(a => a.Name == "http");
        var facadeEndpoint = facade.Resource.Annotations.OfType<EndpointAnnotation>().Single(a => a.Name == "http");
        Assert.Same(realEndpoint, facadeEndpoint);
    }

    [Fact]
    public void CreateFacade_CanBeUsedWithWithReference()
    {
        var builder = DistributedApplication.CreateBuilder([]);
        var realProject = builder.AddProject("orders", CreateFakeCsproj())
            .WithHttpEndpoint(name: "http", port: 5001);
        var facade = ServiceResource.CreateFacade(builder, "orders", realProject);

        var consumer = builder.AddProject("api", CreateFakeCsproj());
        consumer.WithReference(facade);

        var facadeEndpointViaBuilder = facade.GetEndpoint("http");
        Assert.Equal("http", facadeEndpointViaBuilder.EndpointName);
    }

    private static string CreateFakeCsproj()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "Fake.csproj");
        File.WriteAllText(path, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        return path;
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test --filter FullyQualifiedName~ServiceResourceTests`
Expected: build error — `ServiceResource` does not exist yet.

- [ ] **Step 3: Write the implementation**

`src/Aspire.Hosting.ServiceSources/ServiceResource.cs`:
```csharp
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

public sealed class ServiceResource : Resource, IResourceWithServiceDiscovery
{
    internal ServiceResource(string name) : base(name)
    {
    }

    internal static IResourceBuilder<IResourceWithServiceDiscovery> CreateFacade(
        IDistributedApplicationBuilder builder, string name, IResourceBuilder<ProjectResource> realResource)
    {
        var facade = builder.CreateResourceBuilder(new ServiceResource(name));

        foreach (var endpoint in realResource.Resource.Annotations.OfType<EndpointAnnotation>())
        {
            facade.Resource.Annotations.Add(endpoint);
        }

        return facade;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ServiceResourceTests`
Expected: `Passed! - Failed: 0, Passed: 3`

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ServiceResource.cs test/Aspire.Hosting.ServiceSources.Tests/ServiceResourceTests.cs
git commit -m "Add ServiceResource facade that wraps a real resource without registering it"
```

---

### Task 8: `IServiceSource`, `LocalProjectSource.Resolve`, and `AddService`

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/IServiceSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs` (add constructor + `Resolve` instance method)
- Create: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`

**Interfaces:**
- Consumes: `ServiceSourcesConfigCache.ResolveService`/`GetCacheDirectory` (Task 4), `LocalProjectSource.ResolveProjectPath` (Task 6), `ServiceResource.CreateFacade` (Task 7), `LibGit2SharpGitClient` (Task 5).
- Produces: `internal interface IServiceSource { IResourceBuilder<IResourceWithServiceDiscovery> Resolve(IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config); }`. `public static IResourceBuilder<IResourceWithServiceDiscovery> AddService(this IDistributedApplicationBuilder builder, string name)` — the package's sole public entry point besides `ServiceResource` itself.

This wires everything from Tasks 2–7 together. The two test cases below deliberately avoid real git: the happy-path test uses a `path` override (which `ResolveProjectPath` already proved skips all git calls in Task 6), and the error-path test never reaches `LocalProjectSource` at all. Task 9 covers the real-git, real-clone path end to end.

- [ ] **Step 1: Write the failing tests**

`test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`:
```csharp
using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.Tests;

public class AddServiceTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [],
        });

    [Fact]
    public void AddService_LocalSourceWithPathOverride_ReturnsFacadeWrappingRealProject()
    {
        var projectDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(projectDir, "Orders.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);

        var appHostDir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), """
            services:
              orders:
                repository: https://github.com/company/orders
                project: Orders.csproj
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), $$"""
            { "services": { "orders": { "source": "local", "path": "{{projectDir.Replace("\\", "\\\\")}}" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        Assert.Contains(builder.Resources, r => r.Name == "orders");
        Assert.DoesNotContain(builder.Resources, r => ReferenceEquals(r, service.Resource));
    }

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
            { "services": { "orders": { "source": "cluster" } } }
            """);

        var builder = CreateBuilder(appHostDir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message);
        Assert.Contains("cluster", ex.Message);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test --filter FullyQualifiedName~AddServiceTests`
Expected: build error — `AddService` extension method does not exist yet.

- [ ] **Step 3: Write `IServiceSource`**

`src/Aspire.Hosting.ServiceSources/IServiceSource.cs`:
```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources;

internal interface IServiceSource
{
    IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config);
}
```

- [ ] **Step 4: Extend `LocalProjectSource` to implement `IServiceSource`**

Replace the whole file with this — the class declaration changes (adds a constructor parameter and `IServiceSource`) and gains the `Resolve` method; `ResolveProjectPath` and `GetRepositoryName` keep the exact bodies Task 6 wrote:

`src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`:
```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed class LocalProjectSource(IGitClient gitClient) : IServiceSource
{
    public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config)
    {
        var cacheDirectory = ServiceSourcesConfigCache.GetCacheDirectory(builder);
        var projectPath = ResolveProjectPath(metadata, config, cacheDirectory, gitClient);

        var projectBuilder = builder.AddProject(serviceName, projectPath);
        return ServiceResource.CreateFacade(builder, serviceName, projectBuilder);
    }

    internal static string ResolveProjectPath(
        ServiceMetadata metadata, ServiceDeveloperConfig config, string cacheDirectory, IGitClient gitClient)
    {
        string repoRoot;

        if (config.Path is not null)
        {
            repoRoot = config.Path;
        }
        else
        {
            var repoName = GetRepositoryName(metadata.Repository);
            repoRoot = Path.Combine(cacheDirectory, repoName);

            if (!Directory.Exists(repoRoot))
            {
                gitClient.Clone(metadata.Repository, repoRoot);

                var reference = config.Ref ?? metadata.DefaultRef;
                if (reference is not null)
                {
                    gitClient.Checkout(repoRoot, reference);
                }
            }
        }

        var projectPath = Path.Combine(repoRoot, metadata.Project);
        if (!File.Exists(projectPath))
        {
            throw new ServiceSourcesConfigurationException(
                $"Project file '{metadata.Project}' was not found under '{repoRoot}'.");
        }

        return projectPath;
    }

    private static string GetRepositoryName(string repositoryUrl)
    {
        var trimmed = repositoryUrl.TrimEnd('/');
        var lastSegment = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return lastSegment.EndsWith(".git") ? lastSegment[..^4] : lastSegment;
    }
}
```

- [ ] **Step 5: Write `AddService`**

`src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`:
```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;
using Aspire.Hosting.ServiceSources.Sources;

namespace Aspire.Hosting.ServiceSources;

public static class ServiceSourcesBuilderExtensions
{
    private static readonly Dictionary<string, IServiceSource> Sources = new()
    {
        ["local"] = new LocalProjectSource(new LibGit2SharpGitClient()),
    };

    public static IResourceBuilder<IResourceWithServiceDiscovery> AddService(
        this IDistributedApplicationBuilder builder, string name)
    {
        var (metadata, developerConfig) = ServiceSourcesConfigCache.ResolveService(builder, name);

        if (!Sources.TryGetValue(developerConfig.Source, out var source))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{name}' has source '{developerConfig.Source}', which is not implemented yet.");
        }

        return source.Resolve(builder, name, metadata, developerConfig);
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AddServiceTests`
Expected: `Passed! - Failed: 0, Passed: 2`

- [ ] **Step 7: Run the full test suite to confirm nothing regressed**

Run: `dotnet test`
Expected: all tests from Tasks 2–8 pass (25 tests at this point).

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/IServiceSource.cs src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs
git commit -m "Add AddService() entry point wiring config, source dispatch, and the facade"
```

---

### Task 9: End-to-end integration test against a real bare git repo fixture

**Files:**
- Create: `test/Aspire.Hosting.ServiceSources.Tests/Fixtures/sample-service.git/` (committed bare repo — created via the shell script below, not by hand)
- Create: `test/Aspire.Hosting.ServiceSources.Tests/AddServiceIntegrationTests.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj` (copy the fixture to the test output directory)

**Interfaces:**
- Consumes: `AddService` (Task 8), exercised through the real, registered `LocalProjectSource` + `LibGit2SharpGitClient` — no fakes anywhere in this task.

This is "the one test requiring a real dotnet SDK in CI" from the spec's Testing section: it proves the whole pipeline (real LibGit2Sharp clone from a `file`-path repo → real ref checkout → real `AddProject` → facade endpoint copy) works together, without needing DCP/dashboard to actually run (out of scope per the spec's Testing section — that's the manual smoke test in Task 10).

**Important, verified pitfall:** a bare git repository committed as plain files loses its empty directories (`refs/`, `refs/heads/`, `refs/tags/`, `objects/info/`, `objects/pack/`, `branches/`) because git does not track empty directories — and this is true of both the MSBuild `CopyToOutputDirectory` copy *and* of committing the bare repo into this very repository, since it's the same underlying git limitation. Without those directories, LibGit2Sharp's `Repository.Clone` fails with `LibGit2Sharp.NotFoundException: could not find repository at ...` even though every other file is present. The fixture-creation script below adds a `.gitkeep` file into each of those directories specifically to keep them from disappearing — this was confirmed necessary by reproducing the failure and fixing it against a real clone.

- [ ] **Step 1: Create the fixture bare repository**

Run this once, from the repo root, to build and commit the fixture (this is fixture data creation, not application code — there is no red/green cycle for it):

```bash
set -e
WORK=$(mktemp -d)
mkdir -p "$WORK/SampleProj/Properties"

cat > "$WORK/SampleProj/SampleProj.csproj" << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF

cat > "$WORK/SampleProj/Properties/launchSettings.json" << 'EOF'
{
  "profiles": {
    "http": {
      "commandName": "Project",
      "applicationUrl": "http://localhost:5001"
    }
  }
}
EOF

cd "$WORK"
git init -q -b main
git config user.email fixture@example.com
git config user.name fixture
git add -A
git commit -qm "main: port 5001"
git tag v1.0.0

git checkout -qb feature/v2
sed -i 's/5001/5002/' SampleProj/Properties/launchSettings.json
git commit -qam "feature/v2: port 5002"
git checkout -q main

FIXTURE_DIR="$OLDPWD/test/Aspire.Hosting.ServiceSources.Tests/Fixtures/sample-service.git"
mkdir -p "$(dirname "$FIXTURE_DIR")"
git clone -q --bare "$WORK" "$FIXTURE_DIR"

# Empty directories are not tracked by git — recreate them with placeholders so
# LibGit2Sharp recognizes this as a valid repository once it's committed and re-checked-out.
mkdir -p "$FIXTURE_DIR"/refs/heads "$FIXTURE_DIR"/refs/tags "$FIXTURE_DIR"/objects/info "$FIXTURE_DIR"/objects/pack "$FIXTURE_DIR"/branches
touch "$FIXTURE_DIR"/refs/heads/.gitkeep "$FIXTURE_DIR"/refs/tags/.gitkeep "$FIXTURE_DIR"/objects/info/.gitkeep "$FIXTURE_DIR"/objects/pack/.gitkeep "$FIXTURE_DIR"/branches/.gitkeep

rm -rf "$WORK"
```

- [ ] **Step 2: Copy the fixture into the test output directory**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj`, before the closing `</Project>` tag:
```xml
  <ItemGroup>
    <None Include="Fixtures/**" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Write the integration test**

`test/Aspire.Hosting.ServiceSources.Tests/AddServiceIntegrationTests.cs`:
```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.Tests;

public class AddServiceIntegrationTests
{
    private static string FixtureRepoPath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-service.git");

    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [],
        });

    [Fact]
    public void AddService_ManagedClone_ClonesRealRepoAndChecksOutFeatureRef()
    {
        var cacheDirectory = Directory.CreateTempSubdirectory().FullName;
        var appHostDir = Directory.CreateTempSubdirectory().FullName;

        File.WriteAllText(Path.Combine(appHostDir, "servicesources.yaml"), $$"""
            services:
              orders:
                repository: {{FixtureRepoPath}}
                project: SampleProj/SampleProj.csproj
                defaultRef: main
            """);
        File.WriteAllText(Path.Combine(appHostDir, "servicesources.local.json"), $$"""
            {
              "cacheDirectory": "{{cacheDirectory.Replace("\\", "\\\\")}}",
              "services": { "orders": { "source": "local", "ref": "feature/v2" } }
            }
            """);

        var builder = CreateBuilder(appHostDir);

        var service = builder.AddService("orders");

        var clonedProjectPath = Path.Combine(cacheDirectory, "sample-service", "SampleProj", "SampleProj.csproj");
        Assert.True(File.Exists(clonedProjectPath));

        var endpoint = service.GetEndpoint("http");
        Assert.Equal("http", endpoint.EndpointName);

        var realResource = Assert.Single(builder.Resources, r => r.Name == "orders");
        var endpointAnnotation = Assert.Single(
            ((IResource)realResource).Annotations.OfType<EndpointAnnotation>());
        Assert.Equal(5002, endpointAnnotation.Port);
    }
}
```

This proves, against real git and a real `AddProject` call, that: the `feature/v2` ref (not the catalog's `main` default) was actually checked out (port 5002, not 5001); the clone landed under `<cacheDirectory>/sample-service/` (repo name derived from the URL); and the facade's `GetEndpoint("http")` resolves through to the real resource's endpoint.

- [ ] **Step 4: Run the test**

Run: `dotnet test --filter FullyQualifiedName~AddServiceIntegrationTests`
Expected: `Passed! - Failed: 0, Passed: 1`

- [ ] **Step 5: Run the full suite**

Run: `dotnet test`
Expected: all 26 tests pass.

- [ ] **Step 6: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/Fixtures/ test/Aspire.Hosting.ServiceSources.Tests/AddServiceIntegrationTests.cs test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj
git commit -m "Add end-to-end integration test against a real bare-repo fixture"
```

---

### Task 10: Demo AppHost for manual smoke testing

**Files:**
- Create: `samples/SampleService/SampleService.csproj`
- Create: `samples/SampleService/Program.cs`
- Create: `samples/DemoAppHost/DemoAppHost.csproj`
- Create: `samples/DemoAppHost/Program.cs`
- Create: `samples/DemoAppHost/servicesources.yaml`
- Create: `samples/DemoAppHost/servicesources.local.json.example`

**Interfaces:**
- Consumes: `AddService` (Task 8), the package's only public entry point besides `ServiceResource`.

Per the spec's Testing section, this milestone deliberately has no automated DCP/dashboard test — verification here is manual, by a person running the app and watching it reach `Running` in the Aspire dashboard. This task's file contents were verified by real `dotnet build` against the actual `Aspire.Hosting.AppHost` 13.4.6 SDK; two non-obvious requirements surfaced during that verification and are captured below.

**Verified pitfalls (both reproduced and fixed for real, not assumed):**
1. A hand-written AppHost `.csproj` that references only the `Aspire.Hosting.AppHost` NuGet package builds, but DCP never actually starts anything at run time. It must additionally import the `Aspire.AppHost.Sdk` MSBuild SDK and set `<IsAspireHost>true</IsAspireHost>` (this matches the finding already recorded from the design-phase spike).
2. Referencing the `Aspire.Hosting.ServiceSources` library project from the AppHost project fails to compile (`CS0234: the type or namespace name 'ServiceSources' does not exist`) unless the `<ProjectReference>` sets `IsAspireProjectResource="false"` — otherwise the Aspire AppHost SDK's build customization treats the referenced project as a candidate application resource (expecting it to be executable) rather than as an ordinary library reference, and emits warning `ASPIRE004` before failing. This is specific to referencing a non-executable class library from an AppHost project and is not mentioned in the design doc — it only surfaces once you actually try to build a real AppHost against this package.

- [ ] **Step 1: Create the sample backing service**

`samples/SampleService/SampleService.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

</Project>
```

`samples/SampleService/Program.cs`:
```csharp
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", () => "Hello from SampleService");

app.Run();
```

- [ ] **Step 2: Create the demo AppHost project**

`samples/DemoAppHost/DemoAppHost.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <Sdk Name="Aspire.AppHost.Sdk" Version="13.4.6" />

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsAspireHost>true</IsAspireHost>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Aspire.Hosting.AppHost" Version="13.4.6" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj" IsAspireProjectResource="false" />
  </ItemGroup>

</Project>
```

`samples/DemoAppHost/Program.cs`:
```csharp
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

var orders = builder.AddService("orders");

builder.Build().Run();
```

- [ ] **Step 3: Create the demo config files**

`samples/DemoAppHost/servicesources.yaml`:
```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: SampleService/SampleService.csproj
```

(`repository` is unused when `path` is set below, per the design — it's still required because `AddService` requires the service to be present in both config files.)

`samples/DemoAppHost/servicesources.local.json.example`:
```json
{
  "services": {
    "orders": { "source": "local", "path": ".." }
  }
}
```

`path: ".."` resolves relative to `DemoAppHost`, landing on the `samples/` directory — which contains `SampleService/SampleService.csproj`, matching the `project` field in the catalog above (`project` is always relative to the repo root, and here the developer's `path` override stands in for "the repo root").

- [ ] **Step 4: Add the demo projects to the solution**

```bash
dotnet sln add samples/DemoAppHost/DemoAppHost.csproj
dotnet sln add samples/SampleService/SampleService.csproj
```

- [ ] **Step 5: Verify the demo builds**

```bash
cp samples/DemoAppHost/servicesources.local.json.example samples/DemoAppHost/servicesources.local.json
dotnet build samples/DemoAppHost/DemoAppHost.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6: Manual smoke test (not automatable in this environment — do this on a machine with Docker/the Aspire CLI available)**

```bash
cd samples/DemoAppHost
aspire run
```
Expected: the Aspire dashboard opens, the `orders` resource reaches `Running`, and the dashboard's endpoint for `orders` serves `Hello from SampleService`. If `aspire run` isn't installed, `dotnet run` works too (per the design doc's own spike notes) as long as the `Aspire.AppHost.Sdk` import above is present — without it, DCP never starts and every resource hangs with no state.

**Correction (verified 2026-08-13, against a real run on Aspire 13.4.6):** this expected outcome, and the design doc's spike note it's based on (`docs/superpowers/specs/2026-08-09-servicesources-design.md:15`), are wrong about `orders-rebuilder` reaching `Running`. Empirically, `orders-rebuilder` is a hidden resource (only visible with `aspire describe --include-hidden`) that stays `NotStarted` through a normal `aspire run` — including from a completely clean `bin`/`obj`. Per `Aspire.Hosting.dll`'s own doc comments, `AddRebuilderResource` "runs 'dotnet build' on demand via the rebuild command" — it's wired to the dashboard's manual "Rebuild" command, not to startup. What actually builds the out-of-graph project at startup is the `orders` resource's own process launch, which is `dotnet run --project SampleService.csproj` (visible in the AppHost CLI log) — `dotnet run` does its own implicit restore+build. So the correct pass criterion for this step is: `orders` reaches `Running` and serves the expected response. Do not expect or wait on `orders-rebuilder` to reach `Running` — `NotStarted` there is correct, not a failure.

- [ ] **Step 7: Commit**

```bash
git add samples/ ServiceSources.sln
git commit -m "Add demo AppHost for manual smoke testing of AddService()"
```

---

## Self-Review Notes

- **Spec coverage:** `ServiceResource` facade (Task 7), `IServiceSource`/`LocalProjectSource` (Tasks 6, 8), `AddService()` (Task 8), both config files and their fail-fast lookup (Tasks 2–4), the git abstraction and its real implementation (Task 5), error handling naming service + failed step (every `ServiceSourcesConfigurationException` call site), the three-tier testing strategy from the spec's Testing section (config parsing tests, git-abstracted orchestration tests, one real end-to-end integration test) (Tasks 2–3, 6, 9), and the demo AppHost smoke test (Task 10) are all covered.
- **Out of scope confirmed absent:** no build step or build-serialization lock anywhere in this plan (Task 8's `Resolve` calls `AddProject` and stops); no repo auto-update logic; no config file walk-up (`ServiceSourcesConfigCache` always reads from `builder.AppHostDirectory` directly); no automated DCP/dashboard test (Task 9 stops at model-build time, Task 10's actual run is manual).
- **Type consistency:** `ServiceMetadata`, `ServiceDeveloperConfig`, `DeveloperConfigFile`, `ServiceCatalog`, `IGitClient`, `IServiceSource`, and `ServiceResource` are used with identical shapes everywhere they're referenced across Tasks 2–9 — each was compiled and run together as a whole (not just per-task in isolation) while preparing this plan.
