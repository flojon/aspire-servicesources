# Nested per-source developer config — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure the developer config so each source's fields live in a block named for that source, letting a higher configuration layer switch a service's source without the old source's fields surviving the merge ([#161]).

**Architecture:** `servicesources.local.json` entries gain per-source sub-objects (`local`, `url`, `kubernetes`, `container`). Only the block named by `source` is read, so a stale block sits present-but-unread instead of failing validation. The old cross-source field check becomes impossible to violate and is replaced by a shape-driven unknown-key walk, derived by reflection from the bound types themselves, which runs once at config read time. A normalization pass maps blank strings to absent so a higher layer can also *drop* an inherited field.

**Tech Stack:** C#, .NET (multi-targets net8.0/net9.0/net10.0), `Microsoft.Extensions.Configuration` + reflection-based binder, xunit.

**Spec:** `docs/superpowers/specs/2026-08-31-nested-developer-config-design.md`

## Global Constraints

- **The package multi-targets net8.0.** No `System.Threading.Lock`, no net9+-only APIs. Existing code notes this explicitly.
- **Reflection is permitted.** `Directory.Build.props` sets no trimming or AOT properties, and the configuration binder already in use is reflection-based.
- **Breaking changes need a CHANGELOG entry.** While the version is below `1.0.0` a breaking change ships in a minor release with a **Breaking** entry under `[Unreleased]` saying what breaks and how to migrate.
- **Comment what the code does, not what the change did.** No changelog-style commentary in code comments. Verbosity is fine; narrating the diff is not.
- **Configuration keys are case-insensitive.** Every key set compared against configuration must use `StringComparer.OrdinalIgnoreCase`.
- **No backwards compatibility with the flat shape.** It is not read at all; it must produce an error, never silent acceptance.

## Prerequisite (not part of this plan)

[#157]'s branch must take `main` before Task 1 starts. [#148] rewrote `Git/LocalGitCheckout.cs` and `Sources/LocalProjectSource.cs`, which this plan modifies. The file sets overlap only in `README.md` and `CHANGELOG.md`, so conflicts are text. This step needs a rebase-and-force-push or a merge of a branch under review and is the repository owner's to perform.

Verify before starting: `git log --oneline -1 origin/main` is contained in the working branch's history.

---

### Task 1: Nest the model

Restructure `ServiceDeveloperConfig` into per-source blocks and update every consumer. The old validator becomes meaningless — a field belonging to another source can no longer bind — so it is deleted here and replaced in Task 2.

After this task the codebase has **no** unknown-key validation. That gap is deliberate and closed by Task 2.

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/LocalDeveloperConfig.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Config/UrlDeveloperConfig.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Config/KubernetesDeveloperConfig.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Config/ContainerDeveloperConfig.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs`
- Delete: `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfigValidator.cs`
- Delete: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceDeveloperConfigValidatorTests.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/IServiceSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/UrlSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/KubernetesSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ContainerSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigurationTests.cs`
- Test (construct `ServiceDeveloperConfig` directly — the compiler will point at every line): `test/Aspire.Hosting.ServiceSources.Tests/ServiceConfigurationExportsTests.cs`, `ServiceConfigurationExtensionsTests.cs`, `Sources/ContainerSourceTests.cs`, `Sources/KubernetesSourceTests.cs`, `Sources/LocalCheckoutPrefetchTests.cs`, `Sources/LocalProjectSourceTests.cs`, `Sources/UrlSourceTests.cs`
- Test (JSON fixtures with flat fields — the compiler will *not* point at these, so grep for them): `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs`, `AddServiceIntegrationTests.cs`, `ContainerConsumerTests.cs`, `Config/DeveloperConfigurationTests.cs`, `Config/ServiceSourcesConfigCacheTests.cs`, `test/Aspire.Hosting.ServiceSources.Java.Tests/UseJavaTests.cs`, `test/Aspire.Hosting.ServiceSources.JavaScript.Tests/UseJavaScriptTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `ServiceDeveloperConfig` with non-null block properties `Local` (`LocalDeveloperConfig`: `string? Path`, `string? Ref`), `Url` (`UrlDeveloperConfig`: `string? Url`), `Kubernetes` (`KubernetesDeveloperConfig`: `string? Context`, `string? Namespace`, `int? Port`), `Container` (`ContainerDeveloperConfig`: `string? Tag`), plus the existing `string Source`. `IServiceSource` no longer declares `RelevantFields`.

- [x] **Step 1: Write the failing acceptance test for #161**

Add to `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigurationTests.cs`. This is the regression test for the whole change: a base file pointing at a URL, a higher layer switching to a local checkout, and the stale `url` block sitting harmlessly unread.

```csharp
    private const string SwitcherCatalog = """
        services:
          switcher:
            repository: https://github.com/company/switcher
            project: Switcher.csproj
        """;

    /// <remarks>
    /// Environment variables are process-global and xunit runs test classes in parallel, so the
    /// service this test names must be one no other test uses.
    /// </remarks>
    [Fact]
    public void ResolveService_HigherLayerSwitchesSource_LeavesTheOldSourcesBlockUnread()
    {
        var checkout = Directory.CreateTempSubdirectory().FullName;
        var dir = CreateAppHostDirectory(
            SwitcherCatalog,
            """
            { "services": { "switcher": {
                "source": "url",
                "url": { "url": "http://from-local-json.invalid" } } } }
            """);

        Environment.SetEnvironmentVariable("ServiceSources__Services__switcher__Source", "local");
        Environment.SetEnvironmentVariable("ServiceSources__Services__switcher__Local__Path", checkout);
        try
        {
            var builder = CreateBuilder(dir);

            var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "switcher");

            Assert.Equal("local", config.Source);
            Assert.Equal(checkout, config.Local.Path);

            // Still bound, and that is the point: the entry it came from is untouched, and nothing
            // reads it while the effective source is "local".
            Assert.Equal("http://from-local-json.invalid", config.Url.Url);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ServiceSources__Services__switcher__Source", null);
            Environment.SetEnvironmentVariable("ServiceSources__Services__switcher__Local__Path", null);
        }
    }
