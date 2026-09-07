# Code Catalog Stage 1 — Core Split, AddServiceCatalog, WithKind Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `servicesources.yaml` optional. Introduce a source-agnostic domain type
(`ServiceDefinition`), a public `AddServiceCatalog` builder API covering all four source kinds
(`local`/`WithRepository`, `url`, `container`, `kubernetes`) plus `WithKind` for third-party local
kinds, compose it with the existing yaml loader (yaml stays a provider), and reach acceptance
criteria 3 (existing yaml AppHosts unchanged) and 4 (duplicate declaration is an error naming both
sources) in full, and criteria 1–2 (no-yaml AppHost, C# and TypeScript) for `dotnet` and any
`WithKind`-configured kind. `WithPrepare` and typed `AsJava`/`AsJavaScript` handles are Stage 2 —
**out of scope here**; `WithKind` with a raw options object (dictionary or an out-of-tree kind's own
public type) is what Stage 1 ships instead.

**Architecture:** `ServiceMetadata`/`ServiceCatalog` stay exactly as they are — yaml DTOs, nothing
else. A new `ServiceDefinition` (in a new `Config/Catalog/` folder, alongside the DTOs it's derived
from and consumed by) is what every downstream consumer (`IServiceSource.Resolve` and everything it
calls) is retyped to read instead. `ServiceMetadata.ToDefinition()` produces one. A public
`ServiceCatalogBuilder`/`ServiceDefinitionBuilder` pair, exported to ATS from the start (Stage 0's
own conclusion: freezing these signatures without ATS exports risks discovering non-projection in a
later release, which is a breaking change in a package with no ApiCompat), accumulate code-declared
entries; `AddServiceCatalog` on `ServiceSourcesBuilderExtensions` is the entry point. `LoadedConfig.Load`
composes: freeze the code catalog, load yaml if present, merge into a `CodeServiceCatalog` (the
merged map every downstream reader — `ResolveService`, `LocalCheckoutPrefetch`, `AddBackingService`'s
sibling paths — reads instead of `ServiceCatalog.Services`), routing each collision kind to its own
error per the design's finding 11.

**Tech Stack:** C# (Aspire.Hosting.ServiceSources, `net8.0`/`net9.0`/`net10.0`), YamlDotNet (existing
yaml path, untouched), Aspire's ATS export attributes (`[AspireExport]`), xUnit (test/Aspire.Hosting.ServiceSources.Tests).

**Spec:** `docs/superpowers/specs/2026-09-05-servicesources-code-catalog-design.md` (the accepted
design — read alongside this plan; every task below cites the finding or section it implements).
Stage 0's correction: `docs/superpowers/specs/2026-09-07-code-catalog-stage0-ats-probe-findings.md`.

## Global Constraints

- **Stage 0's correction, verbatim:** the catalog builder's service-declaration method must carry an
  **explicit capability id** distinct from `addService` — `[AspireExport("addServiceToCatalog")]` (or
  another distinct string; not `MethodName`, which does not change the colliding capability id, and
  not the bare method name with no explicit `id`). See Task 3.
- **`$(AspireVersion)` floor is 13.5.2** (`Directory.Build.props:74`). The Aspire CLI on `PATH` in
  this environment is 13.5.1 and fails `aspire restore` with NU1605. A CLI ≥ the floor was installed
  during Stage 0 to `/home/flojon/.claude/jobs/47589aa7/tmp/aspire-cli` (version 13.5.3) — confirm it
  is still there before Task 14's TypeScript verification; reinstall with
  `dotnet tool install --tool-path <path> aspire.cli --version 13.5.3` if not.
- **`-warnaserror`** on the build leg (`dotnet build -c Release -warnaserror`, matching
  `ci.yml`'s `🔨 build, test & pack` job) — every task's build step must be warning-clean, not merely
  error-free.
- **No typed `PrepareMode` on `ServiceDefinition` in this stage.** `Prepare.Mode` stays `string?`,
  parsed where it is parsed today (`Prepare/PreparePlan.cs`), per finding 3 and the design's "What
  this deliberately does not do". `ServiceDefinition.Prepare` is the existing `PrepareMetadata?`
  unchanged.
- **`WithPrepare`, `AsJava`, `AsJavaScript` are Stage 2 — do not build them here.** A service
  configured through `WithKind("java", options)` in this stage passes a raw
  `Dictionary<string, object>` (the same shape yaml produces) or, for a genuine third-party kind, that
  kind's own public options type — never `JavaKindOptions`/`JavaScriptKindOptions`, which stay
  `internal` until Stage 2's typed handles land. See Task 13's sample for the concrete shape.
- **Merge comparison is `OrdinalIgnoreCase`, done by hand — never an `OrdinalIgnoreCase` dictionary**
  (finding 11). The underlying map stays `Ordinal`, so lookup for an existing yaml-only AppHost is
  byte-for-byte unchanged. Getting this wrong breaks acceptance criterion 3 silently — no test failure
  until `CatalogCompositionTests` (Task 10) specifically checks it.
- **Origin-aware error messages** (finding 5): a code-only catalog must never produce a
  `ServiceSourcesConfigurationException` whose message contains the literal string
  `servicesources.yaml`. Task 11 drives this from a test rather than a fixed file list, because the
  design's own file/line citations (against commit `48d2f8f`) have already drifted — confirmed during
  this plan's own recon (e.g. `KubernetesSource.cs`'s two throw sites the design cited at `:58,91`
  are now one shared `RequireKubernetesBlock` helper, its message at line 81). Trust the test, not old
  line numbers.

---

## Task 1: The domain type — `ServiceDefinition`, `CatalogOrigin`, and `ServiceMetadata.ToDefinition()`

