# Local-Source Kind Extension Point Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the `"local"` service source a way for other packages to register how to run a non-.NET service locally, without the core package taking on any language-specific dependency.

**Architecture:** `ServiceMetadata` gains an optional `Kind` (defaults to `"dotnet"`) and an opaque `KindConfig` captured from the per-kind yaml block. A new public `ILocalResourceKind` interface plus a per-builder registry (`builder.AddLocalKind(kind, handler)`) let a satellite package register how to turn a cloned repo into a real Aspire resource for its kind. The existing `dotnet` path (reading the flat `project:` field, calling `AddProject`) stays exactly as it is today — untouched, not routed through the new interface — because its config was never a nested opaque block to begin with. Only non-`dotnet` kinds go through the registry. The git clone/checkout logic that both paths need is extracted into a shared, kind-agnostic helper.

**Tech Stack:** .NET 8/9/10, Aspire.Hosting, YamlDotNet, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-19-multi-language-local-source-design.md`

## Global Constraints

- Every existing `servicesources.yaml` must keep working unchanged — `Kind` defaults to `"dotnet"`, the flat `project` field and `AddProject` behavior are untouched for that case.
- Core package (`Aspire.Hosting.ServiceSources.csproj`) must not gain any new `PackageReference` — this plan only touches core, using only `Aspire.Hosting` and `YamlDotNet`, which it already depends on.
- Unregistered `kind` in yaml throws `ServiceSourcesConfigurationException` naming the service, the unknown kind, and the fact that a satellite package + `AddLocalKind`/`Use*` call is needed (exact package/method name is satellite-specific and not knowable by core, so the message is generic — see Task 6).
- All existing tests in `test/Aspire.Hosting.ServiceSources.Tests` must stay green throughout.

---

## File Structure

- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs` — add `Kind` and `KindConfig`.
- Create: `src/Aspire.Hosting.ServiceSources/Config/RawServiceCatalog.cs` — untyped catalog shape used to fish out each service's per-kind raw yaml block.
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs` — two-pass load (typed + raw).
- Create: `src/Aspire.Hosting.ServiceSources/ILocalResourceKind.cs` — the public extension point.
- Create: `src/Aspire.Hosting.ServiceSources/LocalKindConfig.cs` — public helper satellite packages use to parse their opaque config block into a typed options object.
- Create: `src/Aspire.Hosting.ServiceSources/Sources/LocalKindRegistry.cs` — internal per-builder registry of `ILocalResourceKind` by kind name.
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs` — add `AddLocalKind`.
- Create: `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs` — the repo clone/checkout logic extracted out of `LocalProjectSource`.
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs` — dotnet path now calls `LocalGitCheckout`; non-dotnet kinds resolve via the registry instead.
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/PendingLocalResolutions.cs` — dispatch to `AddProject` for dotnet, or to the registered `ILocalResourceKind.Resolve(...)` otherwise.
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs` — new cases for `kind`/kind-specific block parsing.
- Test: `test/Aspire.Hosting.ServiceSources.Tests/LocalKindConfigTests.cs` — new file, tests `LocalKindConfig.Parse<T>`.
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalKindRegistryTests.cs` — new file, tests registration/lookup/duplicate.
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/PendingLocalResolutionsTests.cs` — new cases for the non-dotnet dispatch path.

---

### Task 1: `Kind` and `KindConfig` on `ServiceMetadata`, raw catalog parsing

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Config/RawServiceCatalog.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`

**Interfaces:**
- Produces: `ServiceMetadata.Kind` (`string`, defaults to `"dotnet"`), `ServiceMetadata.KindConfig` (`object?`) — later tasks read both.

- [ ] **Step 1: Write the failing tests**

Add to `ServiceCatalogLoaderTests.cs`:

