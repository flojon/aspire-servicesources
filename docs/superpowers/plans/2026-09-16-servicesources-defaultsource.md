# Add a `defaultSource` to the service catalog (#158) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close #158 by letting a catalog entry declare `defaultSource` (yaml) / `.WithDefaultSource(...)`
(code) — the source a developer gets with no `servicesources.local.json` entry at all — projected as
the lowest-precedence configuration layer, excluded from the parallel clone prefetch so it cannot
re-create the #76 clone-storm, and documented as the zero-config trade-off it is: `defaultSource:
local` means every developer, and CI, clones by default unless CI pins its own source.

**Architecture:** One new field, `ServiceDefinition.DefaultSource`, fed from two producers —
`ServiceMetadata.DefaultSource` (yaml, via `ToDefinition()`) and
`ServiceDefinitionBuilder.WithDefaultSource` (code) — both validated against the same closed
vocabulary `DeveloperConfigShape.Service.SourceNames` already defines. One consumer projects it:
`ServiceSourcesConfigCache.LoadedConfig.Load` stages a `MemoryConfigurationSource` carrying
`ServiceSources:Services:<name>:source = <default>` for every service with no value yet claimed by
any real layer, and inserts it strictly below `servicesources.local.json` (`Sources.Insert(0, ...)`
called after `DeveloperConfigFileSource.EnsureRegistered`). The pre-insert snapshot of each key is
also how `LoadedConfig.DefaultedServiceNames` is built — the provenance set
`LocalCheckoutPrefetch.Run`'s candidate filter excludes, closing both the clone-storm hazard and the
`UnusedCheckoutsMessage`/`FailedCheckoutMessage` false-report acceptance items with the same one
filter (they are built only from services that survived that filter). No new branch is added to
`ResolveService` or to `DeveloperConfiguration.NotConfiguredError` — both already read
`DeveloperConfiguration.Services` however it got populated.

**Tech Stack:** C# / .NET (net8.0, net9.0, net10.0 multi-target), xUnit, Aspire.Hosting 13.5.2 (pinned
floor, `Directory.Build.props:74`), YamlDotNet, bash (the two smoketest scripts).

**Spec:** [docs/superpowers/specs/2026-09-16-servicesources-defaultsource-design.md](../specs/2026-09-16-servicesources-defaultsource-design.md)

## Global Constraints

- **Only the bare `source` string is ever projected** — never a per-source field (`local.path`,
  `url.url`, etc.). The projection code in Task 3 must only ever write a
  `ServiceSources:Services:<name>:source` key; adding any other key here would recreate the #161
  stale-field problem this design is built to avoid.
- **`DeveloperConfigFileSource.EnsureRegistered(builder)` must be called explicitly, and before**
  computing or inserting the defaults layer (spec finding 5). `Sources.Insert(0, ...)` puts whichever
  call happens *later in time* at the lowest precedence — reversing the call order would silently let
  a catalog default outrank a developer's own `servicesources.local.json`.
- **No code change to `DeveloperConfiguration.NotConfiguredError`/`NothingConfiguredError`** (spec
  finding 3, decided explicitly). The projection re-bases `Services.Count == 0` to "no default and no
  explicit entry, for any service" as a side effect of where defaults enter the pipeline. Task 3 adds
  a regression test proving this; it must not add special-casing to either method.
- **No code change to `UnusedCheckoutsMessage`/`FailedCheckoutMessage`'s message bodies** (spec
  finding 4). Both are built only from `_servicesOnCheckout`, which is populated only for services
  that survive `LocalCheckoutPrefetch.Run`'s candidate filter — so the one filter added in Task 4
  closes both acceptance items. Task 4 adds tests proving this; it must not add per-message
  special-casing for a defaulted service.
- **No runtime notice on a defaulted resolution.** Per the spec's Reviewer decisions, question 1:
  stay silent. Do not wire this into `ServiceSourcesWarnings`.
- **ATS/export safety:** `ServiceDefinitionBuilder` carries `[AspireExport(ExposeMethods = true)]` at
  the class level, so `WithDefaultSource` is exported automatically. Its shape (one non-generic
  `string` parameter, returns `ServiceDefinitionBuilder`) is identical to the already-shipped
  `WithUrl`, so no new ATS shape is introduced and no probe is needed (spec "Scope decision"). Task 2
  still updates `CatalogExportsTests.ExportedIds_MatchTheKnownSurface`'s hardcoded id list — that
  test is a deliberate drift guard (design finding 10) and fails on any new export it wasn't told
  about, which is by design, not a defect to route around.
- **Comment style:** short, WHY-only, no implementation narrative, no PR/issue references inside
  shipped code comments (issue numbers belong in commit messages and this plan) — matching the
  existing style already in every file this plan touches.
- Run `dotnet build -c Release -warnaserror` and `dotnet test -c Release` (all three target
  frameworks) after every task. Run `./scripts/smoketest-config-layers.sh` and
  `./scripts/smoketest-local-source.sh` at least once before the PR is opened and again before Phase
  6 land — both are directly exercised by this ticket (config layering; the `"local"` clone-storm
  exclusion) per the notes file's own verify-leg analysis. `typecheck-typescript` is CI-only unless
  the Aspire CLI and npm are confirmed available locally.

## File Structure

- **Modify** `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs` — add `DefaultSource`,
  carry it through `ToDefinition()`.
- **Modify** `src/Aspire.Hosting.ServiceSources/Config/Catalog/ServiceDefinition.cs` — add
  `DefaultSource`.
- **Modify** `src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs` — validate/normalize
  `defaultSource` in the existing per-service loop.
- **Modify** `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs` — add
  `WithDefaultSource`.
- **Modify** `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs` — the projection
  (`LoadedConfig.DefaultedServiceNames` + the config-layer insert in `LoadedConfig.Load`).
- **Modify** `src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs` — one more
  `.Where` on the candidate filter.
- **Modify** `README.md` — "Getting started" and "Authoring the catalog in code" sections.
- **Modify** `CHANGELOG.md` — one `### Added` entry plus its link-block line.
- **Modify** `scripts/smoketest-config-layers.sh` — a `defaultSource` layer, asserted end to end.
- **Modify** `scripts/smoketest-local-source.sh` — a real clone through `defaultSource: local` with
  no `servicesources.local.json` entry at all.