Implements: Architecture → "The domain type"; finding 3 (closes #73, per Reviewer decisions Q4).

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/Catalog/CatalogOrigin.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Config/Catalog/ServiceDefinition.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs` (add `ToDefinition()`)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/ServiceDefinitionTests.cs`

**Interfaces:**
- Produces: `CatalogOrigin` (sealed record, two factory members: `CatalogOrigin.Code`,
  `CatalogOrigin.FromYaml(string path)`), `ServiceDefinition` (sealed class, all properties `init`-only),
  `ServiceMetadata.ToDefinition(string yamlPath)`. Task 2 (the builder) and Task 9 (threading through
  consumers) both consume `ServiceDefinition` by this exact shape.

- [ ] **Step 1: Write the failing test for `CatalogOrigin`**

```csharp
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Tests.Config.Catalog;

public class CatalogOriginTests
{
    [Fact]
    public void Code_DescribesAsCode()
    {
        Assert.Equal("code (AddServiceCatalog)", CatalogOrigin.Code.Describe());
    }

    [Fact]
    public void FromYaml_DescribesWithQuotedPath()
    {
        var origin = CatalogOrigin.FromYaml("/apphost/servicesources.yaml");

        Assert.Equal("'/apphost/servicesources.yaml'", origin.Describe());
    }

    [Fact]
    public void Code_IsNotEqualToYaml()
    {
        Assert.NotEqual(CatalogOrigin.Code, CatalogOrigin.FromYaml("/x/servicesources.yaml"));
    }
}
```

- [ ] **Step 2: Run it, confirm it fails to compile** (the type doesn't exist yet)

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~CatalogOriginTests"`
Expected: build error, `CatalogOrigin` not found.

- [ ] **Step 3: Implement `CatalogOrigin`**

```csharp
namespace Aspire.Hosting.ServiceSources.Config.Catalog;

/// <summary>
/// Which catalog declared a <see cref="ServiceDefinition"/> — code, via
/// <c>AddServiceCatalog(…)</c>, or a specific <c>servicesources.yaml</c> file. Threaded into every
/// error message a code-declared service can reach, so none of them names a file that need not
/// exist (design finding 5).
/// </summary>
internal enum CatalogOriginKind
{
    Code,
    Yaml,
}

internal sealed record CatalogOrigin(CatalogOriginKind Kind, string? YamlPath = null)
{
    public static readonly CatalogOrigin Code = new(CatalogOriginKind.Code);

    public static CatalogOrigin FromYaml(string path) => new(CatalogOriginKind.Yaml, path);

    /// <summary>How this origin names itself inside an exception message.</summary>
    public string Describe() =>
        Kind == CatalogOriginKind.Code ? "code (AddServiceCatalog)" : $"'{YamlPath}'";
}
```

- [ ] **Step 4: Run the test, confirm it passes**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~CatalogOriginTests"`
Expected: 3 passed.

- [ ] **Step 5: Write the failing test for `ServiceDefinition`/`ToDefinition()`**

```csharp
[Fact]
public void ToDefinition_CopiesEveryServiceMetadataProperty()
{
    var metadata = new ServiceMetadata
    {
        Repository = "https://github.com/example/repo",
        Project = "src/Api/Api.csproj",
        DefaultRef = "main",
        Kubernetes = new KubernetesMetadata { Service = "svc", Port = 8080, Scheme = "https" },
        Url = new UrlMetadata { Url = "https://example.com" },
        Container = new ContainerMetadata { Image = "nginx", Port = 80, DefaultTag = "latest", Scheme = "http" },
        Prepare = new PrepareMetadata { Command = ["./prepare.sh"], Mode = "once" },
        Kind = "java",
        KindConfig = new Dictionary<object, object> { ["mavenGoal"] = "spring-boot:run" },
    };

    var definition = metadata.ToDefinition("/apphost/servicesources.yaml");

    Assert.Equal(metadata.Repository, definition.Repository);
    Assert.Equal(metadata.Project, definition.Project);
    Assert.Equal(metadata.DefaultRef, definition.DefaultRef);
    Assert.Same(metadata.Kubernetes, definition.Kubernetes);
    Assert.Same(metadata.Url, definition.Url);
    Assert.Same(metadata.Container, definition.Container);
    Assert.Same(metadata.Prepare, definition.Prepare);
    Assert.Equal(metadata.Kind, definition.Kind);
    Assert.Same(metadata.KindConfig, definition.KindOptions);
    Assert.Equal(CatalogOrigin.FromYaml("/apphost/servicesources.yaml"), definition.Origin);
}
```

Add this test method (and the `using Aspire.Hosting.ServiceSources.Config;` it needs) to
`ServiceDefinitionTests.cs` alongside a `Config/ServiceDefinitionTests.cs`-style class declaration —
same file created in Step 1's namespace.

- [ ] **Step 6: Run it, confirm it fails** (no `ServiceDefinition` type, no `ToDefinition` method)

- [ ] **Step 7: Implement `ServiceDefinition` and `ToDefinition()`**

```csharp
// src/Aspire.Hosting.ServiceSources/Config/Catalog/ServiceDefinition.cs
namespace Aspire.Hosting.ServiceSources.Config.Catalog;

/// <summary>
/// The composed, source-agnostic entry every downstream consumer reads — produced from a yaml
/// <see cref="ServiceMetadata"/> via <see cref="ServiceMetadataExtensions.ToDefinition"/>, or built
/// directly by <see cref="ServiceDefinitionBuilder"/> for a code-declared service. Two things a
/// yaml-bound <see cref="ServiceMetadata"/> deliberately does not carry: <see cref="Origin"/> (design
/// finding 5) and <see cref="KindOptions"/> as an already-typed value rather than a raw yaml block
/// (design finding 6).
/// </summary>
internal sealed class ServiceDefinition
{
    public required string Repository { get; init; }

    public required string Project { get; init; }

    public string? DefaultRef { get; init; }

    public KubernetesMetadata? Kubernetes { get; init; }

    public UrlMetadata? Url { get; init; }

    public ContainerMetadata? Container { get; init; }

    public PrepareMetadata? Prepare { get; init; }

    public required string Kind { get; init; }

    /// <summary>
    /// The raw yaml block (round-tripped through <see cref="LocalKindConfig.Parse{T}"/>) for a
    /// yaml-declared kind, or an already-typed options object for a code-declared one — see design
    /// finding 6's three-branch <see cref="LocalKindConfig.Parse{T}"/> change (Task 7).
    /// </summary>
    public object? KindOptions { get; init; }

    public required CatalogOrigin Origin { get; init; }
}
```

```csharp
// Add to src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs
using Aspire.Hosting.ServiceSources.Config.Catalog;
// … existing usings …

internal sealed class ServiceMetadata
{
    // … existing properties unchanged …

    /// <summary>
    /// Converts this yaml-bound entry into the source-agnostic <see cref="ServiceDefinition"/>
    /// every downstream consumer reads. <see cref="Kind"/> is already normalized to
    /// <see cref="LocalKinds.Dotnet"/> by <see cref="ServiceCatalogLoader"/> by the time this runs,
    /// so no further normalization happens here.
    /// </summary>
    public ServiceDefinition ToDefinition(string yamlPath) => new()
    {
        Repository = Repository,
        Project = Project,
        DefaultRef = DefaultRef,
        Kubernetes = Kubernetes,
        Url = Url,
        Container = Container,
        Prepare = Prepare,
        Kind = Kind,
        KindOptions = KindConfig,
        Origin = CatalogOrigin.FromYaml(yamlPath),
    };
}
```

- [ ] **Step 8: Run both test files, confirm all pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~Catalog"`
Expected: all passed, 0 failed.

- [ ] **Step 9: Build the whole solution**

Run: `dotnet build -c Release -warnaserror`
Expected: `Build succeeded`, 0 warnings.

- [ ] **Step 10: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Config/Catalog/CatalogOrigin.cs \
        src/Aspire.Hosting.ServiceSources/Config/Catalog/ServiceDefinition.cs \
        src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/ServiceDefinitionTests.cs
git commit -m "Introduce ServiceDefinition, the source-agnostic catalog entry (#134)"
```

---

## Task 2: `ServiceCatalogBuilder`/`ServiceDefinitionBuilder` skeleton and `AddServiceCatalog`

Implements: Architecture → "The authoring API" (skeleton only — `AddService`/freeze/ordering; `With*`
methods are Tasks 3–6); Stage 0's correction.

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceCatalogBuilder.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs` (add
  `AddServiceCatalog`, beside `UseDeferredCheckout`/`AddLocalKind` at line ~186)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceCatalogBuilderTests.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/AddServiceCatalogTests.cs`

**Interfaces:**
- Consumes: `ServiceDefinition` (Task 1).
- Produces: `ServiceCatalogBuilder` (public sealed class — `AddService(string name)` →
  `ServiceDefinitionBuilder`; `Freeze()` → `IReadOnlyDictionary<string, ServiceDefinition>`, internal,
  called only from `LoadedConfig.Load` in Task 10), `ServiceDefinitionBuilder` (public sealed class,
  wraps a mutable builder-state object Tasks 3–6 populate; exposes nothing publicly yet beyond what
  `AddService` needs to return a chainable value — Tasks 3–6 add the `With*` methods onto it).
  `ServiceSourcesBuilderExtensions.AddServiceCatalog(this IDistributedApplicationBuilder, Action<ServiceCatalogBuilder>)`.

Public types added here are new public API surface (finding 10) — every one is `sealed`.

- [ ] **Step 1: Write the failing test for accumulation and freeze**

```csharp
// test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceCatalogBuilderTests.cs
using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class ServiceCatalogBuilderTests
{
    [Fact]
    public void AddService_ThenFreeze_ProducesOneEntryPerCall()
    {
        var builder = new ServiceCatalogBuilder();

        builder.AddService("orders");
        builder.AddService("payments");

        var frozen = builder.Freeze();

        Assert.Equal(["orders", "payments"], frozen.Keys.Order());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddService_RejectsNullEmptyOrWhitespaceName(string? name)
    {
        var builder = new ServiceCatalogBuilder();

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService(name!));

        Assert.Contains("name", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddService_TwoNamesDifferingOnlyByCase_RejectedNamingBoth()
    {
        var builder = new ServiceCatalogBuilder();
        builder.AddService("Orders");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("'Orders'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'orders'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddService_SameNameTwice_RejectedAsDuplicate()
    {
        var builder = new ServiceCatalogBuilder();
        builder.AddService("orders");

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => builder.AddService("orders"));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddService_OnFrozenBuilder_ThrowsInvalidOperationException()
    {
        var builder = new ServiceCatalogBuilder();
        builder.AddService("orders");
        var frozen = builder.Freeze();

        Assert.Throws<InvalidOperationException>(() => builder.AddService("payments"));
        Assert.Single(frozen);
    }
}
```

- [ ] **Step 2: Run, confirm it fails to compile**

- [ ] **Step 3: Implement `ServiceDefinitionBuilder` (empty shell — Tasks 3–6 fill it in)**

```csharp
// src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Catalog;

/// <summary>
/// The fluent <c>With*</c>/<c>As*</c> chain for one code-declared service. Accumulates onto a
/// mutable internal state object; <see cref="Build"/> (called only by
/// <see cref="ServiceCatalogBuilder.Freeze"/>) turns it into an immutable <see cref="ServiceDefinition"/>.
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class ServiceDefinitionBuilder
{
    private readonly string _serviceName;

    // Task 3 adds Repository/Project/DefaultRef fields and the single folded WithRepository(url,
    // project:, defaultRef:) — no separate WithProject method exists. Task 4 adds Url/WithUrl; Task 5
    // adds Container/WithContainer; Task 6 adds Kubernetes/WithKubernetes; Task 8 adds
    // Kind/KindOptions/WithKind. Each field starts null and each With* throws the additive-error
    // below if its field is already set — see Task 3 for the exact error and the shared helper.

    internal ServiceDefinitionBuilder(string serviceName)
    {
        _serviceName = serviceName;
    }

    internal string ServiceName => _serviceName;

    /// <summary>Builds the immutable <see cref="ServiceDefinition"/> this chain describes.</summary>
    internal ServiceDefinition Build() => new()
    {
        // Repository/Project default to "" (ServiceMetadata's own defaults) until Task 3 sets them —
        // a service declared with no With* call at all is caught downstream by the same
        // "no source configured" path an empty yaml entry hits today, not rejected here.
        Repository = "",
        Project = "",
        Kind = LocalKinds.Dotnet,
        Origin = CatalogOrigin.Code,
    };
}
```

- [ ] **Step 4: Implement `ServiceCatalogBuilder`**

```csharp
// src/Aspire.Hosting.ServiceSources/Catalog/ServiceCatalogBuilder.cs
using Aspire.Hosting.ServiceSources.Config.Catalog;

namespace Aspire.Hosting.ServiceSources.Catalog;

/// <summary>
/// Accumulates code-declared service entries for one <c>AddServiceCatalog(…)</c> call (or several —
/// see <see cref="ServiceSourcesBuilderExtensions.AddServiceCatalog"/>, which appends). Frozen once
/// the catalog is composed (<c>LoadedConfig.Load</c>); any call reaching a frozen builder is ignored,
/// per design "Composition, freezing, and the errors".
/// </summary>
[AspireExport(ExposeMethods = true)]
public sealed class ServiceCatalogBuilder
{
    private readonly Dictionary<string, ServiceDefinitionBuilder> _entries = new(StringComparer.Ordinal);
    private bool _frozen;

    /// <summary>
    /// Declares a service, returning a chain to configure its source(s). Two code-declared names
    /// differing only by case are rejected here, naming both — see design "Names."
    /// </summary>
    [AspireExport("addServiceToCatalog")]
    public ServiceDefinitionBuilder AddService(string name)
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                "This ServiceCatalogBuilder was already frozen by AddServiceCatalog composing the catalog. " +
                "A builder captured and mutated after that point contributes nothing — declare every service " +
                "before the first AddService(…) call instead.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ServiceSourcesConfigurationException(
                "AddServiceCatalog: a service name is required and cannot be empty or whitespace.");
        }

        var caseCollision = _entries.Keys.FirstOrDefault(
            existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(existing, name, StringComparison.Ordinal));

        if (caseCollision is not null)
        {
            throw new ServiceSourcesConfigurationException(
                $"AddServiceCatalog: service '{name}' differs only by case from already-declared " +
                $"'{caseCollision}'. Two code-declared names must differ by more than case.");
        }

        if (!_entries.TryAdd(name, new ServiceDefinitionBuilder(name)))
        {
            throw new ServiceSourcesConfigurationException(
                $"AddServiceCatalog: service '{name}' is declared twice in code. Remove one of the two calls.");
        }

        return _entries[name];
    }

    /// <summary>Builds every accumulated entry and marks this builder frozen.</summary>
    internal IReadOnlyDictionary<string, ServiceDefinition> Freeze()
    {
        _frozen = true;
        return _entries.ToDictionary(e => e.Key, e => e.Value.Build(), StringComparer.Ordinal);
    }
}
```

- [ ] **Step 5: Run the tests, confirm they pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~ServiceCatalogBuilderTests"`
Expected: all passed.

- [ ] **Step 6: Write the failing test for `AddServiceCatalog` itself**

```csharp
// test/Aspire.Hosting.ServiceSources.Tests/Catalog/AddServiceCatalogTests.cs
using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class AddServiceCatalogTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [TestBuilderDefaults.DisableConfigReloadArg],
        });

    [Fact]
    public void AddServiceCatalog_CalledTwice_Appends()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var builder = CreateBuilder(dir);

        builder.AddServiceCatalog(c => c.AddService("orders"));
        builder.AddServiceCatalog(c => c.AddService("payments"));

        // Task 10 wires this into LoadedConfig; until then, assert indirectly is not possible —
        // this test is completed in Task 10 once ResolveService can see code-declared entries.
        // For now it asserts only that two calls do not throw.
    }

    [Fact]
    public void AddServiceCatalog_RegistersDeveloperConfigFileSource()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var builder = CreateBuilder(dir);

        builder.AddServiceCatalog(c => c.AddService("orders"));

        // DeveloperConfigFileSource.EnsureRegistered is idempotent and internal; assert observable
        // behavior instead — the local.json provider is now in the configuration chain.
        Assert.Contains(
            builder.Configuration.Sources,
            source => source is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource json
                && json.Path == "servicesources.local.json");
    }
}
```

Note: the ordering-error test (`AddServiceCatalog` called after the catalog has already been read)
belongs in Task 10, once `LoadedConfig.Load` exists to read it — add it there, not here.

- [ ] **Step 7: Run, confirm it fails to compile** (`AddServiceCatalog` doesn't exist)

- [ ] **Step 8: Implement `AddServiceCatalog`**

Add to `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs`, after `AddLocalKind`
(after line ~223 in the current file):

```csharp
/// <summary>
/// Declares services in the AppHost's own language instead of (or alongside) <c>servicesources.yaml</c>.
/// Must be called before the first <see cref="AddService"/>, which is where the catalog is read —
/// see the exception thrown by <see cref="Config.ServiceSourcesConfigCache"/> otherwise. Called more
/// than once, entries accumulate; a name declared twice across calls is the same duplicate error as
/// within one call.
/// </summary>
[AspireExport(RunSyncOnBackgroundThread = true)]
public static IDistributedApplicationBuilder AddServiceCatalog(
    this IDistributedApplicationBuilder builder, Action<Catalog.ServiceCatalogBuilder> configure)
{
    DeveloperConfigFileSource.EnsureRegistered(builder);

    Config.ServiceSourcesConfigCache.CodeCatalogFor(builder).Configure(configure);

    return builder;
}
```

This calls a new `ServiceSourcesConfigCache.CodeCatalogFor(builder)` — a per-builder
`ServiceCatalogBuilder` accumulator, same `ConditionalWeakTable` shape as the existing caches in that
class. Add it now (Task 10 is what actually *reads* it into composition):

```csharp
// Add to src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs, near the other
// ConditionalWeakTable fields (after line ~16):
private static readonly ConditionalWeakTable<IDistributedApplicationBuilder, CodeCatalogAccumulator> CodeCatalogs = new();

/// <summary>
/// The code-declared catalog builder for one AppHost builder, created on first
/// <c>AddServiceCatalog</c> call and read by <see cref="LoadedConfig.Load"/>.
/// </summary>
internal static Catalog.ServiceCatalogBuilder CodeCatalogFor(IDistributedApplicationBuilder builder) =>
    CodeCatalogs.GetValue(builder, static _ => new CodeCatalogAccumulator()).Builder;

private sealed class CodeCatalogAccumulator
{
    // Plain object rather than System.Threading.Lock: this package still targets net8.0.
    private readonly object _gate = new();

    public Catalog.ServiceCatalogBuilder Builder { get; } = new();

    public void Configure(Action<Catalog.ServiceCatalogBuilder> configure)
    {
        lock (_gate)
        {
            configure(Builder);
        }
    }
}
```

(`ServiceCatalogBuilder` itself is not thread-safe internally — `RunSyncOnBackgroundThread = true`
means an ATS caller's `configure` can run on a background thread, so this lock, taken around every
`Configure` call, is what the design's "Memory model" paragraph asks for. `LoadedConfig.Load`'s
freeze in Task 10 must take the same lock before calling `Freeze()`.)

- [ ] **Step 9: Run the tests, confirm they pass**

- [ ] **Step 10: Build with `-warnaserror`, run the fast in-repo tsc probe**

The ATS shapes here (`Action<ServiceCatalogBuilder>` parameter, `[AspireExport(RunSyncOnBackgroundThread
= true)]` on the entry point, `[AspireExport(ExposeMethods = true)]` on the builder class,
`[AspireExport("addServiceToCatalog")]` on `AddService`) are exactly what Stage 0 measured clean —
this is not a new probe, just confirmation the real code matches the probe's shape. Run the full
TypeScript export surface leg once now (per Global Constraints, install the CLI floor if the job tool
path is gone):

```bash
rm -rf samples/DemoAppHostTypeScript/.aspire
(cd samples/DemoAppHostTypeScript && aspire restore --non-interactive --nologo && npx tsc --noEmit -p tsconfig.apphost.json)
```

Expected: restore succeeds, `0 errors`. (The sample doesn't call `addServiceCatalog` yet — Task 14
adds that — so this only confirms the ambient declarations compile, same as Stage 0's Task 2.)

- [ ] **Step 11: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Catalog/ServiceCatalogBuilder.cs \
        src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs \
        src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs \
        src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Catalog/
git commit -m "Add AddServiceCatalog and the ServiceCatalogBuilder skeleton (#134)"
```

---

## Task 3: `WithRepository`/`WithProject`, folded, plus the additive-block error

Implements: Architecture → "The authoring API" table's `local` row; the additive-call rule.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs`

**Interfaces:**
- Produces: `ServiceDefinitionBuilder.WithRepository(string url, string? project = null, string?
  defaultRef = null)` → `ServiceDefinitionBuilder`. Folded per the design ("try folding
  `WithProject`/`defaultRef` into `WithRepository`", cleared by Stage 0's probe of optional/named
  parameters). A repeated call to any `With*`/`WithKind` throws, naming the service and the block —
  the shared helper this task introduces (`RequireUnset`) is reused by Tasks 4–6 and 8.

- [ ] **Step 1: Write the failing tests**

```csharp
// test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs
using Aspire.Hosting.ServiceSources.Catalog;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class ServiceDefinitionBuilderTests
{
    [Fact]
    public void WithRepository_SetsRepositoryProjectAndDefaultRef()
    {
        var builder = new ServiceCatalogBuilder();
        var definition = builder.AddService("orders")
            .WithRepository("https://github.com/example/repo", project: "src/Api.csproj", defaultRef: "main")
            .Build();

        Assert.Equal("https://github.com/example/repo", definition.Repository);
        Assert.Equal("src/Api.csproj", definition.Project);
        Assert.Equal("main", definition.DefaultRef);
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
}
```

`internal Build()` needs to be reachable from the test project — either make it `internal` with
`[InternalsVisibleTo]` (check `AssemblyInfo.cs` already grants the test project this; every other
`internal` type in this package is asserted against the same way, so it already does) or expose a
test-only accessor. Use the existing pattern: **confirm** `AssemblyInfo.cs` grants
`InternalsVisibleTo("Aspire.Hosting.ServiceSources.Tests")` before writing this test (it does, per
every other `internal sealed class` in this package having direct test coverage already).

- [ ] **Step 2: Run, confirm failure**

- [ ] **Step 3: Implement, extending `ServiceDefinitionBuilder`**

```csharp
// Add fields and the method to ServiceDefinitionBuilder (from Task 2's shell):
private string? _repository;
private string? _project;
private string? _defaultRef;

/// <summary>Declares this service's repository — the "local" source. See design "The authoring API".</summary>
public ServiceDefinitionBuilder WithRepository(string url, string? project = null, string? defaultRef = null)
{
    RequireUnset(_repository, nameof(WithRepository));
    _repository = url;
    _project = project;
    _defaultRef = defaultRef;
    return this;
}

/// <summary>
/// Guards every <c>With*</c> against a second call for the same block, naming the service and the
/// block — design "Calls are additive… A second call to the same one is a configuration error".
/// </summary>
private void RequireUnset(object? current, string blockName)
{
    if (current is not null)
    {
        throw new ServiceSourcesConfigurationException(
            $"Service '{_serviceName}': {blockName} was already called. Calls are additive across " +
            "different blocks, but a repeated call to the same one is not — remove one of the two.");
    }
}
```

And in `Build()`, replace the hardcoded `Repository = "", Project = "",` with:

```csharp
Repository = _repository ?? "",
Project = _project ?? "",
DefaultRef = _defaultRef,
```

- [ ] **Step 4: Run the tests, confirm they pass**

- [ ] **Step 5: Build with `-warnaserror`**

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs
git commit -m "Add WithRepository, folding project and defaultRef (#134)"
```

---

## Task 4: `WithUrl`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs`

**Interfaces:**
- Produces: `ServiceDefinitionBuilder.WithUrl(string url)` → `ServiceDefinitionBuilder`, setting
  `Url = new UrlMetadata { Url = url }`.

- [ ] **Step 1: Write the failing test**

```csharp
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
```

- [ ] **Step 2: Run, confirm failure**

- [ ] **Step 3: Implement**

```csharp
private UrlMetadata? _url;

public ServiceDefinitionBuilder WithUrl(string url)
{
    RequireUnset(_url, nameof(WithUrl));
    _url = new UrlMetadata { Url = url };
    return this;
}
```

Add `Url = _url,` to `Build()`.

- [ ] **Step 4: Run, confirm pass**

- [ ] **Step 5: Build with `-warnaserror`**

- [ ] **Step 6: Commit**

```bash
git add -u
git commit -m "Add WithUrl (#134)"
```

---

## Task 5: `WithContainer`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs`

**Interfaces:**
- Produces: `ServiceDefinitionBuilder.WithContainer(string image, int port, string? defaultTag =
  null)` → `ServiceDefinitionBuilder`, setting `Container = new ContainerMetadata { Image = image,
  Port = port, DefaultTag = defaultTag }`.

- [ ] **Step 1: Write the failing test**

```csharp
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
```

- [ ] **Step 2: Run, confirm failure**

- [ ] **Step 3: Implement**

```csharp
private ContainerMetadata? _container;

public ServiceDefinitionBuilder WithContainer(string image, int port, string? defaultTag = null)
{
    RequireUnset(_container, nameof(WithContainer));
    _container = new ContainerMetadata { Image = image, Port = port, DefaultTag = defaultTag };
    return this;
}
```

Add `Container = _container,` to `Build()`.

- [ ] **Step 4: Run, confirm pass**

- [ ] **Step 5: Build with `-warnaserror`**

- [ ] **Step 6: Commit**

```bash
git add -u
git commit -m "Add WithContainer (#134)"
```

---

## Task 6: `WithKubernetes`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs`

**Interfaces:**
- Produces: `ServiceDefinitionBuilder.WithKubernetes(string service, int? port = null)` →
  `ServiceDefinitionBuilder`, setting `Kubernetes = new KubernetesMetadata { Service = service, Port
  = port }`. `Scheme` has no code-authoring surface in Stage 1 — the design's authoring-API table
  doesn't give it one either, and the yaml `scheme` field stays reachable only via the loader; leave
  `Kubernetes.Scheme` null from code (default `http`, exactly as an omitted yaml `scheme:` key
  behaves). This is a judgment call within scope, not a deviation: the design table names four
  methods, not five, and adding one on top of it would need its own round.

- [ ] **Step 1: Write the failing test**

```csharp
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
```

- [ ] **Step 2: Run, confirm failure**

- [ ] **Step 3: Implement**

```csharp
private KubernetesMetadata? _kubernetes;

public ServiceDefinitionBuilder WithKubernetes(string service, int? port = null)
{
    RequireUnset(_kubernetes, nameof(WithKubernetes));
    _kubernetes = new KubernetesMetadata { Service = service, Port = port };
    return this;
}
```

Add `Kubernetes = _kubernetes,` to `Build()`.

- [ ] **Step 4: Run, confirm pass**

- [ ] **Step 5: Build with `-warnaserror`**

- [ ] **Step 6: Commit**

```bash
git add -u
git commit -m "Add WithKubernetes (#134)"
```

---

## Task 7: `LocalKindConfig.Parse<T>` — the three-branch change

Implements: finding 6.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/LocalKindConfig.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/LocalKindConfigTests.cs` (extend if it exists —
  check `find test -iname "LocalKindConfigTests.cs"` first; create it in the test project's root
  namespace, matching `LocalKindConfig`'s own root namespace, if it doesn't)

**Interfaces:**
- Modifies: `LocalKindConfig.Parse<T>(object? rawConfig, string? serviceName = null)`. Behavior
  change is **public** (design finding 6's "Two consequences to record"): `LocalKindConfig` is a
  public type. This is a `### Changed` CHANGELOG entry — folded into Task 15.

- [ ] **Step 1: Write the failing tests** — first, enumerate what YamlDotNet's dynamic
      deserialization actually produces for each yaml scalar shape, since `CameFromCode`'s
      correctness depends on covering exactly those and nothing else:

```csharp
// test/Aspire.Hosting.ServiceSources.Tests/LocalKindConfigTests.cs (extend existing file, or create)
public sealed class ProbeOptions
{
    public string? Value { get; set; }
}

public sealed class OtherOptions
{
    public string? Other { get; set; }
}

[Fact]
public void Parse_AlreadyTypedInstance_ReturnedUnchanged()
{
    var options = new ProbeOptions { Value = "x" };

    var result = LocalKindConfig.Parse<ProbeOptions>(options, "svc");

    Assert.Same(options, result);
}

[Fact]
public void Parse_InstanceOfWrongType_ThrowsNamingBothTypes()
{
    var options = new OtherOptions { Other = "x" };

    var ex = Assert.Throws<ServiceSourcesConfigurationException>(
        () => LocalKindConfig.Parse<ProbeOptions>(options, "svc"));

    Assert.Contains(nameof(OtherOptions), ex.Message, StringComparison.Ordinal);
    Assert.Contains(nameof(ProbeOptions), ex.Message, StringComparison.Ordinal);
}

[Fact]
public void Parse_Dictionary_StillRoundTrips()
{
    var raw = new Dictionary<object, object> { ["value"] = "x" };

    var result = LocalKindConfig.Parse<ProbeOptions>(raw, "svc");

    Assert.Equal("x", result!.Value);
}

[Theory]
[InlineData("a string")]
[InlineData(42)]
[InlineData(true)]
public void Parse_YamlProducedScalar_StillReportsScalarShape(object scalar)
{
    var ex = Assert.Throws<ServiceSourcesConfigurationException>(
        () => LocalKindConfig.Parse<ProbeOptions>(scalar, "svc"));

    Assert.Contains("must be a block of key/value pairs", ex.Message, StringComparison.Ordinal);
}

[Fact]
public void Parse_YamlProducedList_StillReportsListShape()
{
    var list = new List<object> { "a", "b" };

    var ex = Assert.Throws<ServiceSourcesConfigurationException>(
        () => LocalKindConfig.Parse<ProbeOptions>(list, "svc"));

    Assert.Contains("a list", ex.Message, StringComparison.Ordinal);
}

[Fact]
public void Parse_Null_ReturnsNull()
{
    Assert.Null(LocalKindConfig.Parse<ProbeOptions>(null, "svc"));
}
```

- [ ] **Step 2: Run, confirm `Parse_AlreadyTypedInstance_ReturnedUnchanged` and
      `Parse_InstanceOfWrongType_ThrowsNamingBothTypes` fail** (the other four already pass against
      today's implementation — confirm that too, so the diff this task makes is visible).

- [ ] **Step 3: Implement the three branches**

```csharp
public static T? Parse<T>(object? rawConfig, string? serviceName = null) where T : class
{
    if (rawConfig is null)
    {
        return null;
    }

    // Branch 1: already the right type — a WithKind(kind, options) call passed a real T. Nothing
    // to parse; the caller's instance is returned as-is. Per design finding 6, the caller must not
    // retain or mutate it afterwards — every shipped kind (Java/JavaScript) already respects this
    // by projecting a fresh immutable record per call, so nothing here needs to defensively copy.
    if (rawConfig is T alreadyTyped)
    {
        return alreadyTyped;
    }

    // Branch 2: came from code, but for a *different* options type — a WithKind(kind, options) call
    // passed the wrong kind's options object. CameFromCode must mirror the branch below exactly:
    // anything YamlDotNet's dynamic deserialization can produce (string, boxed primitive, IList,
    // IDictionary) is NOT this branch, even if T doesn't match — those fall through to the existing
    // scalar/list message instead, unchanged.
    if (CameFromCode(rawConfig))
    {
        throw new ServiceSourcesConfigurationException(
            $"{Prefix(serviceName)}the per-kind config block is a '{rawConfig.GetType().Name}', but this " +
            $"kind expects '{typeof(T).Name}'. Pass the options type this kind's registration method " +
            "documents, not another kind's.");
    }

    // … existing branch 3 (round-trip through yaml) unchanged from here …
    if (rawConfig is not System.Collections.IDictionary)
    {
        var found = rawConfig is System.Collections.IEnumerable and not string
            ? "a list"
            : $"the scalar '{rawConfig}'";

        throw new ServiceSourcesConfigurationException(
            $"{Prefix(serviceName)}the per-kind config block must be a block of key/value pairs, " +
            $"but found {found}. Check the indentation under the kind's key.");
    }

    var yaml = Serializer.Serialize(rawConfig);

    try
    {
        return Deserializer.Deserialize<T>(yaml);
    }
    catch (YamlException ex)
    {
        throw new ServiceSourcesConfigurationException(
            $"{Prefix(serviceName)}the per-kind config block is not valid: " +
            (ex.InnerException ?? ex).Message,
            ex);
    }
}

/// <summary>
/// Whether <paramref name="rawConfig"/> can only have come from a <c>WithKind(kind, options)</c>
/// call — i.e. it is none of the shapes YamlDotNet's dynamic deserialization produces. Mirrors the
/// scalar/list test above rather than checking <see cref="System.Collections.IList"/> directly, so
/// the two branches classify every input the same way (design finding 6).
/// </summary>
private static bool CameFromCode(object rawConfig) =>
    rawConfig is not System.Collections.IDictionary
    && rawConfig is not (System.Collections.IEnumerable and not string)
    && rawConfig is not string
    && rawConfig.GetType() is { IsPrimitive: false } and not (
        System.Type t when t == typeof(bool) || t == typeof(decimal) || t == typeof(DateTime)
            || t == typeof(DateTimeOffset) || t == typeof(Guid) || t == typeof(TimeSpan));
```

If the pattern-matching `is not (Type t when …)` syntax above doesn't compile as written (C# pattern
syntax for this exact shape is easy to get subtly wrong), fall back to an explicit `HashSet<Type>` of
YamlDotNet's known boxed-scalar CLR types and test membership — either is fine, but the six
`Parse_YamlProducedScalar_StillReportsScalarShape`/list/dictionary tests above are what has to pass,
not the specific syntax. Run the tests to find out which shape compiles and is correct; do not guess.

- [ ] **Step 4: Run all `LocalKindConfigTests`, confirm every one passes**

- [ ] **Step 5: Run the existing `JavaKindOptions`/`JavaScriptKindOptions` test suites**, to confirm
      the round-trip branch (3) is unchanged for the two shipped kinds:

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Java.Tests test/Aspire.Hosting.ServiceSources.JavaScript.Tests`
Expected: all passed, unchanged from before this task.

- [ ] **Step 6: Build with `-warnaserror`**

- [ ] **Step 7: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/LocalKindConfig.cs \
        test/Aspire.Hosting.ServiceSources.Tests/LocalKindConfigTests.cs
git commit -m "LocalKindConfig.Parse<T>: pass through an already-typed options object (#134)"
```

---

## Task 8: `WithKind`

Implements: Architecture → "Kind options".

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs`
- Modify: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs`

**Interfaces:**
- Produces: `ServiceDefinitionBuilder.WithKind(string kind, object? options = null)` →
  `ServiceDefinitionBuilder`, setting `Kind = kind` and `KindOptions = options`. Depends on Task 7
  (the `Parse<T>` branches) for `options` to be usable as either a dictionary or an already-typed
  instance downstream — this task itself just stores the value.

- [ ] **Step 1: Write the failing test**

```csharp
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
```

- [ ] **Step 2: Run, confirm failure**

- [ ] **Step 3: Implement**

```csharp
private string? _kind;
private object? _kindOptions;

public ServiceDefinitionBuilder WithKind(string kind, object? options = null)
{
    RequireUnset(_kind, nameof(WithKind));
    _kind = kind;
    _kindOptions = options;
    return this;
}
```

In `Build()`, change `Kind = LocalKinds.Dotnet,` to `Kind = _kind ?? LocalKinds.Dotnet,` and add
`KindOptions = _kindOptions,`.

- [ ] **Step 4: Run, confirm pass**

- [ ] **Step 5: Build with `-warnaserror`**

- [ ] **Step 6: Commit**

```bash
git add -u
git commit -m "Add WithKind (#134)"
```

---

## Task 9: Thread `ServiceDefinition` through every consumer of `ServiceMetadata`

Implements: finding 3's blast-radius table (re-audited against the current tree, since the design's
own line numbers are against commit `48d2f8f` and have drifted).

**Files** (audited fresh during this plan's own recon — grep again before starting, since more
commits may have landed since):
- Modify: `src/Aspire.Hosting.ServiceSources/IServiceSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ContainerSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/KubernetesSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/UrlSource.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/DeferredCheckout.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs` (the
  `LoadedConfig`/`ResolveService` return shape — coordinate with Task 10, which changes this file
  more substantially; do the mechanical retype here, leave composition logic to Task 10)
- Test: every existing test file constructing a `ServiceMetadata` to pass into one of the above
  (run `grep -rl "ServiceMetadata" test/` first to get the current list — this is the "19 test files"
  the design counted, also drifted; trust the grep, not the design's count)

**Interfaces:**
- Consumes: `ServiceDefinition` (Task 1).
- Produces: nothing new — every signature keeps its shape, only the parameter/field *type* changes
  from `ServiceMetadata` to `ServiceDefinition`.

This is mechanical and total: `ServiceMetadata` stays yaml-only (Task 1's `ToDefinition()` is the
only place it's read after this task), and `ServiceDefinition` is what every non-yaml-loading line of
code sees from here on. The transformation is the same at every site:

```diff
- IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
-     IDistributedApplicationBuilder builder, string serviceName, ServiceMetadata metadata, ServiceDeveloperConfig config);
+ IResourceBuilder<IResourceWithServiceDiscovery> Resolve(
+     IDistributedApplicationBuilder builder, string serviceName, ServiceDefinition definition, ServiceDeveloperConfig config);
```

(Parameter renamed `metadata` → `definition` throughout, matching the type; every read of
`metadata.X` inside a method body becomes `definition.X` with no other change — `ServiceDefinition`
carries the identical property set `ServiceMetadata` does, by construction in Task 1.)

- [ ] **Step 1: Grep for the current, real blast radius**

Run: `grep -rln "ServiceMetadata" src/Aspire.Hosting.ServiceSources/ test/`

Expected: a list close to, but not necessarily identical to, the Files section above and the
design's own table — some of `RawServiceCatalog.cs`/`PrepareMetadata.cs`/`LocalKindConfig.cs` will
appear (XML doc comments only, per the design's own note — confirm each hit is a comment, not code,
before skipping it) and `ServiceCatalogLoader.cs`/`ServiceCatalog.cs`/`ServiceMetadata.cs` itself
will appear (these three **stay** on `ServiceMetadata` — they're the yaml DTOs and the reflection
roots that must keep pointing at it).

- [ ] **Step 2: For each source file (not test) in the list, apply the mechanical retype**

One file per commit, so a compile error in the middle of the walk is easy to bisect. Suggested order
(consumer before its callers, though the compiler doesn't care): `IServiceSource.cs` →
`ContainerSource.cs` → `KubernetesSource.cs` → `UrlSource.cs` → `LocalGitCheckout.cs` (retype its
`ServiceMetadata` parameters; check first whether it takes the whole object or only specific fields
— if only fields like `Repository`/`DefaultRef`, consider whether retyping the parameter is even
necessary versus just passing those fields, and note which you chose) → `LocalCheckoutPrefetch.cs` →
`DeferredCheckout.cs` → `LocalProjectSource.cs` (the largest one, read in full during this plan's
recon — every `ServiceMetadata metadata` parameter and `metadata.` access becomes
`ServiceDefinition definition`/`definition.`) → `ServiceSourcesConfigCache.cs`'s
`ResolveService`/`LoadedConfig` shape (coordinate with Task 10 — this file's *composition logic*
changes there; here, only retype `ResolveService`'s return tuple's first element and anywhere it
flows).

After each file: `dotnet build src/Aspire.Hosting.ServiceSources -c Release` and fix the next
compile error the change surfaces, rather than trying to predict every call site up front.

- [ ] **Step 3: Once `src/` builds clean, fix every test file the same way**

Run: `grep -rln "ServiceMetadata" test/` again — this list is now the test-side blast radius. Each
test constructing a `new ServiceMetadata { … }` to pass into a source's `Resolve` (or a helper that
does) needs either `.ToDefinition("<some path>")` appended, or to construct a `ServiceDefinition`
directly with `CatalogOrigin.FromYaml("<test path>")` — prefer the former where the test already
has a `ServiceMetadata` builder helper, to keep the diff small.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test -c Release`
Expected: every test that passed before this task still passes. A test whose assertion depended on
the *type name* `ServiceMetadata` appearing somewhere (unlikely, but check) needs updating to
`ServiceDefinition`.

- [ ] **Step 5: Build with `-warnaserror`**

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "Thread ServiceDefinition through every non-yaml consumer of catalog metadata (#134)"
```

---

## Task 10: Composition — freeze, merge, and the three new errors

Implements: Architecture → "Composition, freezing, and the errors"; finding 2 (extended no-catalog
message); finding 11 (hand-rolled `OrdinalIgnoreCase` merge).

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs`
  (`LoadedConfig.Load`, `LoadedConfig.Catalog`'s type, `ResolveService`)
- Modify: `src/Aspire.Hosting.ServiceSources/Config/Catalog/CatalogOrigin.cs` or a new
  `src/Aspire.Hosting.ServiceSources/Config/Catalog/CodeServiceCatalog.cs` — the merged-map container
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogCompositionTests.cs`
- Test: extend `test/Aspire.Hosting.ServiceSources.Tests/Catalog/AddServiceCatalogTests.cs` (the
  ordering error, deferred from Task 2)

**Interfaces:**
- Consumes: `ServiceCatalogBuilder.Freeze()` (Task 2), `ServiceMetadata.ToDefinition()` (Task 1).
- Produces: `CodeServiceCatalog` (internal sealed class, one property:
  `IReadOnlyDictionary<string, ServiceDefinition> Services`). `LoadedConfig.Catalog` changes type
  from `ServiceCatalog` to `CodeServiceCatalog`. `ResolveService`'s return type changes from
  `(ServiceMetadata Metadata, …)` to `(ServiceDefinition Definition, …)` (mechanical continuation of
  Task 9, landing here because this is where the merge that produces it lives).

- [ ] **Step 1: Write the failing composition tests**

```csharp
// test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogCompositionTests.cs
using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class CatalogCompositionTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [TestBuilderDefaults.DisableConfigReloadArg],
        });

    [Fact]
    public void CodeOnlyCatalog_NoYamlFile_ResolvesService()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" } } }""");
        var builder = CreateBuilder(dir);

        builder.AddServiceCatalog(c => c.AddService("inventory").WithUrl("https://example.com"));

        var (definition, _) = ServiceSourcesConfigCache.ResolveService(builder, "inventory");

        Assert.Equal("https://example.com", definition.Url!.Url);
    }

    [Fact]
    public void YamlOnlyCatalog_Unchanged()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              inventory:
                url:
                  url: https://example.com
            """);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" } } }""");
        var builder = CreateBuilder(dir);

        var (definition, _) = ServiceSourcesConfigCache.ResolveService(builder, "inventory");

        Assert.Equal("https://example.com", definition.Url!.Url);
    }

    [Fact]
    public void BothDisjoint_BothResolve()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              inventory:
                url:
                  url: https://example.com
            """);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "inventory": { "source": "url" }, "payments": { "source": "url" } } }""");
        var builder = CreateBuilder(dir);
        builder.AddServiceCatalog(c => c.AddService("payments").WithUrl("https://payments.example"));

        var (yamlDef, _) = ServiceSourcesConfigCache.ResolveService(builder, "inventory");
        var (codeDef, _) = ServiceSourcesConfigCache.ResolveService(builder, "payments");

        Assert.Equal("https://example.com", yamlDef.Url!.Url);
        Assert.Equal("https://payments.example", codeDef.Url!.Url);
    }

    [Fact]
    public void SameNameInBothCatalogs_ThrowsNamingBothSources()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var yamlPath = Path.Combine(dir, "servicesources.yaml");
        File.WriteAllText(yamlPath,
            """
            services:
              payments:
                url:
                  url: https://example.com
            """);
        var builder = CreateBuilder(dir);
        builder.AddServiceCatalog(c => c.AddService("payments").WithUrl("https://other.example"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "payments"));

        Assert.Contains("payments", ex.Message, StringComparison.Ordinal);
        Assert.Contains("code", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(yamlPath, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeAndYamlNamesDifferingOnlyByCase_IsTheDuplicateError_NotAmbiguousCatalogSpelling()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              Payments:
                url:
                  url: https://example.com
            """);
        var builder = CreateBuilder(dir);
        builder.AddServiceCatalog(c => c.AddService("payments").WithUrl("https://other.example"));

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "payments"));

        Assert.DoesNotContain("declares more than once", ex.Message, StringComparison.Ordinal);
        Assert.Contains("payments", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void YamlDeclaresTwoCaseVariants_StillAmbiguousCatalogSpellingError_NotArgumentException()
    {
        // Design finding 11: the merge must not silently turn this into an ArgumentException by
        // building the merged map as new Dictionary<string, ServiceDefinition>(OrdinalIgnoreCase).
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              orders:
                url:
                  url: https://a.example
              Orders:
                url:
                  url: https://b.example
            """);
        File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
            """{ "services": { "orders": { "source": "url" } } }""");
        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "orders"));

        Assert.Contains("declares more than once", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddServiceCatalog_LookupAgainstYamlOnly_StaysOrdinal()
    {
        // Design finding 11: the map itself stays Ordinal, so AddService("Orders") against a yaml
        // "orders:" still reports not-found — unchanged from today.
        var dir = TempDirectories.CreateSubdirectory().FullName;
        File.WriteAllText(Path.Combine(dir, "servicesources.yaml"),
            """
            services:
              orders:
                url:
                  url: https://example.com
            """);
        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "Orders"));

        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NeitherCatalogDeclaresAnything_ExtendedNoCatalogMessage()
    {
        var dir = TempDirectories.CreateSubdirectory().FullName;
        var builder = CreateBuilder(dir);

        var ex = Assert.Throws<ServiceSourcesConfigurationException>(
            () => ServiceSourcesConfigCache.ResolveService(builder, "anything"));

        Assert.Contains("servicesources.yaml", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddServiceCatalog", ex.Message, StringComparison.Ordinal);
    }
}
```

Also add to `AddServiceCatalogTests.cs` (deferred from Task 2):

```csharp
[Fact]
public void AddServiceCatalog_CalledAfterFirstAddService_ThrowsOrderingError()
{
    var dir = TempDirectories.CreateSubdirectory().FullName;
    File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
        """{ "services": { "orders": { "source": "url" } } }""");
    var builder = CreateBuilder(dir);
    builder.AddServiceCatalog(c => c.AddService("orders").WithUrl("https://example.com"));
    ServiceSourcesConfigCache.ResolveService(builder, "orders"); // reads and freezes the catalog

    var ex = Assert.Throws<ServiceSourcesConfigurationException>(
        () => builder.AddServiceCatalog(c => c.AddService("payments")));

    Assert.Contains("already", ex.Message, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public void AddServiceCatalog_NotCalledAfterAddBackingService_NoOrderingError()
{
    // Design finding 1: AddBackingService does not read the catalog, so it must not trip the
    // ordering check. Guards the wrong rule an earlier draft of the design carried from creeping
    // back in.
    var dir = TempDirectories.CreateSubdirectory().FullName;
    var builder = CreateBuilder(dir);
    builder.AddBackingService("db", () => builder.AddConnectionString("db"));

    // Must not throw.
    builder.AddServiceCatalog(c => c.AddService("orders").WithUrl("https://example.com"));
}
```

- [ ] **Step 2: Run, confirm all the new tests fail** (compile or assertion failures — the merge
      doesn't exist yet)

- [ ] **Step 3: Implement `CodeServiceCatalog`**

```csharp
// src/Aspire.Hosting.ServiceSources/Config/Catalog/CodeServiceCatalog.cs
namespace Aspire.Hosting.ServiceSources.Config.Catalog;

/// <summary>
/// The composed catalog every downstream reader sees — code-declared and yaml-declared entries
/// merged into one map, keyed and compared exactly as <see cref="ServiceCatalog.Services"/> was:
/// <c>Ordinal</c>. Design finding 3 — <c>ServiceCatalog.Services</c> cannot be retyped because
/// <see cref="ServiceCatalogLoader"/> binds <see cref="ServiceCatalog"/> with YamlDotNet directly.
/// </summary>
internal sealed class CodeServiceCatalog
{
    public required IReadOnlyDictionary<string, ServiceDefinition> Services { get; init; }
}
```

- [ ] **Step 4: Rewrite `LoadedConfig.Load` for composition**

Replace the current `LoadedConfig` class in `ServiceSourcesConfigCache.cs`:

```csharp
internal sealed class LoadedConfig
{
    public required Catalog.CodeServiceCatalog Catalog { get; init; }

    public required DeveloperConfiguration DeveloperConfig { get; init; }

    public static LoadedConfig Load(IDistributedApplicationBuilder builder)
    {
        // Freeze first, under the same lock CodeCatalogFor's Configure calls take — an
        // AddServiceCatalog reaching this builder after this point contributes nothing (design:
        // "A ServiceCatalogBuilder captured and mutated after this point contributes nothing").
        var codeEntries = CodeCatalogFor(builder).Freeze();

        var yamlPath = Path.Combine(builder.AppHostDirectory, "servicesources.yaml");
        var yamlExists = File.Exists(yamlPath);

        if (codeEntries.Count == 0 && !yamlExists)
        {
            throw new ServiceSourcesConfigurationException(
                $"No service catalog found. Declare one with builder.AddServiceCatalog(catalog => …) " +
                $"before the first AddService(…) call, or create '{yamlPath}'.");
        }

        var merged = new Dictionary<string, Catalog.ServiceDefinition>(StringComparer.Ordinal);

        foreach (var (name, definition) in codeEntries)
        {
            merged[name] = definition;
        }

        if (yamlExists)
        {
            var yamlCatalog = ServiceCatalogLoader.Load(yamlPath);

            foreach (var (name, metadata) in yamlCatalog.Services)
            {
                // OrdinalIgnoreCase comparison done by hand (design finding 11) — never
                // new Dictionary<string, ServiceDefinition>(StringComparer.OrdinalIgnoreCase), which
                // would (a) throw a raw ArgumentException on the second yaml case-variant instead of
                // the existing AmbiguousCatalogSpellingError, and (b) make lookup itself
                // case-insensitive, silently changing behavior for an existing yaml-only AppHost
                // (acceptance criterion 3).
                var codeCollision = merged.Keys.FirstOrDefault(
                    existing => string.Equals(existing, name, StringComparison.OrdinalIgnoreCase));

                if (codeCollision is not null)
                {
                    var codeOrigin = merged[codeCollision].Origin;
                    throw new ServiceSourcesConfigurationException(
                        $"Service '{name}' is declared twice: in {codeOrigin.Describe()} and in " +
                        $"'{yamlPath}'. A service belongs to one catalog; remove one of the two. To vary a " +
                        "service per developer, set its 'source' in 'servicesources.local.json' instead.");
                }

                // A yaml-vs-yaml case collision (e.g. "orders:" and "Orders:" both in the same file)
                // is NOT caught here — ServiceCatalogLoader/ServiceCatalog.Services is Ordinal, so
                // both entries survive the yaml load and land here as two distinct keys. Left to
                // DeveloperConfiguration.CanonicalizeToCatalog's existing AmbiguousCatalogSpellingError,
                // reached via ReadFrom below with the merged (Ordinal) key set — unchanged from today.
                merged[name] = metadata.ToDefinition(yamlPath);
            }
        }

        var catalog = new Catalog.CodeServiceCatalog { Services = merged };

        // The catalog first, and its names handed over: unchanged from before this task, and now
        // covers code-declared names too (design: "canonical-spelling reconciliation covers
        // code-declared names too").
        return new LoadedConfig
        {
            Catalog = catalog,
            DeveloperConfig = DeveloperConfiguration.ReadFrom(builder, catalog.Services.Keys),
        };
    }
}
```

- [ ] **Step 5: Update `ResolveService`**

```csharp
public static (Catalog.ServiceDefinition Definition, ServiceDeveloperConfig DeveloperConfig) ResolveService(
    IDistributedApplicationBuilder builder, string serviceName)
{
    var loaded = LoadedFor(builder);

    if (!loaded.Catalog.Services.TryGetValue(serviceName, out var definition))
    {
        throw new ServiceSourcesConfigurationException(
            $"Service '{serviceName}' was not found in the service catalog.");
    }

    if (!loaded.DeveloperConfig.Services.TryGetValue(serviceName, out var developerConfig))
    {
        throw loaded.DeveloperConfig.NotConfiguredError(serviceName);
    }

    if (string.IsNullOrWhiteSpace(developerConfig.Source))
    {
        throw loaded.DeveloperConfig.NotConfiguredError(serviceName);
    }

    return (definition, developerConfig);
}
```

(The not-found message change — "was not found in the service catalog" instead of naming
`servicesources.yaml` unconditionally — is itself part of Task 11's Origin-aware pass; it's made
here because this line has to change anyway for the type rename, and leaving the old wording would
immediately fail Task 11's blanket test. If Task 11 wants richer per-origin phrasing here, it edits
this line again — that's expected, not a conflict.)

- [ ] **Step 6: Add the ordering check to `AddServiceCatalog`**

Update `AddServiceCatalog` (from Task 2) to check the "already read" flag before configuring:

```csharp
[AspireExport(RunSyncOnBackgroundThread = true)]
public static IDistributedApplicationBuilder AddServiceCatalog(
    this IDistributedApplicationBuilder builder, Action<Catalog.ServiceCatalogBuilder> configure)
{
    DeveloperConfigFileSource.EnsureRegistered(builder);

    Config.ServiceSourcesConfigCache.CodeCatalogFor(builder).Configure(configure);

    return builder;
}
```

The "already read" check has to live where the read is tracked — extend `CodeCatalogAccumulator`
(added in Task 2) with the flag, and have `LoadedConfig.Load`'s call to `Freeze()` set it:

```csharp
// In ServiceSourcesConfigCache.cs, extend CodeCatalogAccumulator:
private sealed class CodeCatalogAccumulator
{
    private readonly object _gate = new();
    private bool _frozen;

    public Catalog.ServiceCatalogBuilder Builder { get; } = new();

    public void Configure(Action<Catalog.ServiceCatalogBuilder> configure)
    {
        lock (_gate)
        {
            if (_frozen)
            {
                throw new ServiceSourcesConfigurationException(
                    "AddServiceCatalog(…) was called after the service catalog had already been read, so " +
                    "its entries could not be seen. Because a service is resolved as it is added, the " +
                    "catalog must be declared before the first AddService(…) — near the top of the AppHost, " +
                    "next to UseDeferredCheckout() and UseJava().");
            }

            configure(Builder);
        }
    }

    public IReadOnlyDictionary<string, Catalog.ServiceDefinition> FreezeOnce()
    {
        lock (_gate)
        {
            _frozen = true;
            return Builder.Freeze();
        }
    }
}
```

And in `LoadedConfig.Load`, replace `CodeCatalogFor(builder).Freeze()` with a call through the
accumulator's `FreezeOnce()` — which means `CodeCatalogFor` needs to expose the accumulator, not just
the builder. Adjust:

```csharp
private static CodeCatalogAccumulator CodeCatalogAccumulatorFor(IDistributedApplicationBuilder builder) =>
    CodeCatalogs.GetValue(builder, static _ => new CodeCatalogAccumulator());

internal static Catalog.ServiceCatalogBuilder CodeCatalogFor(IDistributedApplicationBuilder builder) =>
    CodeCatalogAccumulatorFor(builder).Builder;
```

And in `LoadedConfig.Load`, `var codeEntries = CodeCatalogAccumulatorFor(builder).FreezeOnce();`
instead of `CodeCatalogFor(builder).Freeze()`.

**Note the design's own caveat, worth re-stating in a code comment where this lands:** this error
does *not* latch — it's raised outside `ConfigLoader<LoadedConfig>.Load`, in `AddServiceCatalog`
itself, and would re-throw per call if reached twice, but in practice there is only one such call
reachable per builder in the ordering-violation case (the first `AddServiceCatalog` after resolution
throws and the AppHost stops).

- [ ] **Step 7: Run every test in this task, confirm all pass**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~Catalog"`

- [ ] **Step 8: Run the full suite**

Run: `dotnet test -c Release`
Expected: everything that passed before still passes — this task changes `LoadedConfig`'s shape,
which every existing `ServiceCatalogLoaderTests`/`ServiceSourcesConfigCacheTests` test touches
indirectly.

- [ ] **Step 9: Build with `-warnaserror`**

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "Compose code and yaml catalogs, with the duplicate/ordering/no-catalog errors (#134)"
```

---

## Task 11: Origin-aware error messages — the blanket guard, then fix whatever it finds

Implements: finding 5, driven by a test rather than a fixed list (Global Constraints explains why).

**Files:**
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogErrorMessageTests.cs`
- Modify: whichever files the test in Step 3 actually flags — do not pre-guess the list; the test
  is the source of truth. Candidates identified during this plan's own recon (Global Constraints and
  earlier grep), to check first: `Sources/ContainerSource.cs`, `Sources/KubernetesSource.cs`,
  `Sources/UrlSource.cs`, `Sources/EndpointScheme.cs` (needs an origin-description parameter threaded
  in from its two callers, not a hardcoded literal), `Java/JavaKindOptions.cs`,
  `Config/DeveloperConfiguration.cs` (the near-miss note), `Git/LocalGitCheckout.cs`
  (`ContainedNameRuleAndRemedy`, which has no per-call origin context at all — generalize its wording
  instead of threading one in, since it's a bare static property), `ServiceEndpointExtensions.cs`
  (`Explain`, which also has no origin readily available at its call site — a runtime helper reading
  `ServiceSourceAnnotation` off an already-resolved `IResource`, not a `ServiceDefinition` — generalize
  its wording too rather than extending `ServiceSourceAnnotation` with an origin field, which would be
  a larger, separately-arguable change outside this task's scope).

**Interfaces:**
- Modifies whatever the test flags. `EndpointScheme.Resolve`'s signature likely needs to accept the
  origin-description string (or a `CatalogOrigin`) instead of hardcoding `"servicesources.yaml's
  {source}.scheme"` — its two callers (`ContainerSource`, `KubernetesSource`) both already have a
  `ServiceDefinition` (post-Task 9) with an `Origin` in hand at the call site.

- [ ] **Step 1: Write the blanket guard test**

```csharp
// test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogErrorMessageTests.cs
using Aspire.Hosting;
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Config;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

/// <summary>
/// Design finding 5: a code-declared service must never see "servicesources.yaml" in an error
/// message. Exercises every reachable failure this package can throw against a code-only catalog and
/// asserts none of them names a file that doesn't exist for that AppHost.
/// </summary>
public class CatalogErrorMessageTests
{
    private static IDistributedApplicationBuilder CreateBuilder(string appHostDirectory) =>
        DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ProjectDirectory = appHostDirectory,
            Args = [TestBuilderDefaults.DisableConfigReloadArg],
        });

    private static void AssertNoYamlMention(Action act)
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(act);
        Assert.DoesNotContain("servicesources.yaml", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServiceNotDeclared() =>
        AssertNoYamlMention(() =>
        {
            var dir = TempDirectories.CreateSubdirectory().FullName;
            var builder = CreateBuilder(dir);
            builder.AddServiceCatalog(c => c.AddService("orders").WithUrl("https://example.com"));
            ServiceSourcesConfigCache.ResolveService(builder, "typo'd-name");
        });

    [Fact]
    public void ContainerMissingImage() =>
        AssertNoYamlMention(() =>
        {
            var dir = TempDirectories.CreateSubdirectory().FullName;
            File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
                """{ "services": { "svc": { "source": "container" } } }""");
            var builder = CreateBuilder(dir);
            // WithContainer requires image and port at the call site, so this exercises the case
            // where the entry declares no container block at all but is resolved as one.
            builder.AddServiceCatalog(c => c.AddService("svc").WithUrl("https://example.com"));
            var (definition, config) = ServiceSourcesConfigCache.ResolveService(builder, "svc");
            Aspire.Hosting.ServiceSources.Sources.ContainerSource.ResolveContainerConfig("svc", definition, config);
        });

    [Fact]
    public void KubernetesMissingService() =>
        AssertNoYamlMention(() =>
        {
            var dir = TempDirectories.CreateSubdirectory().FullName;
            var builder = CreateBuilder(dir);
            builder.AddServiceCatalog(c => c.AddService("svc").WithUrl("https://example.com"));
            var (definition, config) = ServiceSourcesConfigCache.ResolveService(builder, "svc");
            Aspire.Hosting.ServiceSources.Sources.KubernetesSource.BuildPortForwardArgs(
                "svc", definition, config, new Aspire.Hosting.ServiceSources.PortAllocation.SocketPortAllocator(),
                out _, out _);
        });

    [Fact]
    public void UrlNotConfigured() =>
        AssertNoYamlMention(() =>
        {
            var dir = TempDirectories.CreateSubdirectory().FullName;
            var builder = CreateBuilder(dir);
            builder.AddServiceCatalog(c => c.AddService("svc").WithContainer("nginx", 80));
            var (definition, config) = ServiceSourcesConfigCache.ResolveService(builder, "svc");
            Aspire.Hosting.ServiceSources.Sources.UrlSource.ResolveUrl("svc", definition, config);
        });

    [Fact]
    public void UnsupportedScheme() =>
        AssertNoYamlMention(() =>
        {
            // WithContainer exposes no `scheme` parameter in this stage (Task 5), so a
            // code-declared service can never reach an invalid scheme through the public builder
            // today — construct the ServiceDefinition directly instead. Still a real regression
            // guard: it exercises the message EndpointScheme.Resolve produces for a Code-origin
            // definition, which is exactly what this task's fix has to get right, and it is the
            // test that already covers the day WithContainer (or WithKubernetes) grows a scheme
            // parameter of its own.
            var dir = TempDirectories.CreateSubdirectory().FullName;
            var builder = CreateBuilder(dir);
            var definition = new ServiceDefinition
            {
                Repository = "",
                Project = "",
                Kind = "dotnet",
                Container = new ContainerMetadata { Image = "nginx", Port = 80, Scheme = "ftp" },
                Origin = CatalogOrigin.Code,
            };
            var config = new ServiceDeveloperConfig { Source = "container" };
            new Aspire.Hosting.ServiceSources.Sources.ContainerSource().Resolve(builder, "svc", definition, config);
        });

    [Fact]
    public void KindNotRegistered() =>
        AssertNoYamlMention(() =>
        {
            var dir = TempDirectories.CreateSubdirectory().FullName;
            File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
                """{ "services": { "svc": { "source": "local" } } }""");
            var builder = CreateBuilder(dir);
            builder.AddServiceCatalog(c => c.AddService("svc")
                .WithRepository("https://github.com/example/repo")
                .WithKind("nonexistent-kind"));
            var (definition, config) = ServiceSourcesConfigCache.ResolveService(builder, "svc");
            // The kind lookup is a pre-flight check that runs before any git or filesystem work
            // (LocalProjectSource.ResolveKindHandler's own doc comment says so, and it runs before
            // the clone). A git client whose every member but the no-op-by-default
            // EnsureAvailable() throws both suffices and doubles as proof that no checkout was
            // attempted before the error fired.
            new Aspire.Hosting.ServiceSources.Sources.LocalProjectSource(new NeverCalledGitClient())
                .Resolve(builder, "svc", definition, config);
        });

    /// <summary>
    /// Every member but the no-op default <see cref="IGitClient.EnsureAvailable"/> throws — used by
    /// <see cref="KindNotRegistered"/>, which must fail before any of them run.
    /// </summary>
    private sealed class NeverCalledGitClient : Aspire.Hosting.ServiceSources.Git.IGitClient
    {
        public void Clone(string repositoryUrl, string destinationPath, Aspire.Hosting.ServiceSources.Git.IGitProgressSink? progress = null) =>
            throw new NotImplementedException();

        public void Checkout(string repositoryPath, string reference) => throw new NotImplementedException();

        public void Fetch(string repositoryPath) => throw new NotImplementedException();

        public bool HasUncommittedChanges(string repositoryPath) => throw new NotImplementedException();

        public bool IsRefCheckedOut(string repositoryPath, string reference) => throw new NotImplementedException();

        public string? GetOriginUrl(string repositoryPath) => throw new NotImplementedException();
    }
}
```

Both test bodies above are complete and executable as written — no "check the real API before writing this" hedge remains for them; that check has already been done against the real files in this worktree (`ContainerSource.cs`, `LocalProjectSource.cs`, `IGitClient.cs`) while writing this plan. `UnsupportedScheme` deliberately bypasses `ServiceCatalogBuilder`/`WithContainer` to construct its `ServiceDefinition` directly, since the builder's public surface cannot produce an invalid scheme in this stage — see the comment in the test itself.

The remaining "verify against the real generated output" call-outs elsewhere in this plan (Task 14's
TypeScript call shape) are a different kind of hedge — they depend on codegen output from code this
plan doesn't write until Tasks 2–3 land, which cannot be resolved by reading files that exist today.
Every test in this file is fully specified and should compile and run as written.

- [ ] **Step 2: Run the test file, read every failure**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~CatalogErrorMessageTests"`

Expected: several failures, each naming the file and message that still says `servicesources.yaml`.
This list — not the Files section's guessed candidates — is the real work item.

- [ ] **Step 3: Fix each flagged message**

For a site with a `ServiceDefinition`/`Origin` in hand at the call site (the common case after Task
9): replace the hardcoded literal with `definition.Origin.Describe()`, following the exact phrasing
`LoadedConfig.Load`'s duplicate error already established in Task 10 ("in code (AddServiceCatalog)"
/ "in 'path/to/servicesources.yaml'").

For `EndpointScheme.Resolve`, add a parameter:

```csharp
public static string Resolve(
    string serviceName, string source, string? developerScheme, string? catalogScheme,
    Config.Catalog.CatalogOrigin catalogOrigin)
{
    // …
    var origin = fromDeveloperConfig
        ? $"servicesources.local.json's {source}.scheme"
        : $"{catalogOrigin.Describe()}'s {source}.scheme";
    // …
}
```

and update its two call sites in `ContainerSource.cs`/`KubernetesSource.cs` to pass
`definition.Origin`.

For `LocalGitCheckout.ContainedNameRuleAndRemedy` and `ServiceEndpointExtensions.Explain` (no origin
readily available at either site): generalize the wording instead of threading origin through —
e.g. `"Rename the service in its catalog declaration and '{FileName}'."` and `"a 'scheme'/'port'
entry for a 'kubernetes' or 'container' source"` (dropping "in servicesources.yaml"). Neither loses
information a yaml-based AppHost needs — "its catalog declaration" and "a scheme/port entry" are true
regardless of which catalog declared the service.

- [ ] **Step 4: Re-run, repeat Step 3 until the file passes clean**

- [ ] **Step 5: Run the full suite**, to confirm none of the reworded messages broke an existing
      test asserting exact text (`grep -rn "servicesources.yaml" test/` on the messages you changed,
      to find any test still asserting the old wording for a **yaml-declared** service — those
      should still pass unchanged, since the wording only changes when `Origin` is `Code`).

- [ ] **Step 6: Build with `-warnaserror`**

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "Thread catalog Origin into every error message a code-declared service can reach (#134)"
```

---

## Task 12: The reflection test — finding 10's guard

**Files:**
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs`

**Interfaces:**
- Consumes: every public type added in Tasks 2–8 (`ServiceCatalogBuilder`, `ServiceDefinitionBuilder`,
  and — once Task 15's samples are written — confirms no others crept in unexported).

- [ ] **Step 1: Write the test**, mirroring `ServiceConfigurationExportsTests.cs`'s
      `ExportedMethods()` shape (read that file first: `grep -rn "class ServiceConfigurationExportsTests" test/ -A2`
      to confirm the exact reflection pattern it uses, then apply the same shape to the two new
      catalog types):

```csharp
using System.Reflection;
using Aspire.Hosting.ServiceSources.Catalog;

namespace Aspire.Hosting.ServiceSources.Tests.Catalog;

public class CatalogExportsTests
{
    [Fact]
    public void EveryPublicMethodOnServiceCatalogBuilder_IsNonGenericAndReturnsABuilderType()
    {
        foreach (var method in PublicInstanceMethods(typeof(ServiceCatalogBuilder)))
        {
            Assert.False(method.IsGenericMethodDefinition, $"{method.Name} must not be generic.");
            Assert.True(
                method.ReturnType == typeof(ServiceDefinitionBuilder),
                $"{method.Name} should return a builder type, returned {method.ReturnType}.");
        }
    }

    [Fact]
    public void EveryPublicMethodOnServiceDefinitionBuilder_IsNonGenericAndReturnsItself()
    {
        foreach (var method in PublicInstanceMethods(typeof(ServiceDefinitionBuilder)))
        {
            Assert.False(method.IsGenericMethodDefinition, $"{method.Name} must not be generic.");
            Assert.True(
                method.ReturnType == typeof(ServiceDefinitionBuilder),
                $"{method.Name} should return {nameof(ServiceDefinitionBuilder)}, returned {method.ReturnType}.");
        }
    }

    [Fact]
    public void NoTwoExportedCatalogMethods_ShareAGeneratedCapabilityId()
    {
        // Belt-and-suspenders against the Stage 0 regression: build already fails with
        // ASPIREEXPORT013 on a real collision, but this asserts the intent directly rather than
        // relying on the analyzer alone catching a future one.
        var ids = new List<string>();
        foreach (var type in new[] { typeof(ServiceCatalogBuilder), typeof(ServiceDefinitionBuilder) })
        {
            foreach (var method in PublicInstanceMethods(type))
            {
                var export = method.GetCustomAttribute<AspireExportAttribute>();
                if (export is not null)
                {
                    ids.Add(export.Id ?? char.ToLowerInvariant(method.Name[0]) + method.Name[1..]);
                }
            }
        }

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    private static IEnumerable<MethodInfo> PublicInstanceMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName); // exclude property accessors
}
```

Adjust `AspireExportAttribute.Id`'s exact null-handling (whether it's already the derived camelCase
name or null when unset) against what Stage 0's probe and this task's own recon of the packaged
`Aspire.Hosting.xml` established — verify with a quick `dotnet-script`/scratch check if the attribute
shape doesn't match this listing's assumption, rather than guessing.

- [ ] **Step 2: Run, confirm it passes against the real Tasks 2–8 surface**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~CatalogExportsTests"`

- [ ] **Step 3: Build with `-warnaserror`**

- [ ] **Step 4: Commit**

```bash
git add test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs
git commit -m "Add the finding-10 reflection guard for the catalog's exported surface (#134)"
```

---

## Task 13: C# sample with no `servicesources.yaml`

Implements: acceptance criterion 1 (for `dotnet` and `WithKind`); Staging's "C# and TypeScript
samples with no yaml".

**Files:**
- Create: `samples/DemoAppHostCodeCatalog/DemoAppHostCodeCatalog.csproj` (copy
  `samples/DemoAppHost/DemoAppHost.csproj`'s shape — check it first, it likely references
  `CommunityToolkit.Aspire.Hosting.Java` for `UseJava()`; keep that reference since this sample also
  exercises `WithKind("java", …)`)
- Create: `samples/DemoAppHostCodeCatalog/Program.cs`
- Create: `samples/DemoAppHostCodeCatalog/servicesources.local.json.example`
- Modify: `*.slnx` (or `.sln`) to add the new project — check which file format the repo uses
  (`ls *.slnx *.sln 2>/dev/null`) before editing
- Modify: `.github/workflows/ci.yml`'s `build` job if it packs/builds every project by solution
  membership (it likely does, via `dotnet build`/`dotnet test` over the whole solution — confirm by
  reading the job, don't assume a new project needs an explicit CI line)

A **new sample directory**, not a variant of the existing `samples/DemoAppHost`: the existing sample
demonstrates the yaml surface and stays exactly as it is (acceptance criterion 3 — nothing about it
should change in this stage), and a reader comparing the two side by side is the point.

- [ ] **Step 1: Write `Program.cs`**, mirroring `samples/DemoAppHost/Program.cs`'s three services
      (`orders`/local-dotnet, `inventory`/url, `payments`/container+kubernetes) plus the commented-out
      `catalog`/java one, but declared via `AddServiceCatalog` instead of relying on
      `servicesources.yaml`:

```csharp
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.ServiceSources;

var builder = DistributedApplication.CreateBuilder(args);

// Registers the "java" local kind — same call as the yaml-based sample, needed before the first
// AddService() either way. builder.UseJava() is unaffected by where the catalog comes from.
builder.UseJava();

// The whole catalog, declared here instead of in servicesources.yaml. Must come before the first
// AddService() call, which is where it's read.
builder.AddServiceCatalog(catalog =>
{
    catalog.AddService("orders")
        .WithRepository(
            "https://github.com/dotnet/aspire-samples",
            project: "samples/health-checks-ui/HealthChecksUI.ApiService/HealthChecksUI.ApiService.csproj",
            defaultRef: "main");

    catalog.AddService("inventory")
        .WithUrl("https://httpbin.org");

    catalog.AddService("payments")
        .WithContainer("nginxdemos/hello", port: 80, defaultTag: "latest")
        .WithKubernetes("payments", port: 8080);

    // "catalog" (kind: java) is left uncommented here, unlike the yaml sample, because
    // AddServiceCatalog costs nothing extra to declare it — it's servicesources.local.json (below)
    // that decides whether it actually clones anything, exactly as in the yaml sample.
    catalog.AddService("catalog")
        .WithRepository("https://github.com/spring-projects/spring-petclinic", defaultRef: "main")
        .WithKind("java", new Dictionary<string, object>
        {
            ["mavenGoal"] = "spring-boot:run",
            ["port"] = 8080,
        });
});

var orders = builder.AddService("orders")
    .Configure<IResourceWithEnvironment>(r => r.WithEnvironment("DEMO_INJECTED_BY_APPHOST", "true"));

var inventory = builder.AddService("inventory");

var payments = builder.AddService("payments");

builder.Build().Run();
```

The `WithKind("java", new Dictionary<string, object> { … })` shape is deliberate — per Global
Constraints, `JavaKindOptions` stays `internal` until Stage 2's `AsJava` lands, so a raw dictionary
(the same shape yaml's `java:` block produces) is the only way a C# AppHost author configures the
built-in `java` kind through code in this stage. Comment this in the file, briefly, so a reader
doesn't wonder why it isn't `new JavaKindOptions { … }`.

- [ ] **Step 2: Write the `.csproj`**, copying `samples/DemoAppHost/DemoAppHost.csproj` verbatim
      except the assembly/project name — read the original first and match every property
      (`TargetFrameworks`, package references, `IsPackable`, etc.) rather than guessing which matter.

- [ ] **Step 3: Write `servicesources.local.json.example`**, copying
      `samples/DemoAppHost/servicesources.local.json.example`'s shape (same three-to-four services,
      same `source` values) — the developer-config surface is identical regardless of which catalog
      declared the services.

- [ ] **Step 4: Add the project to the solution file**

- [ ] **Step 5: Build the whole solution**

Run: `dotnet build -c Release -warnaserror`
Expected: 0 errors, 0 warnings, and the new project appears in the build output.

- [ ] **Step 6: Run it against a real `servicesources.local.json`** (copy the `.example` file, fill
      in `"source": "url"`/`"container"` for the three uncommented services) and confirm it starts —
      this is a manual smoke check, not an automated test; note the command and outcome in the PR
      body rather than skipping it silently.

Run:
```bash
cp samples/DemoAppHostCodeCatalog/servicesources.local.json.example samples/DemoAppHostCodeCatalog/servicesources.local.json
# edit the copy to set real "source" values for orders/inventory/payments
cd samples/DemoAppHostCodeCatalog && dotnet run
```

- [ ] **Step 7: Commit**

```bash
git add samples/DemoAppHostCodeCatalog/ *.slnx
git commit -m "Add a C# sample declaring its catalog in code, no servicesources.yaml (#134)"
```

---

## Task 14: TypeScript sample with no `servicesources.yaml`

Implements: acceptance criterion 2 (for `dotnet` and `WithKind`).

**Files:**
- Create: `samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts`
- Create: `samples/DemoAppHostTypeScriptCodeCatalog/aspire.config.json` (copy
  `samples/DemoAppHostTypeScript/aspire.config.json`, adjusting only whatever path is
  project-relative)
- Create: `samples/DemoAppHostTypeScriptCodeCatalog/package.json`,
  `samples/DemoAppHostTypeScriptCodeCatalog/tsconfig.apphost.json`,
  `samples/DemoAppHostTypeScriptCodeCatalog/servicesources.local.json.example`,
  `samples/DemoAppHostTypeScriptCodeCatalog/.gitignore` (copy the existing TypeScript sample's
  versions of each — read them first, they're short)
- Modify: `.github/workflows/ci.yml`'s `📘 typescript export surface` job — it currently restores and
  typechecks only `samples/DemoAppHostTypeScript`; add the same restore+typecheck steps for the new
  directory, or extend the job to loop both — read the job in full before deciding, since it has
  several path-specific steps (cache keys, the CLI install) that a second sample directory needs too

**Interfaces:**
- Consumes: the generated ATS SDK — same `.aspire/modules/aspire.mts` shape as the existing sample,
  now including `addServiceCatalog`/`addServiceToCatalog`/`withRepository`/etc. (all confirmed
  crossing ATS cleanly in Stage 0's probe and Task 2's Step 10 confirmation).

- [ ] **Step 1: Copy the existing TypeScript sample's config files**, adjusting only what's
      genuinely different (there should be very little — this is a sibling sample, not a different
      toolchain).

- [ ] **Step 2: Write `apphost.mts`**, the TypeScript mirror of Task 13's `Program.cs`:

```typescript
// TypeScript AppHost demonstrating the catalog authored in code — no servicesources.yaml. See
// samples/DemoAppHostTypeScript/apphost.mts for the yaml-based equivalent.
import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

await builder.addServiceCatalog(async (catalog) => {
  await catalog.withRepository('orders', 'https://github.com/dotnet/aspire-samples', {
    project: 'samples/health-checks-ui/HealthChecksUI.ApiService/HealthChecksUI.ApiService.csproj',
    defaultRef: 'main',
  });

  await catalog.addServiceToCatalog('inventory').then(s => s.withUrl('https://httpbin.org'));

  const payments = await catalog.addServiceToCatalog('payments');
  await payments.withContainer('nginxdemos/hello', 80, { defaultTag: 'latest' });
  await payments.withKubernetes('payments', { port: 8080 });
});

const inventory = await builder.addService('inventory');

const payments = await builder
  .addService('payments')
  .withServiceEnvironment('DEMO_INJECTED_BY_APPHOST', 'true')
  .withServiceReference(inventory)
  .withServiceHttpsEndpoint();

const probeScript =
  'console.log("INVENTORY_URL=" + process.env.INVENTORY_URL);' +
  'console.log("services__inventory__http__0=" + process.env.services__inventory__http__0);';

await builder
  .addExecutable('probe', process.execPath, '.', ['-e', probeScript])
  .withEnvironment('INVENTORY_URL', inventory.getServiceEndpoint())
  .withReference(inventory);

await builder.build().run();
```

**The exact call shapes above (`catalog.withRepository('orders', url, options)` vs.
`catalog.addServiceToCatalog('orders').then(s => s.withRepository(url, options))`) are a guess at
what ATS actually generates for a builder whose `AddService`-equivalent method returns a
`ServiceDefinitionBuilder` chain-starter with no service name yet on it — check the real generated
`.aspire/modules/aspire.mts` after Task 2/3's build (the same way Stage 0's plan Task 3 Step 1 did:
`grep -n "addServiceToCatalog\|withRepository\|withUrl\|withContainer\|withKubernetes"
samples/DemoAppHostTypeScript/.aspire/modules/aspire.mts` once restore has run against this stage's
code) and use the real generated names and call shape, not this listing.** This is the same
verify-before-writing discipline Stage 0's plan used, for the same reason: guessing codegen output
risks asserting a false fact the whole point of Stage 0 was to stop doing.

- [ ] **Step 3: Regenerate and strict-typecheck**

```bash
export PATH="/home/flojon/.claude/jobs/47589aa7/tmp/aspire-cli:$PATH"  # or wherever it was reinstalled
rm -rf samples/DemoAppHostTypeScriptCodeCatalog/.aspire
(cd samples/DemoAppHostTypeScriptCodeCatalog && aspire restore --non-interactive --nologo && npx tsc --noEmit -p tsconfig.apphost.json)
```

Expected: `0 errors`. Fix `apphost.mts` against whatever the real generated names/shapes are (Step
2's note) until this is clean — this is a real compile loop, not a one-shot guess.

- [ ] **Step 4: Update `ci.yml`'s typescript export surface job** to cover the new sample directory
      too (both must restore+typecheck clean in CI, not just locally).

- [ ] **Step 5: Commit**

```bash
git add samples/DemoAppHostTypeScriptCodeCatalog/ .github/workflows/ci.yml
git commit -m "Add a TypeScript sample declaring its catalog in code, no servicesources.yaml (#134)"
```

---

## Task 15: README and CHANGELOG

Implements: Documentation section.

**Files:**
- Modify: `README.md` (new `## Authoring the catalog in code` section after `## Install`; reframe
  `## Getting started` per the design's note)
- Modify: `CHANGELOG.md` (`### Added` for the new API, `### Changed` for yaml becoming optional and
  for `LocalKindConfig.Parse<T>`'s behavior change, under `## [Unreleased]`)

- [ ] **Step 1: Write the README section**, covering (read the design's Documentation section again
      for the exact required contents):
  - The method → `source` mapping table (from the design's "The authoring API" section — copy it
    verbatim, it's already precisely worded).
  - A short C#/TypeScript pair of examples (can lift directly from Task 13/14's samples once their
    final call shapes are confirmed).
  - The three lines a third-party kind package writes to add its own kind, using `WithKind` (name the
    real methods: `builder.AddLocalKind("mykind", new MyKindHandler())` then
    `catalog.AddService("x").WithKind("mykind", myOptions)`).
  - An explicit, unambiguous sentence: a code-declared catalog still needs
    `servicesources.local.json` — this is finding 9, and skipping it would let the feature read as a
    promise it doesn't keep.

- [ ] **Step 2: Reframe `## Getting started`** to present both authoring surfaces before dropping
      into the yaml walkthrough, per the design ("`## Getting started`… is reframed to present the
      two authoring surfaces before dropping into yaml"). Read the current section first
      (`grep -n "^## Getting started" README.md`) to scope the edit precisely rather than rewriting
      more than necessary.

- [ ] **Step 3: Write the CHANGELOG entries**, matching the register of neighboring entries (read
      the last few `### Added`/`### Changed` entries in `## [Unreleased]` first):

```markdown
### Added

- **The service catalog can be authored in code** ([#134]). `builder.AddServiceCatalog(catalog => …)`
  declares services in the AppHost's own language — C# directly, TypeScript (and other guest
  languages) through Aspire's Type System — instead of, or alongside, `servicesources.yaml`. All
  four sources are covered: `WithRepository`/`WithKind` for `"local"`, `WithUrl` for `"url"`,
  `WithContainer` for `"container"`, `WithKubernetes` for `"kubernetes"`. A service declared in both
  catalogs is an error naming both sources — not a merge, and not a silent precedence rule. Yaml
  stays fully supported as one provider; `servicesources.local.json` is still required either way —
  see the README's "Authoring the catalog in code" section.

### Changed

- **`LocalKindConfig.Parse<T>` accepts an already-typed instance** ([#134]). A `WithKind(kind,
  options)` call in code can pass an already-typed options object directly — `Parse<T>` now returns
  it unchanged instead of round-tripping it through yaml, and throws a clearer error naming both
  types if the options object doesn't match what the kind expects. A dictionary (the shape yaml
  itself produces) still round-trips exactly as before.
```

Add `[#134]:` to the link block if it isn't already there from Stage 0's commit — check
`grep -n '^\[#134\]:' CHANGELOG.md` first.

- [ ] **Step 4: Commit**

```bash
git add README.md CHANGELOG.md
git commit -m "Document authoring the service catalog in code (#134)"
```

---

## Self-Review Notes

- **Spec coverage:** every Staging-table Stage 1 item has a task — domain split (Task 1), all four
  `With*` sources plus `AddServiceCatalog` exported from the start (Tasks 2–6), `WithKind` with its
  `Parse<T>` dependency (Tasks 7–8), threading through consumers (Task 9), composition/duplicate/
  ordering/no-catalog errors (Task 10), Origin threading (Task 11), the finding-10 reflection guard
  (Task 12), both no-yaml samples (Tasks 13–14), README/CHANGELOG (Task 15). Acceptance criteria 3
  and 4 are reached in full by Task 10; criteria 1 and 2 (for `dotnet` and `WithKind`) by Tasks 13–14.
  Stage 0's correction is applied in Task 2 Step 4 (`[AspireExport("addServiceToCatalog")]`, not
  `MethodName`).
- **No placeholders:** Task 11's `UnsupportedScheme`/`KindNotRegistered` tests are fully specified,
  verified against the real `ContainerSource.cs`, `LocalProjectSource.cs` and `IGitClient.cs` in this
  worktree (a plan-review round caught these two as under-specified in an earlier draft; both are now
  complete, executable test bodies — see Task 11 Step 1). Task 14's exact ATS call shape remains
  marked "verify against the real generated output before writing this, don't guess" — a genuinely
  different case, since it depends on codegen output from code Tasks 2–3 haven't been implemented yet
  at plan-writing time, which cannot be resolved by reading files that exist today (the same
  discipline Stage 0's own plan used, for the same reason). Every other step has real, complete code.
- **Type consistency:** `ServiceDefinition`, `CatalogOrigin`, `CodeServiceCatalog`,
  `ServiceCatalogBuilder`, `ServiceDefinitionBuilder` are each declared once (Tasks 1, 2, 10) and
  referenced by the same name and shape in every later task. `ResolveService`'s return tuple changes
  from `(ServiceMetadata, ServiceDeveloperConfig)` to `(ServiceDefinition, ServiceDeveloperConfig)`
  across Tasks 9–10 consistently — Task 9 does the mechanical rename, Task 10 is where the tuple's
  *contents* actually change to come from the merged catalog rather than the yaml-only one.