```

- [x] **Step 2: Run it to verify it fails**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~HigherLayerSwitchesSource" -f net10.0`

Expected: FAIL to compile — `ServiceDeveloperConfig` has no member `Local`.

- [x] **Step 3: Create the four block types**

`src/Aspire.Hosting.ServiceSources/Config/LocalDeveloperConfig.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for the <c>"local"</c> source, read from the <c>local</c> block of a
/// service's entry. Bound only when that is the entry's effective source.
/// </summary>
internal sealed class LocalDeveloperConfig
{
    /// <summary>An existing checkout to use as-is, instead of one this tool clones and manages.</summary>
    public string? Path { get; set; }

    /// <summary>The ref a managed checkout sits on. Cannot be combined with <see cref="Path"/>.</summary>
    public string? Ref { get; set; }
}
```

`src/Aspire.Hosting.ServiceSources/Config/UrlDeveloperConfig.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for the <c>"url"</c> source, read from the <c>url</c> block of a
/// service's entry. Bound only when that is the entry's effective source.
/// </summary>
internal sealed class UrlDeveloperConfig
{
    /// <summary>Overrides the catalog's <c>url.url</c> for this service.</summary>
    public string? Url { get; set; }
}
```

`src/Aspire.Hosting.ServiceSources/Config/KubernetesDeveloperConfig.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for the <c>"kubernetes"</c> source, read from the <c>kubernetes</c>
/// block of a service's entry. Bound only when that is the entry's effective source.
/// </summary>
internal sealed class KubernetesDeveloperConfig
{
    /// <summary>The kubectl context the port-forward runs against. Required by this source.</summary>
    public string? Context { get; set; }

    /// <summary>The namespace the service lives in. Defaults to <c>default</c>.</summary>
    public string? Namespace { get; set; }

    /// <summary>The port inside the cluster, overriding the catalog's <c>kubernetes.port</c>.</summary>
    public int? Port { get; set; }
}
```

`src/Aspire.Hosting.ServiceSources/Config/ContainerDeveloperConfig.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// The developer's settings for the <c>"container"</c> source, read from the <c>container</c> block
/// of a service's entry. Bound only when that is the entry's effective source.
/// </summary>
internal sealed class ContainerDeveloperConfig
{
    /// <summary>Overrides the catalog's <c>container.defaultTag</c> for this service.</summary>
    public string? Tag { get; set; }
}
```

- [x] **Step 4: Nest `ServiceDeveloperConfig`**

Replace the whole body of `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfig.cs`:

```csharp
namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// One service's entry in the developer config. Each source's settings live in a block named for
/// that source, so only the block <see cref="Source"/> names is ever read.
/// </summary>
/// <remarks>
/// The nesting is what makes a source switchable from a higher configuration layer.
/// <see cref="IConfiguration"/> merges layers per key rather than per object, so with the settings
/// flat on this type a lower layer's <c>url</c> would survive a higher layer setting
/// <c>source: local</c> and land here alongside it. Under a block it still survives, but nothing
/// reads it.
///
/// The blocks are never null. An entry naming a source with no block of its own is the common case
/// — <c>{ "source": "local" }</c> is a complete entry — and an absent block and an empty one mean
/// the same thing, so consumers read through them without a null check.
/// </remarks>
internal sealed class ServiceDeveloperConfig
{
    public string Source { get; set; } = "";

    public LocalDeveloperConfig Local { get; set; } = new();

    public UrlDeveloperConfig Url { get; set; } = new();

    public KubernetesDeveloperConfig Kubernetes { get; set; } = new();

    public ContainerDeveloperConfig Container { get; set; } = new();
}
```

Add `using Microsoft.Extensions.Configuration;` only if the `<see cref="IConfiguration"/>` reference fails to resolve; otherwise reword the remark to plain `IConfiguration` text.

- [x] **Step 5: Delete the old validator and every test of it**

```bash
git rm src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfigValidator.cs
git rm test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceDeveloperConfigValidatorTests.cs
```

Also delete `AddService_ContainerSourceWithForeignPortField_ThrowsNamingServiceFieldAndSource` from `test/Aspire.Hosting.ServiceSources.Tests/AddServiceTests.cs` (the last test in the file). It asserts the old cross-source rule — that `port` is rejected *because the source is `container`* — which no longer exists as a concept. Task 2 replaces it with a test of the new rule.

Leaving it in place fails Step 9 of this task, and would still fail after Task 2: the new message names the block the field belongs to, which for `port` is `kubernetes`, so its `Assert.Contains("container", ex.Message)` can never pass.

The cross-source check is now unviolatable by shape. Do not try to preserve it.

- [x] **Step 6: Remove `RelevantFields` from the interface and the four sources**

In `src/Aspire.Hosting.ServiceSources/IServiceSource.cs`, delete the `RelevantFields` property and its doc comment, leaving only `Resolve`.