- **Modify** (polish, low-risk) `samples/DemoAppHostCodeCatalog/Program.cs`,
  `samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts`,
  `samples/DemoAppHost/servicesources.yaml` — demonstrate `WithDefaultSource`/`defaultSource` on
  the declared-only `"catalog"` entry each sample already uses to demonstrate catalog-authoring-only
  surface (never added, so this is zero-risk to what the samples actually run).
- **Test:** `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`
- **Test:** `test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/ServiceDefinitionTests.cs`
- **Test:** `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs`
- **Test:** `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs`
- **Test:** `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogCompositionTests.cs`
- **Test:** `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalCheckoutPrefetchTests.cs`

No new files.

---

## Task 1: Yaml schema — `defaultSource` field, validation, `ToDefinition` carry-through

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/Catalog/ServiceDefinition.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/ServiceDefinitionTests.cs`

**Interfaces:**
- Produces: `ServiceMetadata.DefaultSource : string?`, `ServiceDefinition.DefaultSource : string?`.
- Consumes: `DeveloperConfigShape.Service.SourceNames` (already exists) for validation.

- [ ] **Step 1: Write the failing tests**

In `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`, add after
`Load_ParsesServicesFromYaml`:

```csharp
[Fact]
public void Load_ServiceWithDefaultSource_SetsTheField()
{
    var path = Path.GetTempFileName();
    File.WriteAllText(path, """
        services:
          orders:
            repository: https://github.com/company/orders
            project: src/Orders.Api/Orders.Api.csproj
            defaultSource: local
        """);

    try
    {
        var (catalog, _) = ServiceCatalogLoader.Load(path);

        Assert.Equal("local", catalog.Services["orders"].DefaultSource);
    }
    finally
    {
        File.Delete(path);
    }
}

[Fact]
public void Load_ServiceWithBlankDefaultSource_TreatedAsAbsent()
{
    var path = Path.GetTempFileName();
    File.WriteAllText(path, """
        services:
          orders:
            repository: https://github.com/company/orders
            project: src/Orders.Api/Orders.Api.csproj
            defaultSource:
        """);

    try
    {
        var (catalog, _) = ServiceCatalogLoader.Load(path);

        Assert.Null(catalog.Services["orders"].DefaultSource);
    }
    finally
    {
        File.Delete(path);
    }
}

[Fact]
public void Load_ServiceWithInvalidDefaultSource_ThrowsNamingTheFourValues()
{
    var path = Path.GetTempFileName();
    File.WriteAllText(path, """
        services:
          orders:
            repository: https://github.com/company/orders
            project: src/Orders.Api/Orders.Api.csproj
            defaultSource: bogus
        """);

    try
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => ServiceCatalogLoader.Load(path));

        Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bogus", ex.Message, StringComparison.Ordinal);
        Assert.Contains("local", ex.Message, StringComparison.Ordinal);
        Assert.Contains("url", ex.Message, StringComparison.Ordinal);
        Assert.Contains("kubernetes", ex.Message, StringComparison.Ordinal);
        Assert.Contains("container", ex.Message, StringComparison.Ordinal);
    }
    finally
    {
        File.Delete(path);
    }
}
```

Update the existing schema-completeness test `Load_EveryKnownPropertyOnOneService_LoadsWithoutError`
(the reflection-derived unknown-property sets must accept `defaultSource` automatically — this test
proves it, and also reserves it as a kind name via `IsReservedKindName`): add
`defaultSource: local` to its yaml literal, and add `Assert.Equal("local", orders.DefaultSource);`
to its assertions.

In `test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/ServiceDefinitionTests.cs`: this file has
a **reflection-driven drift guard** (`ServiceMetadataProperties_AllHaveMatchingServiceDefinitionProperty`)
that already fails the moment `ServiceMetadata.DefaultSource` exists without a matching
`ServiceDefinition.DefaultSource` — no test edit needed for that guard itself. But its value-level
companion, `ToDefinition_CopiesEveryPropertyValue_ReflectionDriven`, and the manual test
`ToDefinition_CopiesEveryServiceMetadataProperty`, build a `ServiceMetadata` literal that never sets
`DefaultSource`, so both would pass vacuously (null-to-null) without exercising the new field. Add
`DefaultSource = "url",` to the `ServiceMetadata` literal in **both** tests, and add this assertion to
`ToDefinition_CopiesEveryServiceMetadataProperty`:

```csharp
Assert.Equal(metadata.DefaultSource, definition.DefaultSource);
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~Load_ServiceWithDefaultSource|FullyQualifiedName~Load_ServiceWithBlankDefaultSource|FullyQualifiedName~Load_ServiceWithInvalidDefaultSource|FullyQualifiedName~Load_EveryKnownPropertyOnOneService|FullyQualifiedName~ServiceMetadataProperties_AllHaveMatchingServiceDefinitionProperty|FullyQualifiedName~ToDefinition_CopiesEveryServiceMetadataProperty|FullyQualifiedName~ToDefinition_CopiesEveryPropertyValue_ReflectionDriven" -f net8.0`

Expected: the three new tests **FAIL to compile or fail at runtime** (`ServiceMetadata` has no
`DefaultSource` member yet); once the property exists but before `ServiceDefinition` gets one, the
drift-guard test **FAILS** naming the missing `ServiceDefinition.DefaultSource`; the updated schema
test and the two updated `ToDefinition` tests **FAIL** on the new assertions.

- [ ] **Step 3: Add the field to `ServiceMetadata` and `ServiceDefinition`**

In `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs`, add after `DefaultRef`:

```csharp
    public string? DefaultRef { get; set; }

    /// <summary>
    /// The source (<c>"local"</c>/<c>"url"</c>/<c>"kubernetes"</c>/<c>"container"</c>) a developer
    /// gets when nothing configures this service's source explicitly, projected as the
    /// lowest-precedence configuration layer by
    /// <see cref="ServiceSourcesConfigCache.LoadedConfig.Load"/>. Validated against the same closed
    /// vocabulary a developer's own <c>source:</c> value is, in <see cref="ServiceCatalogLoader.Load"/>.
    /// A blank scalar is normalized to absent there, the same way an empty developer-config field
    /// means "unset" elsewhere in this codebase.
    /// </summary>
    public string? DefaultSource { get; set; }
```