```csharp
    [Fact]
    public void Load_NoKindSpecified_DefaultsToDotnet()
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

            Assert.Equal("dotnet", catalog.Services["orders"].Kind);
            Assert.Null(catalog.Services["orders"].KindConfig);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CustomKindWithMatchingBlock_CapturesKindAndRawBlock()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              frontend:
                repository: https://github.com/company/frontend
                kind: javascript
                javascript:
                  appDirectory: .
                  runScript: dev
                  packageManager: npm
            """);

        try
        {
            var catalog = ServiceCatalogLoader.Load(path);
            var frontend = catalog.Services["frontend"];

            Assert.Equal("javascript", frontend.Kind);
            Assert.NotNull(frontend.KindConfig);
            var block = Assert.IsAssignableFrom<IDictionary<object, object>>(frontend.KindConfig);
            Assert.Equal(".", block["appDirectory"]);
            Assert.Equal("dev", block["runScript"]);
            Assert.Equal("npm", block["packageManager"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_CustomKindWithoutMatchingBlock_LeavesKindConfigNull()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """
            services:
              frontend:
                repository: https://github.com/company/frontend
                kind: javascript
            """);

        try
        {
            var catalog = ServiceCatalogLoader.Load(path);

            Assert.Equal("javascript", catalog.Services["frontend"].Kind);
            Assert.Null(catalog.Services["frontend"].KindConfig);
        }
        finally
        {
            File.Delete(path);
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceCatalogLoaderTests"`
Expected: the three new tests FAIL (compile error: `Kind`/`KindConfig` don't exist yet).

- [ ] **Step 3: Add `Kind` and `KindConfig` to `ServiceMetadata`**

In `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs`, add:

```csharp
    public string Kind { get; set; } = "dotnet";

    public object? KindConfig { get; set; }
```

- [ ] **Step 4: Create `RawServiceCatalog`**

Create `src/Aspire.Hosting.ServiceSources/Config/RawServiceCatalog.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// Untyped mirror of <see cref="ServiceCatalog"/>, used only to fish the raw yaml mapping for a
/// service's kind-specific block (e.g. the <c>javascript:</c> block) out of the document — core
/// doesn't know the shape of that block, so it can't be captured by <see cref="ServiceMetadata"/>
/// itself. YamlDotNet deserializes each service's remaining unknown keys as
/// <c>Dictionary&lt;object, object&gt;</c> values here because the declared value type is
/// <c>object</c>.
/// </summary>
internal sealed class RawServiceCatalog
{
    public Dictionary<string, Dictionary<string, object>> Services { get; set; } = new();
}
```

- [ ] **Step 5: Update `ServiceCatalogLoader` to do a second, raw pass**

Replace the contents of `src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs` with:

```csharp
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.Hosting.ServiceSources.Config;

internal static class ServiceCatalogLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static ServiceCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service catalog file not found at '{path}'. Expected a 'servicesources.yaml' file in the AppHost project directory.");
        }

        var yaml = File.ReadAllText(path);
        var catalog = Deserializer.Deserialize<ServiceCatalog>(yaml) ?? new ServiceCatalog();
        var raw = Deserializer.Deserialize<RawServiceCatalog>(yaml) ?? new RawServiceCatalog();

        foreach (var (name, metadata) in catalog.Services)
        {
            if (raw.Services.TryGetValue(name, out var rawService) &&
                rawService.TryGetValue(metadata.Kind, out var kindBlock))
            {
                metadata.KindConfig = kindBlock;
            }
        }

        return catalog;
    }
}
```

Note: `IgnoreUnmatchedProperties()` is required here — without it, a `kind`-specific block like
`javascript:` (a key `ServiceMetadata` doesn't declare) would make the *typed* pass throw.
`RawServiceCatalog.Services` values are typed as `Dictionary<string, object>` specifically so
YamlDotNet decodes `kind: javascript` as the string key `"kind"` at the top of that per-service
map — the lookup `rawService.TryGetValue(metadata.Kind, ...)` then finds the sibling block whose
key equals the kind name (e.g. `"javascript"`).

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceCatalogLoaderTests"`
Expected: PASS (all `ServiceCatalogLoaderTests`, including the three new ones and the pre-existing
ones).

- [ ] **Step 7: Run the full test suite**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests`
Expected: PASS, no regressions.

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs \
        src/Aspire.Hosting.ServiceSources/Config/RawServiceCatalog.cs \
        src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs
git commit -m "Add Kind/KindConfig to ServiceMetadata, parse kind-specific yaml block"
```

---

### Task 2: `LocalKindConfig.Parse<T>` helper

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/LocalKindConfig.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/LocalKindConfigTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks (standalone utility over `object?`).
- Produces: `public static class LocalKindConfig { public static T? Parse<T>(object? rawConfig) where T : class; }` — later tasks and satellite packages call this to turn `ServiceMetadata.KindConfig` into a typed options object.

- [ ] **Step 1: Write the failing test**

Create `test/Aspire.Hosting.ServiceSources.Tests/LocalKindConfigTests.cs`:

```csharp
using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.Tests;

public class LocalKindConfigTests
{
    private sealed class Options
    {
        public string? AppDirectory { get; set; }

        public string? RunScript { get; set; }
    }

    [Fact]
    public void Parse_NullConfig_ReturnsNull()
    {
        Assert.Null(LocalKindConfig.Parse<Options>(null));
    }

    [Fact]
    public void Parse_RawDictionary_MapsCamelCaseKeysToProperties()
    {
        var raw = new Dictionary<object, object>
        {
            ["appDirectory"] = ".",
            ["runScript"] = "dev",
        };

        var options = LocalKindConfig.Parse<Options>(raw);

        Assert.NotNull(options);
        Assert.Equal(".", options.AppDirectory);
        Assert.Equal("dev", options.RunScript);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LocalKindConfigTests"`
Expected: FAIL (compile error: `LocalKindConfig` doesn't exist).

- [ ] **Step 3: Implement `LocalKindConfig`**

Create `src/Aspire.Hosting.ServiceSources/LocalKindConfig.cs`:

```csharp
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Parses a service's opaque per-kind config block (<see cref="Config.ServiceMetadata.KindConfig"/>,
/// as handed to <see cref="ILocalResourceKind.Resolve"/>) into a strongly-typed options object.
/// Satellite packages (e.g. a JavaScript or Java local-kind implementation) call this instead of
/// working with the raw <c>Dictionary&lt;object, object&gt;</c> directly.
/// </summary>
public static class LocalKindConfig
{
    private static readonly ISerializer Serializer = new SerializerBuilder().Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="rawConfig"/> is <see langword="null"/>
    /// (i.e. the service's yaml had no block matching its <c>kind</c>). Round-trips
    /// <paramref name="rawConfig"/> back through yaml rather than reflecting over it directly,
    /// since it arrives as an untyped <c>Dictionary&lt;object, object&gt;</c> produced by
    /// YamlDotNet's dynamic deserialization.
    /// </summary>
    public static T? Parse<T>(object? rawConfig) where T : class
    {
        if (rawConfig is null)
        {
            return null;
        }

        var yaml = Serializer.Serialize(rawConfig);
        return Deserializer.Deserialize<T>(yaml);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LocalKindConfigTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/LocalKindConfig.cs \
        test/Aspire.Hosting.ServiceSources.Tests/LocalKindConfigTests.cs
git commit -m "Add LocalKindConfig.Parse<T> helper for satellite packages"
```

---

### Task 3: `ILocalResourceKind` interface

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/ILocalResourceKind.cs`

**Interfaces:**
- Produces: the `ILocalResourceKind` contract every later task (registry, dispatch, and future satellite packages) implements against:
  ```csharp
  public interface ILocalResourceKind
  {
      IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
          IDistributedApplicationBuilder builder,
          string serviceName,
          string repoRoot,
          object? rawConfig);
  }
  ```

This is a pure interface declaration with no independent behavior to unit-test — its contract is
exercised through Task 4 (registry) and Task 6 (dispatch) tests via fakes. No test file for this
task; it's verified indirectly.

- [ ] **Step 1: Create the interface**

Create `src/Aspire.Hosting.ServiceSources/ILocalResourceKind.cs`:

```csharp
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// Turns a cloned/checked-out local repository into a real Aspire resource for one non-dotnet
/// "local" service kind (e.g. JavaScript, Java). Implemented by satellite packages and registered
/// via <see cref="ServiceSourcesBuilderExtensions.AddLocalKind"/>.
/// </summary>
public interface ILocalResourceKind
{
    /// <summary>
    /// <paramref name="repoRoot"/> is the already-resolved local checkout directory (cloning and
    /// ref checkout have already happened by the time this is called). <paramref name="rawConfig"/>
    /// is the service's opaque per-kind yaml block — parse it with
    /// <see cref="LocalKindConfig.Parse{T}"/>.
    /// </summary>
    IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
        IDistributedApplicationBuilder builder,
        string serviceName,
        string repoRoot,
        object? rawConfig);
}
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build src/Aspire.Hosting.ServiceSources`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/ILocalResourceKind.cs
git commit -m "Add ILocalResourceKind extension point interface"
```

---

### Task 4: `LocalKindRegistry` + `AddLocalKind`

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Sources/LocalKindRegistry.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalKindRegistryTests.cs`

**Interfaces:**
- Consumes: `ILocalResourceKind` (Task 3).
- Produces: `internal sealed class LocalKindRegistry { static LocalKindRegistry For(IDistributedApplicationBuilder builder); void Register(string kind, ILocalResourceKind handler); bool TryGet(string kind, out ILocalResourceKind? handler); }` and `public static IDistributedApplicationBuilder AddLocalKind(this IDistributedApplicationBuilder builder, string kind, ILocalResourceKind handler)` — Task 6 (`LocalProjectSource`/`PendingLocalResolutions`) calls `LocalKindRegistry.For(builder).TryGet(...)`.

- [ ] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalKindRegistryTests.cs`:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Sources;
using Aspire.Hosting.ServiceSources.Tests;

namespace Aspire.Hosting.ServiceSources.Tests.Sources;

public class LocalKindRegistryTests
{
    private sealed class FakeKind : ILocalResourceKind
    {
        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }

    private static IDistributedApplicationBuilder CreateBuilder() =>
        TestHelpers.CreateBuilder(Directory.CreateTempSubdirectory().FullName);

    [Fact]
    public void For_SameBuilder_ReturnsSameInstance()
    {
        var builder = CreateBuilder();

        Assert.Same(LocalKindRegistry.For(builder), LocalKindRegistry.For(builder));
    }

    [Fact]
    public void For_DifferentBuilders_ReturnIndependentInstances()
    {
        var builderA = CreateBuilder();
        var builderB = CreateBuilder();
        LocalKindRegistry.For(builderA).Register("javascript", new FakeKind());

        Assert.False(LocalKindRegistry.For(builderB).TryGet("javascript", out _));
    }

    [Fact]
    public void Register_ThenTryGet_ReturnsSameHandler()
    {
        var builder = CreateBuilder();
        var handler = new FakeKind();

        LocalKindRegistry.For(builder).Register("javascript", handler);

        Assert.True(LocalKindRegistry.For(builder).TryGet("javascript", out var found));
        Assert.Same(handler, found);
    }

    [Fact]
    public void TryGet_UnregisteredKind_ReturnsFalse()
    {
        var builder = CreateBuilder();

        Assert.False(LocalKindRegistry.For(builder).TryGet("java", out var found));
        Assert.Null(found);
    }

    [Fact]
    public void Register_SameKindTwice_ThrowsNamingKind()
    {
        var builder = CreateBuilder();
        LocalKindRegistry.For(builder).Register("javascript", new FakeKind());

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => LocalKindRegistry.For(builder).Register("javascript", new FakeKind()));

        Assert.Contains("javascript", ex.Message);
    }

    [Fact]
    public void AddLocalKind_RegistersHandlerRetrievableViaFor()
    {
        var builder = CreateBuilder();
        var handler = new FakeKind();

        builder.AddLocalKind("javascript", handler);

        Assert.True(LocalKindRegistry.For(builder).TryGet("javascript", out var found));
        Assert.Same(handler, found);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LocalKindRegistryTests"`
Expected: FAIL (compile error: `LocalKindRegistry`/`AddLocalKind` don't exist).

- [ ] **Step 3: Implement `LocalKindRegistry`**

Create `src/Aspire.Hosting.ServiceSources/Sources/LocalKindRegistry.cs`:

```csharp
using System.Runtime.CompilerServices;

namespace Aspire.Hosting.ServiceSources.Sources;

/// <summary>
/// Per-builder registry of <see cref="ILocalResourceKind"/> handlers, keyed by the <c>kind</c>
/// name a service's <c>servicesources.yaml</c> entry declares. Populated by satellite packages via
/// <see cref="ServiceSourcesBuilderExtensions.AddLocalKind"/>, consulted by the <c>"local"</c>
/// source for any kind other than the built-in <c>"dotnet"</c>.
/// </summary>
internal sealed class LocalKindRegistry
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, LocalKindRegistry> Cache = new();

    private readonly Dictionary<string, ILocalResourceKind> _handlers = new();

    public static LocalKindRegistry For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static _ => new LocalKindRegistry());

    public void Register(string kind, ILocalResourceKind handler)
    {
        if (!_handlers.TryAdd(kind, handler))
        {
            throw new ServiceSourcesConfigurationException(
                $"Local kind '{kind}' is already registered. Call AddLocalKind for a given kind at most once.");
        }
    }

    public bool TryGet(string kind, out ILocalResourceKind? handler) =>
        _handlers.TryGetValue(kind, out handler);
}
```

- [ ] **Step 4: Add `AddLocalKind` to `ServiceSourcesBuilderExtensions`**

In `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`, add this method to the
`ServiceSourcesBuilderExtensions` class (after `AddService`):

```csharp
    /// <summary>
    /// Registers <paramref name="handler"/> as the resolver for local-sourced services whose
    /// <c>servicesources.yaml</c> entry declares <c>kind: &lt;paramref name="kind"/&gt;</c>.
    /// Called by a satellite package's own registration method (e.g. a hypothetical
    /// <c>UseJavaScript()</c>), not typically called directly by an AppHost author.
    /// </summary>
    public static IDistributedApplicationBuilder AddLocalKind(
        this IDistributedApplicationBuilder builder, string kind, ILocalResourceKind handler)
    {
        Sources.LocalKindRegistry.For(builder).Register(kind, handler);
        return builder;
    }
```

Add `using Aspire.Hosting.ServiceSources.Sources;` is already present in this file (it's used for
`LocalProjectSource` etc.), so no new `using` is needed — reference the type as
`Sources.LocalKindRegistry` only if there's a naming clash; otherwise add
`using Aspire.Hosting.ServiceSources.Sources;` if not already present and call `LocalKindRegistry.For(...)` directly. (Check the top of the file — Task's existing `using` list already includes `Aspire.Hosting.ServiceSources.Sources;`, so just write `LocalKindRegistry.For(builder).Register(kind, handler);`.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LocalKindRegistryTests"`
Expected: PASS

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests`
Expected: PASS, no regressions.

- [ ] **Step 7: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/LocalKindRegistry.cs \
        src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalKindRegistryTests.cs
git commit -m "Add LocalKindRegistry and AddLocalKind"
```

---

### Task 5: Extract `LocalGitCheckout`

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`

**Interfaces:**
- Produces: `internal static class LocalGitCheckout { static string ResolveRepoRoot(string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config, string appHostDirectory, IGitClient gitClient); }` — Task 6 calls this for both the dotnet path and the registry-dispatched path.
- Consumes: nothing new — this is a pure extraction of existing logic in `LocalProjectSource.ResolveProjectPath` (current file, lines 19–103 minus the final project-path-specific two lines).

This is a refactor: behavior must not change. No new tests are written for `LocalGitCheckout` in
isolation — the existing `LocalProjectSourceTests` already exercise every branch of this logic
through `LocalProjectSource.ResolveProjectPath`, and they must keep passing unchanged after the
extraction (characterization-test style).

- [ ] **Step 1: Run the existing tests to confirm the baseline is green**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LocalProjectSourceTests"`
Expected: PASS (establishes the baseline before refactoring).

- [ ] **Step 2: Create `LocalGitCheckout` with the extracted logic**

Create `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs`. Move into it everything from
`LocalProjectSource.ResolveProjectPath` up to (but not including) the final
`var projectPath = Path.Combine(repoRoot, metadata.Project); ...; return projectPath;` lines, plus
the private helpers it uses (`CheckoutWithFetchRetry`, `EnsureGitignore`, `RepositoryUrlsMatch`,
`NormalizeRepositoryUrl`):

```csharp
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Git;

/// <summary>
/// Resolves a service's local checkout directory — cloning it if necessary, checking out the
/// configured ref — shared by every local-source kind (the built-in <c>dotnet</c> kind and any
/// kind registered via <see cref="ILocalResourceKind"/>). Language-agnostic: this never looks at
/// how the resulting checkout is actually run.
/// </summary>
internal static class LocalGitCheckout
{
    public static string ResolveRepoRoot(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        string appHostDirectory,
        IGitClient gitClient)
    {
        string repoRoot;

        if (config.Path is not null)
        {
            if (config.Ref is not null)
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{serviceName}': 'ref' cannot be combined with 'path' — 'path' points directly at " +
                    "an existing checkout, and 'ref' only applies when this tool manages the clone.");
            }

            // Anchor a relative `path` override to the AppHost directory (matching Aspire's own
            // AddProject behavior), not to the process's current working directory.
            // Path.GetFullPath is a no-op when config.Path is already absolute.
            repoRoot = Path.GetFullPath(config.Path, appHostDirectory);
        }
        else
        {
            EnsureGitignore(appHostDirectory);
            repoRoot = Path.Combine(appHostDirectory, ".servicesources", "checkouts", serviceName);
            var reference = config.Ref ?? metadata.DefaultRef;

            if (!Directory.Exists(Path.Combine(repoRoot, ".git")))
            {
                try
                {
                    gitClient.Clone(metadata.Repository, repoRoot);
                }
                catch (Exception ex)
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{serviceName}': failed to clone repository '{metadata.Repository}' into '{repoRoot}'.", ex);
                }

                if (reference is not null)
                {
                    CheckoutWithFetchRetry(serviceName, metadata, repoRoot, reference, gitClient);
                }
            }
            else
            {
                var existingOrigin = gitClient.GetOriginUrl(repoRoot);
                if (existingOrigin is not null && !RepositoryUrlsMatch(existingOrigin, metadata.Repository))
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{serviceName}': checkout at '{repoRoot}' already contains a clone of " +
                        $"'{existingOrigin}', which does not match the configured repository '{metadata.Repository}'. " +
                        "Remove the checkout directory or fix the configured repository URL.");
                }

                if (reference is not null)
                {
                    if (gitClient.HasUncommittedChanges(repoRoot))
                    {
                        if (!gitClient.IsRefCheckedOut(repoRoot, reference))
                        {
                            throw new ServiceSourcesConfigurationException(
                                $"Service '{serviceName}': checkout at '{repoRoot}' has uncommitted changes and is not " +
                                $"on the configured ref '{reference}'. Commit or stash your changes, then re-run.");
                        }
                    }
                    else if (!gitClient.IsRefCheckedOut(repoRoot, reference))
                    {
                        CheckoutWithFetchRetry(serviceName, metadata, repoRoot, reference, gitClient);
                    }
                }
            }
        }

        return repoRoot;
    }

    private static void CheckoutWithFetchRetry(
        string serviceName, ServiceMetadata metadata, string repoRoot, string reference, IGitClient gitClient)
    {
        try
        {
            gitClient.Checkout(repoRoot, reference);
            return;
        }
        catch (ServiceSourcesConfigurationException)
        {
            // Ref not resolvable from local data; fall through to fetch-and-retry below.
        }
        catch (Exception ex)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': failed to checkout ref '{reference}' of repository '{metadata.Repository}' at '{repoRoot}'.", ex);
        }

        try
        {
            gitClient.Fetch(repoRoot);
        }
        catch (Exception ex)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': failed to fetch repository '{metadata.Repository}' at '{repoRoot}' " +
                $"while resolving ref '{reference}'.", ex);
        }

        try
        {
            gitClient.Checkout(repoRoot, reference);
        }
        catch (Exception ex)
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': failed to checkout ref '{reference}' of repository '{metadata.Repository}' at '{repoRoot}'.", ex);
        }
    }

    private static void EnsureGitignore(string appHostDirectory)
    {
        var dir = Path.Combine(appHostDirectory, ".servicesources");
        Directory.CreateDirectory(dir);

        var gitignorePath = Path.Combine(dir, ".gitignore");
        try
        {
            // FileMode.CreateNew is atomic: it fails if the file already exists, which makes
            // this safe against concurrent resolution of multiple services (see
            // PendingLocalResolutions, which resolves them in parallel) racing to create it.
            using var stream = new FileStream(gitignorePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write("*\n!.gitignore\n");
        }
        catch (IOException)
        {
            // Already created by a concurrent resolution or a prior run — leave it as-is.
        }
    }

    private static bool RepositoryUrlsMatch(string a, string b) =>
        string.Equals(NormalizeRepositoryUrl(a), NormalizeRepositoryUrl(b), StringComparison.Ordinal);

    private static string NormalizeRepositoryUrl(string repositoryUrl)
    {
        var trimmed = repositoryUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        // Normalize both URL forms (https://host/path) and scp-like SSH syntax
        // ([user@]host:path, e.g. git@github.com:example/orders) down to "host/path"
        // so an HTTPS remote and an SSH remote for the same repository compare equal.
        var schemeIndex = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex >= 0)
        {
            trimmed = trimmed[(schemeIndex + 3)..];
            var slashIndex = trimmed.IndexOf('/');
            var atIndex = trimmed.IndexOf('@');
            if (atIndex >= 0 && (slashIndex < 0 || atIndex < slashIndex))
            {
                trimmed = trimmed[(atIndex + 1)..];
            }
        }
        else
        {
            var colonIndex = trimmed.IndexOf(':');
            var slashIndex = trimmed.IndexOf('/');
            if (colonIndex >= 0 && (slashIndex < 0 || colonIndex < slashIndex))
            {
                var host = trimmed[..colonIndex];
                var atIndex = host.IndexOf('@');
                if (atIndex >= 0)
                {
                    host = host[(atIndex + 1)..];
                }

                trimmed = $"{host}/{trimmed[(colonIndex + 1)..]}";
            }
        }

        return trimmed.TrimEnd('/');
    }
}
```

- [ ] **Step 3: Rewrite `LocalProjectSource.ResolveProjectPath` to call `LocalGitCheckout`**

In `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`, replace the body of
`ResolveProjectPath` (everything from `string repoRoot;` down to, but not including, the final
`var projectPath = ...` block) with a single call, and delete the now-moved private helper methods
(`CheckoutWithFetchRetry`, `EnsureGitignore`, `RepositoryUrlsMatch`, `NormalizeRepositoryUrl`) from
this file:

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
        var facade = ServiceResource.CreateEmptyFacade(builder, serviceName);

        PendingLocalResolutions.For(builder).Add(new PendingResolution(serviceName, metadata, config, facade, gitClient));

        return facade;
    }

    internal static string ResolveProjectPath(
        string serviceName,
        ServiceMetadata metadata,
        ServiceDeveloperConfig config,
        string appHostDirectory,
        IGitClient gitClient)
    {
        var repoRoot = LocalGitCheckout.ResolveRepoRoot(serviceName, metadata, config, appHostDirectory, gitClient);

        var projectPath = Path.Combine(repoRoot, metadata.Project);
        if (!File.Exists(projectPath))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{serviceName}': project file '{metadata.Project}' was not found under '{repoRoot}'.");
        }

        return projectPath;
    }
}
```

- [ ] **Step 4: Run the existing tests to confirm they still pass unchanged**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~LocalProjectSourceTests"`
Expected: PASS — same tests as Step 1, now exercising the extracted `LocalGitCheckout` indirectly.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests`
Expected: PASS, no regressions.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs \
        src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs
git commit -m "Extract LocalGitCheckout out of LocalProjectSource"
```

---

### Task 6: Dispatch non-dotnet kinds through the registry

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/PendingLocalResolutions.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/PendingLocalResolutionsTests.cs`

**Interfaces:**
- Consumes: `LocalKindRegistry.For(builder).TryGet(kind, out handler)` (Task 4),
  `LocalGitCheckout.ResolveRepoRoot(...)` (Task 5), `ILocalResourceKind.Resolve(...)` (Task 3),
  `ServiceMetadata.Kind`/`KindConfig` (Task 1).
- Produces: the observable end-to-end behavior — a `"local"`-sourced service whose `kind` is
  `"dotnet"` (or unset) resolves exactly as before; a service with any other registered `kind`
  resolves via that kind's handler; a service with an unregistered `kind` throws
  `ServiceSourcesConfigurationException`.

- [ ] **Step 1: Write the failing tests**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Sources/PendingLocalResolutionsTests.cs` (inside
the existing `PendingLocalResolutionsTests` class, after the existing fakes/helpers):

```csharp
    private sealed class FakeLocalResourceKind : ILocalResourceKind
    {
        public List<(string ServiceName, string RepoRoot, object? RawConfig)> Calls { get; } = [];

        public IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
            IDistributedApplicationBuilder builder, string serviceName, string repoRoot, object? rawConfig)
        {
            Calls.Add((serviceName, repoRoot, rawConfig));
            return ServiceResource.CreateEmptyFacade(builder, serviceName);
        }
    }

    private static ServiceMetadata MetadataWithKind(string repository, string kind, object? kindConfig = null) =>
        new() { Repository = repository, Kind = kind, KindConfig = kindConfig };

    [Fact]
    public async Task ResolveAllAsync_RegisteredNonDotnetKind_DispatchesToHandlerWithResolvedRepoRoot()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        var handler = new FakeLocalResourceKind();
        builder.AddLocalKind("javascript", handler);
        var facade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        var kindConfig = new Dictionary<object, object> { ["appDirectory"] = "." };
        PendingLocalResolutions.For(builder).Add(new PendingResolution(
            "frontend", MetadataWithKind("https://fake/frontend", "javascript", kindConfig), DevConfig(), facade, new FakeGitClient()));

        await PublishBeforeStartEventAsync(builder);

        var call = Assert.Single(handler.Calls);
        Assert.Equal("frontend", call.ServiceName);
        Assert.EndsWith(Path.Combine(".servicesources", "checkouts", "frontend"), call.RepoRoot);
        Assert.Same(kindConfig, call.RawConfig);
    }

    [Fact]
    public async Task ResolveAllAsync_UnregisteredNonDotnetKind_ThrowsNamingServiceAndKind()
    {
        var builder = CreateBuilder(CreateAppHostDirectory());
        var facade = ServiceResource.CreateEmptyFacade(builder, "frontend");
        PendingLocalResolutions.For(builder).Add(new PendingResolution(
            "frontend", MetadataWithKind("https://fake/frontend", "javascript"), DevConfig(), facade, new FakeGitClient()));

        var ex = await Assert.ThrowsAsync<ServiceSourcesConfigurationException>(
            () => PublishBeforeStartEventAsync(builder));

        Assert.Contains("frontend", ex.Message);
        Assert.Contains("javascript", ex.Message);
    }
```

Also add the necessary `using Aspire.Hosting.ServiceSources;` at the top of the test file if not
already present (it is — `PendingLocalResolutionsTests.cs` already has it).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~PendingLocalResolutionsTests"`
Expected: the two new tests FAIL — `ResolveAllAsync_RegisteredNonDotnetKind_...` fails because
`ResolveAllAsync` still unconditionally calls `builder.AddProject(...)`, which will throw or
misbehave for a service with no `Project` set; `ResolveAllAsync_UnregisteredNonDotnetKind_...`
fails for the same reason instead of throwing the expected message. Every pre-existing test in this
file must still PASS (they all use the default `Kind = "dotnet"` via the existing `Metadata(...)`
helper, which is untouched).

- [ ] **Step 3: Update `PendingLocalResolutions` to dispatch by kind**

In `src/Aspire.Hosting.ServiceSources/Sources/PendingLocalResolutions.cs`, replace `ResolveOne` and
`ResolveAllAsync`'s resource-creation loop. The new `ResolveOne` resolves only the repo root (shared
by every kind) instead of a dotnet-specific project path; the final resource creation (dotnet
`AddProject` vs. registry dispatch) moves into `ResolveAllAsync`, since that's where `builder` is
available for the registry lookup:

```csharp
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources.Config;
using Aspire.Hosting.ServiceSources.Git;

namespace Aspire.Hosting.ServiceSources.Sources;

internal sealed record PendingResolution(
    string ServiceName,
    ServiceMetadata Metadata,
    ServiceDeveloperConfig Config,
    IResourceBuilder<IResourceWithServiceDiscovery> Facade,
    IGitClient GitClient);

internal sealed class PendingLocalResolutions
{
    private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, PendingLocalResolutions> Cache = new();

    private readonly List<PendingResolution> _pending = [];
    private bool _resolutionStarted;

    public static PendingLocalResolutions For(IDistributedApplicationBuilder builder) =>
        Cache.GetValue(builder, static b =>
        {
            var store = new PendingLocalResolutions();
            b.Eventing.Subscribe<BeforeStartEvent>((_, ct) => store.ResolveAllAsync(b, ct));
            return store;
        });

    public void Add(PendingResolution pending)
    {
        if (_resolutionStarted)
        {
            throw new ServiceSourcesConfigurationException(
                $"Cannot register 'local'-sourced service '{pending.ServiceName}' because BeforeStartEvent has already " +
                "fired and pending 'local' services have already been resolved. All AddService calls for 'local' sources " +
                "must happen before the app host starts.");
        }

        _pending.Add(pending);
    }

    private async Task ResolveAllAsync(IDistributedApplicationBuilder builder, CancellationToken cancellationToken)
    {
        _resolutionStarted = true;

        var results = await Task.WhenAll(_pending.Select(pending =>
            Task.Run(() => ResolveOne(pending, builder.AppHostDirectory), cancellationToken)));

        var failures = results.Where(r => r.Exception is not null).ToArray();
        if (failures.Length > 0)
        {
            throw AggregateFailures(failures);
        }

        foreach (var result in results)
        {
            var pending = result.Pending;
            if (pending.Metadata.Kind == "dotnet")
            {
                var projectPath = Path.Combine(result.RepoRoot!, pending.Metadata.Project);
                if (!File.Exists(projectPath))
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{pending.ServiceName}': project file '{pending.Metadata.Project}' was not found under '{result.RepoRoot}'.");
                }

                var projectBuilder = builder.AddProject(pending.ServiceName, projectPath);
                ServiceResource.CopyEndpointAnnotations(pending.Facade, projectBuilder);
                continue;
            }

            if (!LocalKindRegistry.For(builder).TryGet(pending.Metadata.Kind, out var handler))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{pending.ServiceName}': kind '{pending.Metadata.Kind}' is not registered. " +
                    "Add the satellite package for this kind and call its registration method " +
                    "(e.g. builder.UseJavaScript()) before this service is resolved.");
            }

            var resourceBuilder = handler!.Resolve(builder, pending.ServiceName, result.RepoRoot!, pending.Metadata.KindConfig);
            ServiceResource.CopyEndpointAnnotations(pending.Facade, resourceBuilder);
        }
    }

    private static ResolutionResult ResolveOne(PendingResolution pending, string appHostDirectory)
    {
        try
        {
            var repoRoot = LocalGitCheckout.ResolveRepoRoot(
                pending.ServiceName, pending.Metadata, pending.Config, appHostDirectory, pending.GitClient);
            return new ResolutionResult(pending, repoRoot, null);
        }
        catch (Exception ex)
        {
            return new ResolutionResult(pending, null, ex);
        }
    }

    private static ServiceSourcesConfigurationException AggregateFailures(IReadOnlyCollection<ResolutionResult> failures)
    {
        var lines = failures.Select(f => f.Exception!.InnerException is not null
            ? $"  - {f.Exception.Message} ({f.Exception.InnerException.Message})"
            : $"  - {f.Exception.Message}");
        var message = "Failed to resolve one or more 'local'-sourced services:" + Environment.NewLine +
            string.Join(Environment.NewLine, lines);
        return new ServiceSourcesConfigurationException(message, failures.First().Exception!);
    }

    private readonly record struct ResolutionResult(PendingResolution Pending, string? RepoRoot, Exception? Exception);
}
```