Delete this line from each of `Sources/LocalProjectSource.cs`, `Sources/UrlSource.cs`, `Sources/KubernetesSource.cs`, `Sources/ContainerSource.cs` (the field set differs per file):

```csharp
    public IReadOnlySet<string> RelevantFields { get; } = new HashSet<string> { ... };
```

In `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`, delete the validator call from `AddService`:

```csharp
        ServiceDeveloperConfigValidator.Validate(name, developerConfig.Source, source.RelevantFields, developerConfig);
```

- [x] **Step 7: Point the consumers at their blocks**

`src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs` — in `PrepareRepoRoot`, `config.Path` becomes `config.Local.Path` (three occurrences: the `is not null` test, the comment, and the `Path.GetFullPath` argument) and `config.Ref` becomes `config.Local.Ref` (the `is not null` test). In `ConfiguredReference`, `config.Ref ?? metadata.DefaultRef` becomes `config.Local.Ref ?? metadata.DefaultRef`.

`src/Aspire.Hosting.ServiceSources/Sources/UrlSource.cs` — in `ResolveUrl`:

```csharp
        var rawUrl = config.Url.Url ?? metadata.Url?.Url;
```

`src/Aspire.Hosting.ServiceSources/Sources/KubernetesSource.cs` — `config.Context` becomes `config.Kubernetes.Context` (the `IsNullOrWhiteSpace` guard and the `"--context"` argument), `config.Port` becomes `config.Kubernetes.Port`, `config.Namespace` becomes `config.Kubernetes.Namespace`.

`src/Aspire.Hosting.ServiceSources/Sources/ContainerSource.cs`:

```csharp
        var tag = string.IsNullOrWhiteSpace(config.Container.Tag) ? metadata.Container.DefaultTag : config.Container.Tag;
```

`Sources/LocalProjectSource.cs` passes `config` through without reading fields; only its `RelevantFields` line goes.

- [x] **Step 8: Build and fix the remaining compile errors in tests**

Run: `dotnet build ServiceSources.slnx`

Every error is one of two mechanical shapes. Object initializers:

```csharp
// before
new ServiceDeveloperConfig { Source = "local", Path = dir }
// after
new ServiceDeveloperConfig { Source = "local", Local = new() { Path = dir } }
```

and JSON literals in test fixtures:

```csharp
// before
{ "services": { "orders": { "source": "local", "path": "..." } } }
// after
{ "services": { "orders": { "source": "local", "local": { "path": "..." } } } }
```

Note the helper in `Sources/UrlSourceTests.cs`, which is target-typed and easy to miss when reading for `new ServiceDeveloperConfig`:

```csharp
// before
    private static ServiceDeveloperConfig DevConfig(string? urlOverride = null) =>
        new() { Source = "url", Url = urlOverride };
// after
    private static ServiceDeveloperConfig DevConfig(string? urlOverride = null) =>
        new() { Source = "url", Url = new() { Url = urlOverride } };
```

**The JSON fixtures will not raise a compile error.** After Task 1 a flat `"path"` in a test fixture binds to nothing and, with no validator yet, does so silently — the test then fails somewhere confusing, or worse, still passes for the wrong reason. Find them all before running anything:

```bash
grep -rn '"source"' test --include=*.cs
```

Every hit whose entry carries a field beside `source` needs that field moved into a block. Environment-variable names in tests gain a block segment too: `ServiceSources__Services__x__Path` becomes `ServiceSources__Services__x__Local__Path`.

- [x] **Step 9: Run the full suite**

Run: `dotnet test ServiceSources.slnx -f net10.0`

Expected: PASS, including `ResolveService_HigherLayerSwitchesSource_LeavesTheOldSourcesBlockUnread`.

- [x] **Step 10: Commit**

```bash
git add -A
git commit -m "Nest each source's developer settings under a block of its own (#161)"
```

---

### Task 2: Shape-driven unknown-key validation

Restore strictness, widened: any key that does not correspond to a property of the bound types is rejected, at read time, for every entry. This replaces the deleted cross-source check and gives the old flat shape a precise error.

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfigShape.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfigValidator.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfiguration.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs` (Step 6)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceDeveloperConfigValidatorTests.cs` (new file, same path as the one Task 1 deleted)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceSourcesConfigCacheTests.cs` (Step 6)

**Interfaces:**
- Consumes: `ServiceDeveloperConfig` and the four block types from Task 1.
- Produces: `ServiceDeveloperConfigShape.RootKeys` (`IReadOnlySet<string>`), `.BlockFields` (`IReadOnlyDictionary<string, IReadOnlySet<string>>`, block name → field names), `.Blocks` (`IReadOnlyList<PropertyInfo>`, the block properties of `ServiceDeveloperConfig`), `.HomeBlockOf(string field)` returning `string?`; and `ServiceDeveloperConfigValidator.Validate(string serviceName, IConfigurationSection entry)`. Task 3 consumes `.Blocks`.

- [x] **Step 1: Write the failing tests**

Create `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceDeveloperConfigValidatorTests.cs`:

```csharp
using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Config;

/// <summary>
/// Keys are checked against the shape of the bound types, so a key that would bind to nothing is
/// reported rather than silently dropped. Every block is checked, not only the one the entry's
/// source names.
/// </summary>
public class ServiceDeveloperConfigValidatorTests
{
    private const string Catalog = """
        services:
          orders:
            repository: https://github.com/company/orders
            project: Orders.csproj
        """;