In `src/Aspire.Hosting.ServiceSources/Config/Catalog/ServiceDefinition.cs`, add after `Kind`:

```csharp
    public required string Kind { get; init; }

    /// <summary>
    /// Fed from <see cref="ServiceMetadata.DefaultSource"/> (yaml) or
    /// <see cref="Catalog.ServiceDefinitionBuilder.WithDefaultSource"/> (code) — see
    /// <see cref="ServiceSourcesConfigCache.LoadedConfig.Load"/> for how this is projected into
    /// configuration.
    /// </summary>
    public string? DefaultSource { get; init; }
```

- [ ] **Step 4: Carry it through `ToDefinition`**

In `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs`'s `ToDefinition`, add
`DefaultSource = DefaultSource,` to the object initializer (alongside `Kind = Kind,`).

- [ ] **Step 5: Validate and normalize in `ServiceCatalogLoader.Load`**

In `src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs`, in the per-service loop, insert
between the existing `Kind` normalization and the `raw.Services.TryGetValue` check:

```csharp
            // YamlDotNet assigns null for an empty `kind:` scalar, overriding the "dotnet" default —
            // normalize before it's used as a dictionary key or compared against raw property names.
            if (string.IsNullOrWhiteSpace(metadata.Kind))
            {
                metadata.Kind = LocalKinds.Dotnet;
            }

            // A blank scalar means "no default" — a catalog author clearing a default they no
            // longer want should not have to delete the line (matches
            // DeveloperConfiguration.NormalizeBlankToAbsent's rule for a developer's own fields).
            if (string.IsNullOrWhiteSpace(metadata.DefaultSource))
            {
                metadata.DefaultSource = null;
            }
            else if (!DeveloperConfigShape.Service.SourceNames.Contains(metadata.DefaultSource))
            {
                throw new ServiceSourcesConfigurationException(
                    $"Service '{name}': defaultSource value '{metadata.DefaultSource}' is not a valid " +
                    "source. Expected one of: " + string.Join(", ", DeveloperConfigShape.Service.SourceNames) + ".");
            }

            if (!raw.Services.TryGetValue(name, out var rawService))
            {
                continue;
            }
```

(`DeveloperConfigShape` is in the same `Aspire.Hosting.ServiceSources.Config` namespace as
`ServiceCatalogLoader` — no new `using` needed.)

- [ ] **Step 6: Run the tests to verify they pass**

Run the same filter as Step 2. Expected: all **PASS**.

- [ ] **Step 7: Run the full suite and build**

Run: `dotnet build -c Release -warnaserror` — expect 0 errors/warnings.
Run: `dotnet test -c Release` — expect every test green across net8.0/net9.0/net10.0.

- [ ] **Step 8: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs \
        src/Aspire.Hosting.ServiceSources/Config/Catalog/ServiceDefinition.cs \
        src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Config/Catalog/ServiceDefinitionTests.cs