Note what changed from the pre-refactor version: `ResolveOne` now calls
`LocalGitCheckout.ResolveRepoRoot` (kind-agnostic) instead of
`LocalProjectSource.ResolveProjectPath` (dotnet-specific), and `ResolutionResult.ProjectPath` is
renamed `RepoRoot` to match. The dotnet-specific project-file lookup that used to live inside
`LocalProjectSource.ResolveProjectPath` moves into the `pending.Metadata.Kind == "dotnet"` branch of
`ResolveAllAsync` — this is the one place in this task where dotnet's behavior is preserved exactly,
just relocated.

- [ ] **Step 4: Run the new tests to verify they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~PendingLocalResolutionsTests"`
Expected: PASS — both new tests and every pre-existing test in this file.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests`
Expected: PASS, no regressions. In particular confirm
`test/Aspire.Hosting.ServiceSources.Tests/AddServiceIntegrationTests.cs` and
`test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs` (which exercise the `dotnet` path
end-to-end through `AddService`) still pass unchanged.

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/PendingLocalResolutions.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Sources/PendingLocalResolutionsTests.cs
git commit -m "Dispatch non-dotnet local kinds through LocalKindRegistry"
```

---

## Out of scope for this plan

- The `KoalaSoft.Aspire.Hosting.ServiceSources.JavaScript` and `...Java` satellite packages
  themselves (tracked as #44 and #45) — this plan only builds the extension point they'll register
  against.
- `[AspireExport]` on `AddLocalKind` or any `Use*` satellite method for TypeScript AppHost support
  (tracked against #42) — noted in the spec's "TypeScript/guest-language AppHost compatibility"
  section as a one-line addition per method, to be added when each satellite package (or #42
  itself) is implemented.
- `README.md` documentation of the `kind`/`AddLocalKind` mechanism — worth adding once at least one
  satellite package exists to document as a concrete example, rather than documenting an
  extension point with no real implementations of it yet.