    private static string CreateAppHostDirectory(string json)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), Catalog);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"), json);
        return dir;
    }

    private static ServiceSourcesConfigurationException Load(string json)
    {
        var builder = TestHelpers.CreateBuilder(CreateAppHostDirectory(json));
        return Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));
    }

    [Fact]
    public void Validate_FlatFieldAtEntryRoot_NamesTheBlockItBelongsUnder()
    {
        var ex = Load("""{ "services": { "orders": { "source": "local", "path": "/src/orders" } } }""");

        Assert.Contains("'path' is not a valid key here", ex.Message);
        Assert.Contains("'local' block", ex.Message);
    }

    /// <remarks>
    /// The flat shape's worst case, because the field's old flat name is also a block name: the key
    /// is valid at this level, so only its scalar value gives it away. It binds to nothing, so
    /// letting it through would resolve the service off the catalog's url as though nothing were
    /// wrong.
    /// </remarks>
    [Fact]
    public void Validate_FlatValueWrittenAtABlockKey_IsRejected()
    {
        var ex = Load("""{ "services": { "orders": { "source": "url", "url": "https://orders.invalid" } } }""");

        Assert.Contains("'url' is not a valid key here", ex.Message);
    }

    /// <remarks>
    /// Replaces AddServiceTests.AddService_ContainerSourceWithForeignPortField_…, which Task 1
    /// deleted. The rule it guards has changed: a stray field is now rejected for not being a valid
    /// key anywhere at this level, not for belonging to a source other than the entry's, so the
    /// message names the block 'port' belongs in and says nothing about the entry's own source.
    /// </remarks>
    [Fact]
    public void Validate_FlatFieldBelongingToAnotherSource_NamesThatSourcesBlock()
    {
        var ex = Load("""{ "services": { "orders": { "source": "container", "port": 9090 } } }""");

        Assert.Contains("orders", ex.Message);
        Assert.Contains("'port' is not a valid key here", ex.Message);
        Assert.Contains("'kubernetes' block", ex.Message);

        // Naming a source to switch to would be advice to change what the service resolves to.
        Assert.DoesNotContain("\"source\"", ex.Message);
    }

    /// <remarks>
    /// The widening this change makes deliberately: 'orders' is well formed and is the service being
    /// resolved, while the malformed entry names a service nothing asks for. Validation runs over
    /// every entry when the config is read, so it is still reported.
    /// </remarks>
    [Fact]
    public void Validate_MalformedEntryForAnotherService_StillFailsTheLoad()
    {
        var ex = Load("""
            { "services": {
                "orders": { "source": "local" },
                "unused": { "source": "local", "path": "/src/unused" } } }
            """);

        Assert.Contains("unused", ex.Message);
        Assert.Contains("'path'", ex.Message);
    }

    [Fact]
    public void Validate_UnknownKeyBelongingToNoBlock_ListsTheValidKeys()
    {
        var ex = Load("""{ "services": { "orders": { "source": "local", "nonsense": "x" } } }""");

        Assert.Contains("'nonsense' is not a valid key", ex.Message);
        Assert.Contains("'source'", ex.Message);
        Assert.Contains("'kubernetes'", ex.Message);
    }

    [Fact]
    public void Validate_TypoInsideAnInactiveBlock_IsStillRejected()
    {
        var ex = Load("""
            { "services": { "orders": {
                "source": "local",
                "kubernetes": { "contxt": "dev-west" } } } }
            """);

        Assert.Contains("'contxt'", ex.Message);
        Assert.Contains("kubernetes", ex.Message);
    }

    [Fact]
    public void Validate_ValidInactiveBlock_IsAccepted()
    {
        var dir = CreateAppHostDirectory("""
            { "services": { "orders": {
                "source": "url",
                "url": { "url": "https://orders.invalid" },
                "kubernetes": { "context": "dev-west", "namespace": "orders", "port": 8080 } } } }
            """);

        var builder = TestHelpers.CreateBuilder(dir);

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("url", config.Source);
        Assert.Equal("dev-west", config.Kubernetes.Context);
    }

    /// <remarks>
    /// The file spells its keys lowercase and the properties they are checked against are
    /// PascalCase, so an ordinal comparison would reject every well-formed file there is. The
    /// PascalCase case is the spelling an environment variable would supply, reaching the same
    /// fields through the file here because that is the cheaper way to drive it.
    /// </remarks>
    [Theory]
    [InlineData("local", "path")]
    [InlineData("Local", "Path")]
    [InlineData("LOCAL", "PATH")]
    public void Validate_AnyKeyCasing_IsAccepted(string block, string field)
    {
        var dir = CreateAppHostDirectory(
            $$"""{ "services": { "orders": { "source": "local", "{{block}}": { "{{field}}": "/src/orders" } } } }""");

        var builder = TestHelpers.CreateBuilder(dir);

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "orders");

        Assert.Equal("/src/orders", config.Local.Path);
    }
}
```

- [x] **Step 2: Run them to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~ServiceDeveloperConfigValidatorTests" -f net10.0`

Expected: the six rejection tests FAIL — `Assert.Throws` finds no exception, because Task 1 left no validation. `Validate_ValidInactiveBlock_IsAccepted` and the three `Validate_AnyKeyCasing_IsAccepted` cases already PASS; they are the guards that Task 2 does not overshoot into rejecting valid config.

- [x] **Step 3: Write the shape map**

Create `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfigShape.cs`:

```csharp
using System.Reflection;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// What a service entry is allowed to contain, read off <see cref="ServiceDeveloperConfig"/> itself
/// rather than declared a second time beside it. Deriving it means a field added to a block type is
/// immediately a valid key, with nothing to keep in step.
/// </summary>
/// <remarks>
/// Every set compares with <see cref="StringComparer.OrdinalIgnoreCase"/> because configuration
/// keys do: a <c>Local:Path</c> arriving from an environment variable and a <c>local:path</c> in
/// the file are the same key.
/// </remarks>
internal static class ServiceDeveloperConfigShape
{
    /// <summary>The block properties — every property whose value is a nested settings object.</summary>
    /// <remarks>
    /// Tested for positively rather than by excluding <see cref="string"/> alone, so that a scalar
    /// added at the entry root later — a <c>bool?</c> or an <c>int?</c> — is not silently taken for
    /// a block and walked for fields it does not have.
    /// </remarks>
    public static IReadOnlyList<PropertyInfo> Blocks { get; } =
        typeof(ServiceDeveloperConfig).GetProperties()
            .Where(p => p.PropertyType.IsClass && p.PropertyType != typeof(string))
            .ToArray();

    /// <summary>The keys valid directly on a service entry: <c>source</c> and the block names.</summary>
    public static IReadOnlySet<string> RootKeys { get; } =
        typeof(ServiceDeveloperConfig).GetProperties()
            .Select(p => p.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Block name to the keys valid inside it.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> BlockFields { get; } =
        Blocks.ToDictionary(
            block => block.Name,
            block => (IReadOnlySet<string>)block.PropertyType.GetProperties()
                .Select(field => field.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The block <paramref name="field"/> belongs in, or <see langword="null"/> if no block has a
    /// field by that name. Used to turn "that key does not go there" into "here is where it goes",
    /// which is unambiguous only because no field name is shared by two blocks.
    /// </summary>
    public static string? HomeBlockOf(string field) =>
        BlockFields.FirstOrDefault(block => block.Value.Contains(field)).Key;
}
```

- [x] **Step 4: Write the validator**

Create `src/Aspire.Hosting.ServiceSources/Config/ServiceDeveloperConfigValidator.cs`:

```csharp
using Microsoft.Extensions.Configuration;

namespace Aspire.Hosting.ServiceSources.Config;

/// <summary>
/// Fails fast on a key that would bind to nothing, so a typo — or a field written flat, where it
/// belongs to a block — is reported instead of being silently dropped.
/// </summary>
internal static class ServiceDeveloperConfigValidator
{
    /// <summary>
    /// Checks one service's entry. Every block is checked, not only the one <c>source</c> names: a
    /// block for a source this entry is not currently using is legitimate and left alone, but a
    /// typo inside it would otherwise lie in wait until the day the source is switched to it.
    /// </summary>
    public static void Validate(string serviceName, IConfigurationSection entry)
    {
        foreach (var key in entry.GetChildren())
        {
            if (!ServiceDeveloperConfigShape.RootKeys.Contains(key.Key))
            {
                throw NotValidHere(serviceName, key.Key);
            }

            if (!ServiceDeveloperConfigShape.BlockFields.TryGetValue(key.Key, out var fields))
            {
                continue;
            }

            // A block name carrying a value rather than an object is the old flat shape written
            // with a name this type happens to also use for a block — `"url": "https://…"` against
            // the `url` block. It binds to nothing, and passing the check above on the strength of
            // the name alone would let the one field most likely to be written flat through in
            // silence.
            if (key.Value is not null)
            {
                throw NotValidHere(serviceName, key.Key);
            }

            foreach (var field in key.GetChildren())
            {
                if (!fields.Contains(field.Key))
                {
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{serviceName}': '{field.Key}' is not a valid key in the "
                        + $"'{key.Key.ToLowerInvariant()}' block. Valid keys there are {Quoted(fields)}.");
                }
            }
        }
    }

    /// <remarks>
    /// The suggestion names the block and nothing else. Telling a developer which <c>source</c> to
    /// set would be advice to change what the service resolves to — a stray <c>port</c> on a
    /// container-sourced entry belongs in the <c>kubernetes</c> block, but that is emphatically not
    /// a reason to make the service kubernetes-sourced.
    /// </remarks>
    private static ServiceSourcesConfigurationException NotValidHere(string serviceName, string key)
    {
        var home = ServiceDeveloperConfigShape.HomeBlockOf(key)?.ToLowerInvariant();

        return new ServiceSourcesConfigurationException(
            home is not null
                ? $"Service '{serviceName}': '{key}' is not a valid key here. It belongs in the "
                  + $"'{home}' block: \"{serviceName}\": {{ ..., \"{home}\": {{ \"{key}\": ... }} }}."
                : $"Service '{serviceName}': '{key}' is not a valid key. Valid keys are "
                  + $"{Quoted(ServiceDeveloperConfigShape.RootKeys)}.");
    }

    private static string Quoted(IEnumerable<string> keys) =>
        string.Join(", ", keys.Select(k => $"'{k.ToLowerInvariant()}'").Order(StringComparer.Ordinal));
}
```

- [x] **Step 5: Call it from `ReadFrom`**

In `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfiguration.cs`, replace the binding line in `ReadFrom`:

```csharp
        var section = builder.Configuration.GetSection(ServicesKey);

        // Before binding, and for every entry rather than only the ones an AddService call reaches:
        // LocalCheckoutPrefetch clones every "local" entry the moment the first local-sourced
        // service is resolved, including entries for services no AddService call ever names, so a
        // malformed one would otherwise pay for a checkout before anything looked at it. The keys
        // are checked as the developer spelled them, ahead of the canonicalization below.
        foreach (var entry in section.GetChildren())
        {
            ServiceDeveloperConfigValidator.Validate(entry.Key, entry);
        }

        var bound = section.Get<Dictionary<string, ServiceDeveloperConfig>>() ?? [];
```