git commit -m "$(cat <<'EOF'
Add defaultSource to the yaml catalog schema (#158)

ServiceMetadata.DefaultSource carries a per-service default through
to ServiceDefinition, validated against the same closed source
vocabulary a developer's own servicesources.local.json entry is. Not
yet consumed anywhere -- ServiceSourcesConfigCache still has to
project it as a configuration layer, and LocalCheckoutPrefetch still
has to exclude default-derived entries from its clone-storm guard.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: Code-catalog twin — `ServiceDefinitionBuilder.WithDefaultSource`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs`

**Interfaces:**
- Consumes: `DeveloperConfigShape.Service.SourceNames`, `RequireUnset` (both already exist).
- Produces: `ServiceDefinitionBuilder WithDefaultSource(string source)`.

- [ ] **Step 1: Write the failing tests**

In `test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs`, add (near the
other `With*` tests, e.g. after `WithUrl_CalledTwice_Throws`):

```csharp
[Fact]
public void WithDefaultSource_SetsTheField()
{
    var definition = new ServiceCatalogBuilder().AddService("orders")
        .WithRepository("https://github.com/example/repo")
        .WithDefaultSource("local")
        .Build();

    Assert.Equal("local", definition.DefaultSource);
}

[Fact]
public void WithDefaultSource_CalledTwice_ThrowsNamingServiceAndBlock()
{
    var chain = new ServiceCatalogBuilder().AddService("orders")
        .WithRepository("https://github.com/example/repo")
        .WithDefaultSource("local");

    var ex = Assert.Throws<ServiceSourcesConfigurationException>(
        () => chain.WithDefaultSource("url"));

    Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
    Assert.Contains("WithDefaultSource", ex.Message, StringComparison.Ordinal);
}

[Fact]
public void WithDefaultSource_InvalidValue_ThrowsNamingTheFourValues()
{
    var chain = new ServiceCatalogBuilder().AddService("orders")
        .WithRepository("https://github.com/example/repo");

    var ex = Assert.Throws<ServiceSourcesConfigurationException>(
        () => chain.WithDefaultSource("bogus"));

    Assert.Contains("orders", ex.Message, StringComparison.Ordinal);
    Assert.Contains("bogus", ex.Message, StringComparison.Ordinal);
    Assert.Contains("local", ex.Message, StringComparison.Ordinal);
    Assert.Contains("url", ex.Message, StringComparison.Ordinal);
    Assert.Contains("kubernetes", ex.Message, StringComparison.Ordinal);
    Assert.Contains("container", ex.Message, StringComparison.Ordinal);
}

[Fact]
public void WithDefaultSource_NotCalled_DefaultSourceStaysNull()
{
    var definition = new ServiceCatalogBuilder().AddService("orders")
        .WithRepository("https://github.com/example/repo")
        .Build();

    Assert.Null(definition.DefaultSource);
}
```

In `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs`'s
`ExportedIds_MatchTheKnownSurface`, add to the `expected` array (beside the other
`ServiceDefinitionBuilder.*` entries):

```csharp
            $"{nameof(ServiceDefinitionBuilder)}.{CamelCase(nameof(ServiceDefinitionBuilder.WithDefaultSource))}",
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~WithDefaultSource|FullyQualifiedName~ExportedIds_MatchTheKnownSurface" -f net8.0`

Expected: the four new builder tests **FAIL to compile** (no such method yet); once the method exists
but before the exports list is updated, `ExportedIds_MatchTheKnownSurface` **FAILS** (the new exported
id is not in the expected list — proving the drift guard actually watches this surface).

- [ ] **Step 3: Add the field and the method**

In `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs`, add a field alongside
`_kind`/`_kindOptions`:

```csharp
    private string? _defaultSource;
```

Add the method after `WithProject` (or any other single-value `With*` — placement among the existing
methods is not load-bearing):

```csharp
    /// <summary>
    /// Declares which source (<c>"local"</c>/<c>"url"</c>/<c>"kubernetes"</c>/<c>"container"</c>) a
    /// developer gets for this service when nothing configures it explicitly — the code-authoring
    /// equivalent of yaml's <c>defaultSource:</c> field. See design "Projection: a config layer, not
    /// a resolver branch".
    /// </summary>
    public ServiceDefinitionBuilder WithDefaultSource(string source)
    {
        RequireUnset(_defaultSource, nameof(WithDefaultSource));

        if (!DeveloperConfigShape.Service.SourceNames.Contains(source))
        {
            throw new ServiceSourcesConfigurationException(
                $"Service '{_serviceName}': {nameof(WithDefaultSource)}('{source}') is not a valid " +
                "source. Expected one of: " + string.Join(", ", DeveloperConfigShape.Service.SourceNames) + ".");
        }

        _defaultSource = source;
        return this;
    }
```

In `Build()`, add `DefaultSource = _defaultSource,` to the object initializer.

- [ ] **Step 4: Run the tests to verify they pass**

Run the same filter as Step 2. Expected: all **PASS**.

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet build -c Release -warnaserror` — expect 0 errors/warnings (no new `[AspireExport]`
surface beyond what `ExposeMethods = true` already covers, so no new `ASPIREEXPORT008` risk).
Run: `dotnet test -c Release` — expect every test green across net8.0/net9.0/net10.0.

- [ ] **Step 6 (polish, optional but recommended): demonstrate it in the code-catalog samples**

Both `samples/DemoAppHostCodeCatalog/Program.cs` and
`samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts` already use their `"catalog"` service entry
(a Java service, declared but never passed to `AddService`/`addService`) purely to demonstrate
catalog-authoring surface that would otherwise go undemonstrated (`WithKind`/`asJava`/`withPrepare`).
Since it is never added, adding `.WithDefaultSource("local")` there is zero-risk to what the samples
actually run:

In `samples/DemoAppHostCodeCatalog/Program.cs`, after the `catalog.AddService("catalog")` chain's
`.WithRepository(...)` call, add `.WithDefaultSource("local")` to the chain.

In `samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts`, after
`await catalogService.withRepository(...)`, add `await catalogService.withDefaultSource('local');`.

In `samples/DemoAppHost/servicesources.yaml`, the yaml-only sample's own `catalog:` entry (also
declared with no `Program.cs` registration call) gains `defaultSource: local` alongside its existing
`defaultRef: main`.

If time-boxed, this step may be dropped without affecting the acceptance checklist — it is
demonstration, not behavior.

- [ ] **Step 7: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Catalog/ServiceDefinitionBuilderTests.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogExportsTests.cs \
        samples/DemoAppHostCodeCatalog/Program.cs \
        samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts \
        samples/DemoAppHost/servicesources.yaml
git commit -m "$(cat <<'EOF'
Add WithDefaultSource, the code-catalog twin of defaultSource (#158)

Symmetric with the yaml field name one-for-one, validated against the
identical closed source vocabulary defaultSource is. #134's design
explicitly deferred this pending #158, noting it would need a
code-catalog twin from day one -- this is that twin. The catalog
still has to project the value as a configuration layer before it
does anything.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: Projection — the bottom configuration layer, and `DefaultedServiceNames`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogCompositionTests.cs`

**Interfaces:**
- Consumes: `DeveloperConfigFileSource.EnsureRegistered`, `DeveloperConfiguration.ServicesKey`
  (both already exist).
- Produces: `LoadedConfig.DefaultedServiceNames : IReadOnlySet<string>`.

- [ ] **Step 1: Write the failing tests**

In `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogCompositionTests.cs`, add:

```csharp
[Fact]
public void YamlCatalogDefaultSource_NoExplicitEntryAnywhere_ResolvesService()
{
    var dir = TempDirectories.CreateSubdirectory().FullName;
    File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
        services:
          inventory:
            url:
              url: https://example.com
            defaultSource: url
        """);
    // No servicesources.local.json at all -- this is the whole point of #158.
    var builder = CreateBuilder(dir);

    var (definition, devConfig) = ServiceSourcesConfigCache.ResolveService(builder, "inventory");

    Assert.Equal("https://example.com", definition.Url!.Url);
    Assert.Equal("url", devConfig.Source);
    Assert.Contains("inventory", ServiceSourcesConfigCache.LoadedFor(builder).DefaultedServiceNames);
}

[Fact]
public void ExplicitEntry_SameValueAsCatalogDefault_IsNotMarkedAsDefaultDerived()
{
    var dir = TempDirectories.CreateSubdirectory().FullName;
    File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
        services:
          inventory:
            url:
              url: https://example.com
            defaultSource: url
        """);
    // A deliberate, reviewed opt-in that happens to match the default -- must stay eligible for
    // the parallel prefetch exactly as an ordinary explicit entry would (design "Default-derived
    // exclusion": value comparison alone cannot tell these apart, which is why the exclusion set
    // is built from a pre-insert snapshot instead).
    File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
        """{ "services": { "inventory": { "source": "url" } } }""");
    var builder = CreateBuilder(dir);

    var (_, devConfig) = ServiceSourcesConfigCache.ResolveService(builder, "inventory");

    Assert.Equal("url", devConfig.Source);
    Assert.DoesNotContain("inventory", ServiceSourcesConfigCache.LoadedFor(builder).DefaultedServiceNames);
}

[Fact]
public void HigherLayerExplicitBlank_OptsOutOfCatalogDefault_ReproducesNotConfiguredError()
{
    var dir = TempDirectories.CreateSubdirectory().FullName;
    File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
        services:
          inventory:
            url:
              url: https://example.com
            defaultSource: url
        """);
    // The one gesture configuration offers for refusing a lower layer's value (design "Opting out
    // of a default"): an explicit blank still shadows the projected default, because a higher
    // layer's provider already has the key at all -- config resolution never falls through to a
    // lower layer once a higher one has an entry, blank or not.
    File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
        """{ "services": { "inventory": { "source": "" } } }""");
    var builder = CreateBuilder(dir);

    var ex = Assert.Throws<ServiceSourcesConfigurationException>(
        () => ServiceSourcesConfigCache.ResolveService(builder, "inventory"));

    Assert.Contains("inventory", ex.Message, StringComparison.Ordinal);
    Assert.Contains("has no source configured", ex.Message, StringComparison.Ordinal);
}

/// <summary>
/// Spec finding 3, made concrete: <see cref="DeveloperConfiguration.NotConfiguredError"/> needs no
/// code change because a projected default already makes <c>Services.Count &gt; 0</c>, which is
/// what re-bases its branch from "nothing configured anywhere" to "this one service, specifically".
/// </summary>
[Fact]
public void OneServiceDefaulted_AnotherServiceUnconfigured_GetsThePerServiceErrorNotTheFileWideOne()
{
    var dir = TempDirectories.CreateSubdirectory().FullName;
    File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
        services:
          inventory:
            url:
              url: https://example.com
            defaultSource: url
          payments:
            url:
              url: https://payments.example
        """);
    // No servicesources.local.json at all: inventory resolves via defaultSource; payments has no
    // entry anywhere and no default of its own.
    var builder = CreateBuilder(dir);

    var ex = Assert.Throws<ServiceSourcesConfigurationException>(
        () => ServiceSourcesConfigCache.ResolveService(builder, "payments"));

    Assert.Contains("payments", ex.Message, StringComparison.Ordinal);
    Assert.Contains("has no source configured", ex.Message, StringComparison.Ordinal);
    Assert.DoesNotContain("No service sources are configured", ex.Message, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~YamlCatalogDefaultSource|FullyQualifiedName~ExplicitEntry_SameValueAsCatalogDefault|FullyQualifiedName~HigherLayerExplicitBlank_OptsOutOfCatalogDefault|FullyQualifiedName~OneServiceDefaulted_AnotherServiceUnconfigured" -f net8.0`

Expected: the first two tests **FAIL to compile** (`LoadedConfig.DefaultedServiceNames` doesn't exist
yet). The third test (`HigherLayerExplicitBlank_OptsOutOfCatalogDefault_ReproducesNotConfiguredError`)
**PASSES already** — the explicit blank entry alone already makes `Services` non-empty and reaches
the per-service error, regardless of whether anything projects `defaultSource`; it is a baseline,
run now and re-run in Step 4 to prove Task 3 does not regress it. The fourth test
(`OneServiceDefaulted_AnotherServiceUnconfigured_...`) **FAILS**: with no projection yet, `inventory`
has no entry at all (nothing configures it and there is no `servicesources.local.json`), so
`Services` is empty for *both* services and `payments` reaches `NothingConfiguredError` instead of
the per-service error — the assertion that the message does **not** contain "No service sources are
configured" fails.

- [ ] **Step 3: Add `DefaultedServiceNames` and the projection**

In `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs`, add usings:

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Aspire.Hosting.ServiceSources.Catalog;
using Aspire.Hosting.ServiceSources.Config.Catalog;
using Aspire.Hosting.ServiceSources.Git;
```

In the `LoadedConfig` class, add a property alongside `HasCodeEntries`:

```csharp
    /// <summary>Whether this AppHost declared at least one service via <c>AddServiceCatalog</c>.</summary>
    public required bool HasCodeEntries { get; init; }

    /// <summary>
    /// Service names whose configured <c>source</c> exists only because
    /// <see cref="Config.Catalog.ServiceDefinition.DefaultSource"/> projected it — never because a
    /// developer, appsettings, an environment variable or the command line configured it.
    /// <see cref="Sources.LocalCheckoutPrefetch"/>'s candidate filter excludes these so a catalog
    /// default cannot re-create the #76 clone-storm for a service nobody actually asked for.
    /// </summary>
    public required IReadOnlySet<string> DefaultedServiceNames { get; init; }
```

In `LoadedConfig.Load`, between `var catalog = new Catalog.CodeServiceCatalog { ... };` and the
`// Read ahead of the warning below` comment, insert:

```csharp
            var catalog = new Catalog.CodeServiceCatalog { Services = merged, Repositories = repositories };

            // Lands the catalog's own defaultSource values as the lowest-precedence configuration
            // layer -- strictly below servicesources.local.json -- so any real layer still wins.
            // Must run before the snapshot below: EnsureRegistered is idempotent, but calling it
            // here guarantees the local file already occupies index 0 before this step's own
            // insert, so this layer lands at index 0 and the file is pushed to index 1 rather than
            // the reverse (design finding 5 — Sources.Insert(0, ...) gives the *lowest* precedence
            // to whichever call happens later in time).
            DeveloperConfigFileSource.EnsureRegistered(builder);

            var defaultedSources = new Dictionary<string, string?>(StringComparer.Ordinal);
            var defaultedServiceNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (name, definition) in catalog.Services)
            {
                if (definition.DefaultSource is not { } defaultSource)
                {
                    continue;
                }

                // Read before this layer exists, so a blank or absent result can only mean nothing
                // has claimed this key yet -- comparing the final merged value instead could not
                // tell a developer's own "local" from a default that merely happens to agree with
                // it (design "Default-derived exclusion": value comparison cannot substitute for
                // provenance).
                var key = $"{DeveloperConfiguration.ServicesKey}:{name}:source";
                if (!string.IsNullOrWhiteSpace(builder.Configuration[key]))
                {
                    continue;
                }

                defaultedSources[key] = defaultSource;
                defaultedServiceNames.Add(name);
            }

            if (defaultedSources.Count > 0)
            {
                builder.Configuration.Sources.Insert(
                    0, new MemoryConfigurationSource { InitialData = defaultedSources });
            }

            // Read ahead of the warning below rather than at the return statement (its usual place):
```

In the final `return new LoadedConfig { ... };`, add `DefaultedServiceNames = defaultedServiceNames,`.

- [ ] **Step 4: Run the tests to verify they pass**

Run the same filter as Step 2. Expected: all four **PASS**.

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet build -c Release -warnaserror` — expect 0 errors/warnings.
Run: `dotnet test -c Release` — expect every test green across net8.0/net9.0/net10.0, including every
pre-existing `CatalogCompositionTests`/`DeveloperConfigurationTests` test (this step touches a shared
composition path — a regression here would show up as a config-layering test elsewhere failing, not
only as a new test failing).

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogCompositionTests.cs
git commit -m "$(cat <<'EOF'
Project defaultSource as the lowest-precedence configuration layer (#158)

ServiceSourcesConfigCache.LoadedConfig.Load stages a
MemoryConfigurationSource carrying "source" for every catalog service
with no value yet claimed by any real layer, and inserts it below
servicesources.local.json. A pre-insert snapshot of each key -- taken
before this layer exists -- both decides what to project and builds
DefaultedServiceNames, the provenance set LocalCheckoutPrefetch needs
to tell a default-derived entry from a developer's own explicit one
of the same value. No branch changes in ResolveService or
NotConfiguredError: both already read DeveloperConfiguration.Services
however it was populated.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: `LocalCheckoutPrefetch` exclusion — the clone-storm guard and the diagnostic fix

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalCheckoutPrefetchTests.cs`

**Interfaces:**
- Consumes: `ServiceSourcesConfigCache.LoadedConfig.DefaultedServiceNames` (Task 3).

This is the task that actually closes the #76 clone-storm hazard and both diagnostic acceptance
items — `UnusedCheckoutsMessage`/`FailedCheckoutMessage` are built only from services that survive
the candidate filter this task changes, so no separate change to either message is needed (Global
Constraints).

- [ ] **Step 1: Write the failing tests**

In `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalCheckoutPrefetchTests.cs`, add (near
`ServiceMarkedLocalButNeverAdded_IsReportedRatherThanClonedSilently`):

```csharp
[Fact]
public void DefaultedToLocal_NeverAdded_IsExcludedFromThePrefetchAndReportsNothing()
{
    var dir = TempDirectories.CreateSubdirectory().FullName;
    File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
        services:
          orders:
            repository: https://example.com/orders.git
            project: Service.csproj
          billing:
            repository: https://example.com/billing.git
            project: Service.csproj
            defaultSource: local
        """);
    // "orders" is explicit; "billing" has no entry anywhere and resolves only through the
    // catalog's own defaultSource.
    File.WriteAllText(Path.Combine(dir, "servicesources.local.json"),
        """{ "services": { "orders": { "source": "local" } } }""");
    var builder = TestHelpers.CreateBuilder(dir);
    var git = new FakeGitClient();

    new LocalProjectSource(git).Resolve(builder, "orders", Definition("orders"), DevConfig());

    // Unlike an explicit "local" entry for a service never added (which IS reported -- see
    // ServiceMarkedLocalButNeverAdded_IsReportedRatherThanClonedSilently -- so the developer knows
    // to remove it), a defaulted service was never anyone's decision to clone: nothing was cloned
    // for it, and nothing is reported.
    Assert.Equal(["https://example.com/orders.git"], git.Cloned);
    Assert.Null(LocalCheckoutPrefetch.For(builder, git).UnusedCheckoutsMessage);
}

[Fact]
public void DefaultedToLocal_ActuallyAdded_ResolvesViaTheDirectNonPrefetchedPath()
{
    var dir = TempDirectories.CreateSubdirectory().FullName;
    File.WriteAllText(Path.Combine(dir, "servicesources.yaml"), """
        services:
          billing:
            repository: https://example.com/billing.git
            project: Service.csproj
            defaultSource: local
        """);
    // No servicesources.local.json at all.
    var builder = TestHelpers.CreateBuilder(dir);
    var git = new FakeGitClient();

    var service = new LocalProjectSource(git).Resolve(builder, "billing", Definition("billing"), DevConfig());

    Assert.NotNull(service);
    // Excluded from the parallel prefetch (default-derived), but AddService still resolves it
    // correctly -- a correct, serialized cold clone on its own thread, the same "not in the
    // prefetch set" path an ordinary cold clone takes.
    Assert.Equal(["https://example.com/billing.git"], git.Cloned);
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~DefaultedToLocal" -f net8.0`

Expected: `DefaultedToLocal_NeverAdded_IsExcludedFromThePrefetchAndReportsNothing` **FAILS** — today
`billing` is cloned in parallel (its projected `"local"` source, from Task 3, is indistinguishable
from an explicit one at this filter), so `git.Cloned` contains both urls and
`UnusedCheckoutsMessage` is non-null naming `billing`. `DefaultedToLocal_ActuallyAdded_ResolvesViaTheDirectNonPrefetchedPath`
should already **PASS** (nothing here depends on the fix; it exists to catch a future regression).

- [ ] **Step 3: Add the exclusion filter**

In `src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs`, in `Run`'s `candidates`
pipeline, add immediately after the existing `"local"`-source filter:

```csharp
        var candidates = config.DeveloperConfig.Services
            // Case-insensitive to agree with how AddService resolves the same value...
            .Where(entry => string.Equals(entry.Value.Source, "local", StringComparison.OrdinalIgnoreCase))
            // A catalog defaultSource projects into the identical configuration key an explicit
            // "local" entry would use (design "Default-derived exclusion"), so without this a
            // defaulted service would re-create the #76 clone-storm this filter exists to prevent
            // -- cloned in parallel whether or not AddService() is ever called for it. Excluded
            // here rather than by comparing values, because an explicit entry naming the identical
            // value is a deliberate opt-in and must stay eligible (see DefaultedServiceNames).
            .Where(entry => !config.DefaultedServiceNames.Contains(entry.Key))
            // A service the developer marked "local" but that the catalog doesn't describe can't be
            // checked out and isn't this phase's problem to report — AddService still rejects it
            // properly if the AppHost actually asks for it.
            .Where(entry => config.Catalog.Services.ContainsKey(entry.Key))
```

- [ ] **Step 4: Run the tests to verify they pass**

Run the same filter as Step 2. Expected: both **PASS**.

- [ ] **Step 5: Run the full suite and build**

Run: `dotnet build -c Release -warnaserror` — expect 0 errors/warnings.
Run: `dotnet test -c Release` — expect every test green across net8.0/net9.0/net10.0, including every
pre-existing `LocalCheckoutPrefetchTests` test (this filter sits in a hot, well-covered path — a
regression here would most likely show up as an existing prefetch test failing).

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs \
        test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalCheckoutPrefetchTests.cs
git commit -m "$(cat <<'EOF'
Exclude default-derived entries from the local-checkout prefetch (#158)

Without this, a catalog defaultSource: local is indistinguishable
from a developer's own explicit entry at LocalCheckoutPrefetch's
candidate filter, re-creating the #76 clone-storm for a service
nobody ever called AddService() for. The same exclusion closes the
UnusedCheckoutsMessage/FailedCheckoutMessage acceptance item too: both
are built only from services that survive this filter, so nothing
else needed to change for a defaulted service to stop being
misreported as an entry to delete from a file it was never in.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: Documentation — README, and the two smoketest scripts

**Files:**
- Modify: `README.md`
- Modify: `scripts/smoketest-config-layers.sh`
- Modify: `scripts/smoketest-local-source.sh`

**Interfaces:** none — documentation and shell-script changes only. "Tests" here are the scripts'
own pass/fail assertions, run manually per Global Constraints rather than through `dotnet test`.

- [ ] **Step 1: README — "Getting started" (yaml)**

In `README.md`, after the yaml block under "**2. Add the shared catalog...**" (the one showing
`defaultRef: main`), add a new paragraph:

````markdown
A catalog entry may also declare `defaultSource`, so a service resolves without a
`servicesources.local.json` entry at all:

```yaml
services:
  orders:
    repository: https://github.com/example/orders
    project: src/Orders.Api/Orders.Api.csproj
    defaultRef: main
    defaultSource: local     # optional; the source a developer gets with no entry of their own
```

**`defaultSource: local` means every developer, and CI, clones and builds that repository by
default** — including on a machine or pipeline that never explicitly asked for it. If CI should not
clone, CI must pin its own source (an environment variable, or its own configuration layer) rather
than relying on the absence of a file.
````

- [ ] **Step 2: README — "Authoring the catalog in code"**

In the same file's "Authoring the catalog in code" section, after the "**Which builder method enables
which `source`**" table, add:

````markdown
`WithDefaultSource(source)` is a separate call, not a `source` value of its own — it names which of
the four a developer gets when nothing configures this service explicitly, the code-authoring
equivalent of yaml's `defaultSource:`:

```csharp
catalog.AddService("orders")
    .WithRepository("https://github.com/example/orders", defaultRef: "main")
    .WithProject("src/Orders.Api/Orders.Api.csproj")
    .WithDefaultSource("local");
```

The same caveat as the yaml field applies: `WithDefaultSource("local")` means every developer, and
CI, clones by default unless CI pins its own source.
````

- [ ] **Step 3: `scripts/smoketest-config-layers.sh` — a `defaultSource` layer, precedence proven end to end**

Add a new sentinel alongside the existing ones (after `COMMANDLINE_SENTINEL`):

```bash
COMMANDLINE_SENTINEL="layer-commandline"
DEFAULT_SENTINEL="layer-catalog-default"
```

In the catalog heredoc ("installing a catalog whose 'orders' entry is the subject"), add
`defaultSource: $DEFAULT_SENTINEL` under `orders`:

```bash
cat > "$apphost_dir/servicesources.yaml" <<EOF
services:
  orders:
    repository: https://github.com/example/orders
    project: SampleService/SampleService.csproj
    defaultRef: main
    defaultSource: $DEFAULT_SENTINEL
  inventory:
    url:
      url: http://unused.invalid
  payments:
    url:
      url: http://unused.invalid
EOF
```

Insert two new steps immediately before `log "1. servicesources.local.json alone"`:

```bash
log "0. catalog defaultSource alone, no servicesources.local.json at all"
rm -f "$apphost_dir/servicesources.local.json" "$apphost_dir/appsettings.json" "$apphost_dir/appsettings.$PROFILE.json"
expect_source "$DEFAULT_SENTINEL" "$(resolved_source default project -)" \
  "the catalog's own defaultSource is the bottom layer, engaged when nothing else configures the service"

log "0b. appsettings.json overrides the catalog default, with still no servicesources.local.json"
write_appsettings "$apphost_dir" appsettings.json "$APPSETTINGS_SENTINEL"
expect_source "$APPSETTINGS_SENTINEL" "$(resolved_source default-appsettings project -)" \
  "an appsettings.json layer outranks the catalog's defaultSource even with no servicesources.local.json entry at all"
```

(Step 1 already does `rm -f "$apphost_dir/appsettings.json" ...` at its own start, so the
`appsettings.json` step 0b wrote is cleaned up before step 1 needs a blank slate — no extra cleanup
required.)

- [ ] **Step 4: `scripts/smoketest-local-source.sh` — a real clone through `defaultSource: local`**

Append a new scenario after "4c. mode: always runs it on every start" (the file's last existing
scenario), reusing `$origin_repo`/`$checkout_dir`/`$MAIN_MARKER`/`run_until_marker` exactly as the
existing scenarios do:

```bash
# ---------------------------------------------------------------------------
# 5. defaultSource: local resolves and clones with no servicesources.local.json entry at all
# ---------------------------------------------------------------------------
log "5. defaultSource: local, no servicesources.local.json entry for 'orders' at all"
rm -rf "$checkout_dir"
cat > "$apphost_dir/servicesources.yaml" <<EOF
services:
  orders:
    repository: $origin_repo
    project: SampleService/SampleService.csproj
    defaultRef: main
    defaultSource: local
  inventory:
    url:
      url: http://unused.invalid
  payments:
    url:
      url: http://unused.invalid
EOF
cat > "$apphost_dir/servicesources.local.json" <<'EOF'
{
  "services": {
    "inventory": { "source": "url" },
    "payments": { "source": "url" }
  }
}
EOF

marker="$(run_until_marker "defaultSource" "$checkout_dir")"
[[ "$marker" == "$MAIN_MARKER" ]] \
  || fail "defaultSource: expected the marker from defaultRef ('$MAIN_MARKER'), got '$marker'"
printf '    resolved, cloned and ran orders with no servicesources.local.json entry for it at all\n'
```

Note: the mocked, fast unit tests in Task 4 are the authoritative coverage for the clone-storm
*exclusion* itself (a defaulted service the AppHost never adds must not be cloned) — this script adds
real-clone coverage for the *positive* path (a defaulted, actually-added service really clones,
builds and runs), since `DemoAppHost`'s `Program.cs` calls `AddService` for a fixed set of three
services and none of the other smoketest scripts vary that set either.

- [ ] **Step 5: Run both scripts**

```bash
./scripts/smoketest-config-layers.sh
./scripts/smoketest-local-source.sh
```

Expected: both **PASS**, printing `PASS: ...` at the end. If `dotnet`/`git` are unavailable in this
environment, note that explicitly rather than claiming they were run.

- [ ] **Step 6: Commit**

```bash
git add README.md scripts/smoketest-config-layers.sh scripts/smoketest-local-source.sh
git commit -m "$(cat <<'EOF'
Document defaultSource and extend both config smoketests (#158)

README: a catalog entry may declare defaultSource (yaml) or call
WithDefaultSource (code) to resolve without a servicesources.local.json
entry -- both places state plainly that defaultSource: local means
every developer, and CI, clones by default unless CI pins its own
source. smoketest-config-layers.sh proves the projected layer sits
below every real layer, including appsettings with no local.json file
at all. smoketest-local-source.sh proves a real clone through
defaultSource: local with no servicesources.local.json entry at all.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: `CHANGELOG.md` entry

**Files:**
- Modify: `CHANGELOG.md`

**Interfaces:** none.

- [ ] **Step 1: Add the entry**

In `CHANGELOG.md`, under `## [Unreleased]` / `### Added`, insert at the top of the list (before the
`java`/`javascript` OpenTelemetry entry):

```markdown
- **Catalog entries can declare a `defaultSource`, so a service resolves without a
  `servicesources.local.json` entry at all** ([#158]). `defaultSource: local` in yaml, or
  `.WithDefaultSource("local")` in a code-declared catalog, projects that value as the
  lowest-precedence configuration layer — below `servicesources.local.json`, so any real layer
  still overrides it. Only the bare source name is projected, never a per-source field, so
  switching a defaulted service to a different source from a higher layer never leaves a stale
  field behind. **A catalog declaring `defaultSource: local` means every developer, and CI, clones
  that repository by default** — pin an explicit source in CI if that is not wanted. A
  default-derived entry is excluded from the parallel checkout prefetch (#76); its first clone, if
  the AppHost actually adds it, runs serialized rather than in parallel with the others.
```

Add the link, in ascending numeric order (between `[#150]` and `[#159]`):

```markdown
[#158]: https://github.com/flojon/aspire-servicesources/issues/158
```

- [ ] **Step 2: Verify the file is still well-formed**

Run: `grep -n "^\[#15" CHANGELOG.md` — expect `[#150]`, `[#158]`, `[#159]`, `[#160]`, `[#161]` in that
order.

- [ ] **Step 3: Full build and test, one more time**

Run: `dotnet build -c Release -warnaserror` — expect 0 errors/warnings.
Run: `dotnet test -c Release` — expect every test green across net8.0/net9.0/net10.0.

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md
git commit -m "$(cat <<'EOF'
Add a CHANGELOG entry for defaultSource (#158)

EOF
)"
```

---

## Self-Review Notes

- **Spec coverage:** Acceptance item 1 (yaml schema) → Task 1. Item 2 (code-catalog twin) → Task 2.
  Item 3 (bottom-layer projection, not a resolver branch) → Task 3. Item 4 (bare `source` only,
  never a per-source field) → Task 3's projection code writes only `:source`; enforced by
  construction, not by a separate check. Item 5 (`NotConfiguredError` re-based) → Task 3's fourth
  test, with the Global Constraints entry stating explicitly that no code change is expected there.
  Item 6 (`LocalCheckoutPrefetch` exclusion) → Task 4. Item 7
  (`UnusedCheckoutsMessage`/`FailedCheckoutMessage`) → Task 4, closed by the same filter, proven by
  the same test. Item 8 (documentation) → Task 5. Item 9 (code-catalog twin, again — #134's own
  deferred item) → Task 2. Item 10 (changelog) → Task 6.
- **Ordering is real, not just a file list:** Task 1 and 2 both produce the one domain field
  (`ServiceDefinition.DefaultSource`) from yaml and code respectively, and neither depends on the
  other — order between them is arbitrary, but both must land before Task 3, which is the first
  consumer (`catalog.Services[...].DefaultSource`). Task 4 depends on Task 3's
  `LoadedConfig.DefaultedServiceNames`. Task 5/6 depend on nothing being incomplete — they document
  and changelog a feature that is fully working once Task 4 lands. This is a straight-line
  dependency chain, not a reordering of independent file edits.
- **Independent verifiability:** every task builds and passes its own filtered test run before the
  full suite, and every task's commit is a coherent, buildable state on its own (Task 1 adds an
  unconsumed domain field; Task 2 adds a second, still-unconsumed producer; Task 3 makes the field
  do something; Task 4 closes the safety hole Task 3's projection would otherwise open; Task 5/6 are
  pure documentation). A reviewer can check out any task's commit and run the full suite green.
- **Deliberately not built:** a runtime notice on a defaulted resolution (Reviewer decision 1: no),
  an `ExecutionContext.IsRunMode` gate (spec non-goal), a `defaults:` catalog-root block (non-goal),
  a `RepositoryMetadata`/`RepositoryBuilder` field (non-goal), any change to `#133`'s kind-nesting
  scope (non-goal). None of these appear as a task above, matching the spec's own scope boundaries.
- **Risk the spec flagged, addressed:** finding 5's ordering hazard is handled by Task 3 calling
  `DeveloperConfigFileSource.EnsureRegistered(builder)` explicitly before the snapshot/insert, with a
  comment naming why — the smoketest addition in Task 5 (step 0/0b) is the one thing in this plan
  that proves the ordering end to end against a real `IConfiguration` chain rather than only against
  the in-process test builder Task 3's xUnit tests use.
- **Placeholder scan:** no TBD/TODO, and no placeholder text left in any shipped file, code comment,
  or documentation snippet — every code block and README paragraph above is the literal text to
  write.