`using Microsoft.Extensions.Configuration;` is already present.

- [x] **Step 6: Stop a failed load from registering the file source twice**

`ReadFrom` calls `AddFileSource` before this validation, so it can now throw *after* its own side effect. `ConfigLoader.Load` deliberately leaves its slot empty when a load throws, so the next caller re-runs `ReadFrom` and inserts a second `MemoryConfigurationSource` into the builder's configuration — breaking the once-only invariant that `DeveloperConfiguration`'s own remarks and `ServiceSourcesConfigCacheTests.LoadedFor_CalledRepeatedly_RegistersTheFileSourceOnce` both exist to protect. Until now `ReadFrom` could not throw, because the catalog load ahead of it failed first, before any side effect.

Write the failing test first, in `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceSourcesConfigCacheTests.cs`, following the shape of the existing `LoadedFor_CalledRepeatedly_RegistersTheFileSourceOnce` and counting sources the same way it does:

```csharp
    /// <remarks>
    /// Reading the config registers the file on the builder, and validation now runs after that
    /// registration, so a rejected entry throws with the side effect already applied. The error has
    /// to keep being reported to every caller without the registration being repeated.
    /// </remarks>
    [Fact]
    public void LoadedFor_AfterAValidationFailure_DoesNotRegisterTheFileSourceAgain()
    {
        var dir = CreateAppHostDirectory(
            OrdersCatalog,
            """{ "services": { "orders": { "source": "local", "path": "/src/orders" } } }""");

        var builder = CreateBuilder(dir);

        Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceSourcesConfigCache.LoadedFor(builder));
        var afterFirst = OurConfigurationSources(builder);

        Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceSourcesConfigCache.LoadedFor(builder));

        Assert.Equal(afterFirst, OurConfigurationSources(builder));
    }
```

This uses the file's own private `CreateBuilder` and its `OurConfigurationSources` helper — the same ones `LoadedFor_CalledRepeatedly_RegistersTheFileSourceOnce` uses. Do not introduce new helpers, and match the existing names for building the AppHost directory and catalog.

Run it to see it fail (the count grows by one), then latch the failure in `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs`:

```csharp
        // Plain object rather than System.Threading.Lock: this package still targets net8.0.
        private readonly object _gate = new();

        private LoadedConfig? _loaded;

        private ExceptionDispatchInfo? _failure;

        /// <summary>
        /// A load that throws is remembered and rethrown, rather than retried: a configuration
        /// error has to keep being reported to whoever asks, and reading the config registers
        /// servicesources.local.json on the builder, which must not happen a second time.
        /// </summary>
        public LoadedConfig Load(IDistributedApplicationBuilder builder)
        {
            lock (_gate)
            {
                _failure?.Throw();

                if (_loaded is not null)
                {
                    return _loaded;
                }

                try
                {
                    return _loaded = LoadedConfig.Load(builder);
                }
                catch (Exception ex)
                {
                    _failure = ExceptionDispatchInfo.Capture(ex);
                    throw;
                }
            }
        }
```

Add `using System.Runtime.ExceptionServices;` at the top of the file. Replace `Load`'s existing `<summary>` with the one above — the old text says a throwing load leaves the slot empty so the next caller tries again, which is exactly what this changes. Leave the `ConfigLoader` class's own `<summary>`, which sits above this block, untouched.

Run the test again: PASS. `ServiceSourcesConfigCacheTests` must stay green as a whole; the error is still raised on every call, so any test asserting a repeated failure still passes.

- [x] **Step 7: Run the validator tests**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~ServiceDeveloperConfigValidatorTests" -f net10.0`

Expected: PASS — 8 test methods, 10 executed cases.

- [x] **Step 8: Run the full suite**

Run: `dotnet test ServiceSources.slnx -f net10.0`

Expected: PASS. If a pre-existing test fixture carries a stray key that was previously ignored, the new check will now name it — fix the fixture, not the check.

- [x] **Step 9: Commit**

```bash
git add -A
git commit -m "Reject a config key that would bind to nothing, at read time (#161)"
```

---

### Task 3: Blank means absent

A higher layer can override a field but not yet *drop* one. Normalizing blank strings to null makes `…__Local__Path=` a working unset, and closes a bug where a blank `path` resolves the checkout to the AppHost directory.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfiguration.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigurationTests.cs`

**Interfaces:**
- Consumes: `ServiceDeveloperConfigShape.Blocks` from Task 2.
- Produces: nothing other tasks depend on.

- [x] **Step 1: Write the failing tests**

Add the catalog constant beside the others already in `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigurationTests.cs`:

```csharp
    private const string BlankingCatalog = """
        services:
          blanking:
            repository: https://github.com/company/blanking
            project: Blanking.csproj
        """;
```

then append the test. The path is escaped for JSON the same way `AddServiceTests` does it, because a Windows temp path contains backslashes:

```csharp
    /// <remarks>
    /// Blanking a value is the only gesture a higher layer has for dropping a field the file below
    /// set — configuration can add a key but not remove one. Without this the empty value binds as
    /// "" rather than null, and 'path' in particular then resolves through
    /// Path.GetFullPath("", appHostDirectory) to the AppHost directory itself, which
    /// LocalGitCheckout adopts as the checkout and uses with no clone or fetch.
    ///
    /// The blank arrives on an in-memory layer rather than an environment variable because
    /// Environment.SetEnvironmentVariable(name, "") *deletes* the variable instead of setting it
    /// empty, so the test would assert nothing. A real shell's `VAR= dotnet run` does export an
    /// empty string, and the environment provider reads it as ""; only the in-process gesture for
    /// arranging it differs. Both layers sit above the file, which is inserted at index 0.
    /// </remarks>
    [Fact]
    public void ResolveService_BlankOverride_DropsTheFieldRatherThanBindingEmpty()
    {
        var configured = Directory.CreateTempSubdirectory().FullName;
        var dir = CreateAppHostDirectory(
            BlankingCatalog,
            $$"""
            { "services": { "blanking": { "source": "local",
                "local": { "path": "{{configured.Replace("\\", "\\\\")}}" } } } }
            """);

        var builder = CreateBuilder(dir);
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ServiceSources:Services:blanking:Local:Path"] = "" });

        var (_, config) = ServiceSourcesConfigCache.ResolveService(builder, "blanking");

        Assert.Null(config.Local.Path);

        // The bug this closes: with the path left as "", PrepareRepoRoot takes its override branch
        // and Path.GetFullPath("", appHostDirectory) hands back the AppHost directory, which it then
        // adopts as the checkout. Absent, it uses the managed checkout instead. The .git directory
        // is pre-seeded so the adopt-existing branch answers without needing a git client.
        var repoRoot = Path.Combine(dir, ".servicesources", "checkouts", "blanking");
        Directory.CreateDirectory(Path.Combine(repoRoot, ".git"));

        var prepared = LocalGitCheckout.PrepareRepoRoot(
            "blanking", new ServiceMetadata(), config, dir, gitClient: null!);

        Assert.Equal(repoRoot, prepared.RepoRoot);
        Assert.NotEqual(dir, prepared.RepoRoot);
    }
```

`AddInMemoryCollection` needs `using Microsoft.Extensions.Configuration;`, and `LocalGitCheckout` needs `using Aspire.Hosting.ServiceSources.Git;`. Add both to the file if absent.

Then the test for the one deliberate semantic change, also in `DeveloperConfigurationTests.cs`. It belongs at this level, not in `UrlSourceTests`: normalization happens when the config is read, so by the time `UrlSource.ResolveUrl` runs the value is already `null` and a test there would only restate the existing `ResolveUrl_NoOverride_FallsBackToMetadataUrl`.

```csharp
    /// <remarks>
    /// Written blank in the file rather than overridden blank from above, which is the other way
    /// the value arrives empty. Before normalization this bound as "" and shadowed the catalog's
    /// url.url, so the service failed as "no url configured" while the catalog had one all along.
    /// </remarks>
    [Fact]
    public void ResolveService_BlankFieldInTheFile_FallsBackToTheCatalog()
    {
        var dir = CreateAppHostDirectory(
            BlankUrlCatalog,
            """{ "services": { "blankurl": { "source": "url", "url": { "url": "" } } } }""");

        var builder = CreateBuilder(dir);

        var (metadata, config) = ServiceSourcesConfigCache.ResolveService(builder, "blankurl");

        Assert.Null(config.Url.Url);

        // Absent rather than empty is what lets the catalog through: `config.Url.Url ?? metadata…`
        // does not fall through an empty string, so before this the service failed as "no url
        // configured" while the catalog had one all along.
        Assert.Equal(
            "https://from-catalog.example.com/",
            UrlSource.ResolveUrl("blankurl", metadata, config).ToString());
    }
```

`UrlSource` needs `using Aspire.Hosting.ServiceSources.Sources;`.

with its catalog constant, which carries the URL the entry falls back to:

```csharp
    private const string BlankUrlCatalog = """
        services:
          blankurl:
            url:
              url: https://from-catalog.example.com
        """;
```

- [x] **Step 2: Run them to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~Blank" -f net10.0`

Expected: both FAIL. `ResolveService_BlankOverride_DropsTheFieldRatherThanBindingEmpty` finds `config.Local.Path` is `""`; `ResolveService_BlankFieldInTheFile_FallsBackToTheCatalog` finds `config.Url.Url` is `""`. Both should read `null`.

- [x] **Step 3: Write the normalizer**

Add to `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfiguration.cs`:

```csharp
    /// <summary>
    /// Maps a blank string field to absent, throughout every block.
    /// </summary>
    /// <remarks>
    /// A higher configuration layer can set a key but has no way to remove one, so blanking it is
    /// the only gesture available for dropping a field the file below set — and an empty
    /// environment variable binds as "" rather than null, which every consumer would read as a
    /// configured value. Nullable numbers already behave this way: the binder maps an empty string
    /// to null for <c>int?</c>, so before this only the string fields were out of step.
    /// </remarks>
    private static void NormalizeBlankToAbsent(ServiceDeveloperConfig config)
    {
        foreach (var block in ServiceDeveloperConfigShape.Blocks)
        {
            var instance = block.GetValue(config);

            foreach (var field in block.PropertyType.GetProperties().Where(f => f.PropertyType == typeof(string)))
            {
                if (field.GetValue(instance) is string value && string.IsNullOrWhiteSpace(value))
                {
                    field.SetValue(instance, null);
                }
            }
        }
    }
```

- [x] **Step 4: Call it after binding**

In `ReadFrom`, immediately after the `Get<Dictionary<string, ServiceDeveloperConfig>>()` line added in Task 2:

```csharp
        var bound = section.Get<Dictionary<string, ServiceDeveloperConfig>>() ?? [];

        foreach (var config in bound.Values)
        {
            NormalizeBlankToAbsent(config);
        }
```

- [x] **Step 5: Run the tests**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests/Aspire.Hosting.ServiceSources.Tests.csproj --filter "FullyQualifiedName~Blank" -f net10.0`

Expected: PASS, both.

- [x] **Step 6: Run the full suite**

Run: `dotnet test ServiceSources.slnx -f net10.0`

Expected: PASS.

- [x] **Step 7: Commit**

```bash
git add -A
git commit -m "Treat a blank developer-config field as absent (#161)"
```

---

### Task 4: Documentation, samples and changelog

The shape is user-facing, and the README currently documents the flat one — including a "Named profiles" example that is an instance of the bug.

**Files:**
- Modify: `README.md`
- Modify: `samples/DemoAppHost/servicesources.local.json.example`
- Modify: `samples/DemoAppHostTypeScript/servicesources.local.json.example`
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: the final shape from Tasks 1–3.
- Produces: nothing code depends on.

- [x] **Step 1: Update both `.example` files**

`samples/DemoAppHost/servicesources.local.json.example` — the entries are bare today, so the shape barely changes; add one block to show the form:

```json
{
  "services": {
    "orders": { "source": "local" },
    "inventory": { "source": "url" },
    "payments": { "source": "container", "container": { "tag": "latest" } }
  }
}
```

Apply the same treatment to `samples/DemoAppHostTypeScript/servicesources.local.json.example`, keeping whatever services it names.

- [x] **Step 2: Rewrite the README's "Overriding `servicesources.local.json`" section**

Keep the layer table and both existing warnings (the output-directory one and the `DOTNET_ENVIRONMENT` one) as they are. Change:

- The single-run example keeps working verbatim, and say so — it is the line people paste:
  `ServiceSources__Services__orders__Source=url dotnet run`
- The "Any field works the same way" sentence gains the block segment:
  `ServiceSources__Services__orders__Local__Ref`, `ServiceSources__Services__orders__Container__Tag`.
- Add that a blank value drops a field: `ServiceSources__Services__orders__Local__Path=`.
- The **Named profiles** `appsettings.Cluster.json` example becomes:

```json
{
  "ServiceSources": {
    "Services": {
      "orders": {
        "source": "kubernetes",
        "kubernetes": { "context": "dev-west", "namespace": "orders", "port": 8080 }
      }
    }
  }
}
```

- Add a short paragraph stating the property the nesting buys, since it is the reason for the shape: switching `source` from a higher layer leaves the previous source's block in place and unread, so no field has to be removed from the file to switch away from it.

- [x] **Step 3: Update every other README spot documenting a flat field**

There are six blocks of flat developer-config JSON besides the Named profiles example, plus one line of prose. Line numbers are from the branch state before Step 2; re-locate each by its description rather than trusting the number after an edit shifts the file:

| Around | What it shows |
| --- | --- |
| 162-167 | the `local` source's options — `path` and `ref` |
| 331-332 | the monorepo example — two entries |
| 657-662 | kubernetes — `context`, `namespace`, `port` |
| 703 | `url` overriding the catalog's `url.url` |
| 738 | `tag` overriding the catalog's `defaultTag` |
| 778 | an inline kubernetes entry |
| 822-823 | prose naming `…__Ref` and `…__Tag` environment variables |

Nest each field under its source's block, and give the two environment-variable names their block segment (`…__Local__Ref`, `…__Container__Tag`).

- [x] **Step 4: Verify no flat developer-config example survives**

```bash
grep -n '"path"\|"ref"\|"url"\|"context"\|"namespace"\|"port"\|"tag"' README.md samples/*/servicesources.local.json.example
grep -n 'ServiceSources__Services__' README.md
```

Every developer-config occurrence from the first command must sit inside a block; catalog (`servicesources.yaml`) examples are a different shape and stay flat, so check which kind each hit is. Every hit from the second must be either `…__Source` or carry a block segment.

- [x] **Step 5: Write the CHANGELOG entry**

Under `## [Unreleased]`, add a `### Breaking` section:

````markdown
### Breaking

- **Each source's settings move into a block named for that source in `servicesources.local.json`**
  ([#161]). A field written directly on a service's entry is no longer read, and is reported rather
  than ignored. Rewrite each entry by moving its fields under the source they belong to:

  ```json
  { "services": { "orders": { "source": "local", "path": "/src/orders" } } }
  ```

  becomes

  ```json
  { "services": { "orders": { "source": "local", "local": { "path": "/src/orders" } } } }
  ```

  `source` itself is unchanged, so pinning a source from the environment —
  `ServiceSources__Services__orders__Source=container` — needs no edit. Overriding a *field* from a
  higher layer gains the block segment: `ServiceSources__Services__orders__Local__Ref`.

  The shape exists so that a higher configuration layer can switch a service's source. Configuration
  merges per key, so with the fields flat the old source's fields survived a switch and were then
  rejected as invalid for the new source, which made the override story in this release false for
  the case it was most wanted for. Under a block they survive unread.
````

Add the link reference `[#161]: https://github.com/flojon/aspire-servicesources/issues/161` beside the others at the bottom of the file, matching the existing style.

- [x] **Step 6: Run the full suite once more**

Run: `dotnet test ServiceSources.slnx -f net10.0`

Expected: PASS.

- [x] **Step 7: Commit**

```bash
git add -A
git commit -m "Document the nested developer-config shape (#161)"
```

[#148]: https://github.com/flojon/aspire-servicesources/pull/148
[#157]: https://github.com/flojon/aspire-servicesources/pull/157
[#161]: https://github.com/flojon/aspire-servicesources/issues/161
