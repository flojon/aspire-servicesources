# Launch profile selection for the `dotnet` kind Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `dotnet` service can select a named `launchSettings.json` profile, or opt out of profiles entirely, from the yaml catalog (`dotnet:` block) or the code catalog (`AsDotnet`), on the eager, `path` and deferred-checkout paths (#415).

**Architecture:** A typed `DotnetMetadata` block on `ServiceMetadata`/`ServiceDefinition` (not the opaque `KindConfig`), validated once per origin (loader / `Build()`), then applied through Aspire's `AddProject(name, path, Action<ProjectResourceOptions>)` on the eager and `path` sources and through `WithProjectDefaults(ProjectResourceOptions)` plus a name-carrying `DeferredProjectMetadata` placeholder on the deferred path. A shared reader in `LandedLaunchProfile` backs a "named profile must exist" check that runs after the project file resolves (eager, `path`) or after the clone lands (deferred).

**Tech Stack:** C# / .NET (net8.0;net9.0;net10.0), Aspire.Hosting 13.5.2, YamlDotNet, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-28-415-dotnet-launch-profile-design.md` (Approved). Read it first; section numbers below refer to it.

## Global Constraints

- Source lives in `src/Aspire.Hosting.ServiceSources`, tests in `test/Aspire.Hosting.ServiceSources.Tests` (one module: src plus its test project).
- `DotnetMetadata` must be in namespace `Aspire.Hosting.ServiceSources.Config` and expose only the two yaml scalars as public properties (the loader derives yaml keys by reflecting public instance properties; helpers must be methods, not properties).
- yaml keys: `dotnet:` with `launchProfileName` (string) and `excludeLaunchProfile` (bool). Catalog only; no `servicesources.local.json` override.
- API names: `AsDotnet(Action<DotnetKindOptionsBuilder>)`, `WithLaunchProfileName(string)`, `ExcludeLaunchProfile()`. `AsDotnet` does NOT call `WithKind`. New public types are `[AspireExport]`-marked like `AsJava`/`JavaKindOptionsBuilder`.
- Blank yaml `launchProfileName:` means absent; `WithLaunchProfileName(null/blank)` throws immediately; a padded name is used verbatim.
- `excludeLaunchProfile: true` with a non-blank name is a config error; `false` with a name is fine. `dotnet:` on a non-`dotnet` kind is a config error. Both checks live in one shared implementation and run only at load (yaml) / `Build()` (code), never at resolution.
- A named profile must exist in `<project dir>/Properties/launchSettings.json`; no file at all is an error; an unreadable file skips the check.
- All catalog/repository-derived strings in error text go through `Name`, `Raw.Escaped`, `Raw.Join`, `Raw.Literal`/`Raw.Compose` (see neighbouring messages in `ServiceCatalogLoader`).
- An unconfigured service must behave exactly as today (two-argument `AddProject`, `new ProjectResourceOptions()`).
- Build must pass `dotnet build -c Release --no-restore -warnaserror` (public types need XML docs). Cheap test leg every task: `dotnet test -f net10.0` from the repo root (or `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter ...` while iterating).
- Code comments: only the non-obvious WHY, one short sentence, no ticket/PR references.

## Review Focus

Failure modes the spec implies that no headline test covers, most likely first. Each has a test in the owning task.

1. A `dotnet:` block on a service currently using the `url`/`container` source (`defaultSource: url`) must load fine and be ignored, not crash (Task 1, Task 4).
2. `launchSettings.json` containing comments and trailing commas, with the named profile present, must pass the check (Task 3).
3. An unparseable `launchSettings.json` with a named profile must skip our check and not throw from it (Task 3).
4. A profile key containing control characters must not appear raw in the "profile not found" message (Task 3).
5. Deferred, warm checkout (project file already on disk at composition) with a named profile: placeholder must not interfere, Aspire reads the real file (Task 5).

---

## File Structure

- Create `src/Aspire.Hosting.ServiceSources/Config/DotnetMetadata.cs`: yaml-bound block plus `Resolve`/`Validate` statics.
- Modify `Config/ServiceMetadata.cs`, `Config/Catalog/ServiceDefinition.cs`: carry `Dotnet`.
- Modify `Config/ServiceCatalogLoader.cs`: call `DotnetMetadata.Validate`; rewrite stale comment.
- Create `Dotnet/DotnetKindOptionsBuilder.cs`, `Dotnet/DotnetServiceSourcesBuilderExtensions.cs`.
- Modify `Catalog/ServiceDefinitionBuilder.cs`: internal `WithDotnet`, `Build()` validation and copy.
- Modify `Sources/LandedLaunchProfile.cs`: `ProfileNames`. Create `Sources/LaunchProfileCheck.cs`: `Verify`.
- Modify `Sources/LocalProjectSource.cs`, `Sources/PathSource.cs`: `AddDotnetProject`, `ToProjectResourceOptions`.
- Modify `Sources/DeferredProjectMetadata.cs`, `Sources/DeferredCheckout.cs`.
- Docs: `docs/guides/yaml-catalog.md`, `docs/guides/catalog-in-code.md`, `docs/sources/repository.md`, `docs/sources/path.md`, `CHANGELOG.md`.
- Tests: `Config/ServiceCatalogLoaderTests.cs`, `Catalog/ServiceDefinitionBuilderTests.cs`, new `Config/DotnetMetadataTests.cs`, new `Sources/LaunchProfileCheckTests.cs`, `Sources/LocalProjectSourceTests.cs`, `Sources/PathSourceTests.cs`, `Sources/UrlSourceTests.cs`, `Sources/DeferredCheckoutTests.cs`, `Catalog/CatalogExportsTests.cs` (run only).

Task order is real: 1 defines the type all others consume; 2 needs 1; 3 is independent of 2 but needed by 4 and 5; 4 defines `ToProjectResourceOptions` that 5 reuses.

---

### Task 1: `DotnetMetadata`, yaml binding, cross-field validation

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/DotnetMetadata.cs`
- Modify: `Config/ServiceMetadata.cs`, `Config/Catalog/ServiceDefinition.cs`, `Config/ServiceCatalogLoader.cs`
- Test: `test/.../Config/DotnetMetadataTests.cs` (new), `test/.../Config/ServiceCatalogLoaderTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class DotnetMetadata { public string? LaunchProfileName { get; set; } public bool? ExcludeLaunchProfile { get; set; } }`
  - `internal static (string? Name, bool Exclude) DotnetMetadata.Resolve(DotnetMetadata? metadata)`: `Name` is null when absent or blank; `Exclude` is true only when `ExcludeLaunchProfile == true` and there is no name. (When both are set it returns `(name, false)`; that state is unreachable past `Validate` and deliberately not re-checked.)
  - `internal static void DotnetMetadata.Validate(string serviceName, string kind, DotnetMetadata? metadata)` throws `ServiceSourcesConfigurationException`.
  - `ServiceMetadata.Dotnet` (`DotnetMetadata?`), `ServiceDefinition.Dotnet` (`DotnetMetadata?`, `init`), copied in `ToDefinition`.

- [ ] **Step 1: Write the failing tests.** In `DotnetMetadataTests.cs` (namespace `Aspire.Hosting.ServiceSources.Tests.Config`):

```csharp
public class DotnetMetadataTests
{
    [Fact]
    public void Resolve_Null_IsNoNameAndNoExclude() =>
        Assert.Equal((null, false), DotnetMetadata.Resolve(null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankName_IsAbsent(string? name) =>
        Assert.Equal((null, false), DotnetMetadata.Resolve(new() { LaunchProfileName = name }));

    [Fact]
    public void Resolve_PaddedName_IsUsedVerbatim() =>
        Assert.Equal((" http ", false), DotnetMetadata.Resolve(new() { LaunchProfileName = " http " }));

    [Fact]
    public void Resolve_ExcludeTrue_IsExclude() =>
        Assert.Equal((null, true), DotnetMetadata.Resolve(new() { ExcludeLaunchProfile = true }));

    [Fact]
    public void Resolve_ExcludeFalseWithName_SelectsTheName() =>
        Assert.Equal(("http", false), DotnetMetadata.Resolve(new() { LaunchProfileName = "http", ExcludeLaunchProfile = false }));

    [Fact]
    public void Validate_ExcludeTrueWithName_ThrowsNamingServiceAndBothFields()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => DotnetMetadata.Validate(
            "orders", LocalKinds.Dotnet, new() { LaunchProfileName = "http", ExcludeLaunchProfile = true }));
        Assert.Contains("orders", ex.Message);
        Assert.Contains("launchProfileName", ex.Message);
        Assert.Contains("excludeLaunchProfile", ex.Message);
    }

    [Fact]
    public void Validate_ExcludeTrueWithBlankName_IsFine() =>
        DotnetMetadata.Validate("orders", LocalKinds.Dotnet, new() { LaunchProfileName = " ", ExcludeLaunchProfile = true });

    [Fact]
    public void Validate_NonDotnetKind_ThrowsNamingServiceAndKind()
    {
        var ex = Assert.Throws<ServiceSourcesConfigurationException>(() => DotnetMetadata.Validate(
            "orders", "java", new() { LaunchProfileName = "http" }));
        Assert.Contains("orders", ex.Message);
        Assert.Contains("java", ex.Message);
    }

    [Fact]
    public void Validate_NullMetadata_NeverThrows() => DotnetMetadata.Validate("orders", "java", null);
}
```

In `ServiceCatalogLoaderTests.cs`, add (use the file's existing temp-file/`try/finally` style; add a small local helper if none exists) tests named:
`Load_DotnetBlock_BindsBothFields`, `Load_DotnetBlockBlankLaunchProfileName_LoadsAsAbsent` (assert `DotnetMetadata.Resolve(...).Name` is null), `Load_DotnetBlockUnknownKey_ThrowsNamingServiceAndDotnet` (key `launchProfile:`; message contains `orders`, `launchProfile`, `dotnet`), `Load_DotnetBlockOnJavaKind_ThrowsNamingServiceAndKind`, `Load_DotnetBlockExcludeWithName_Throws`, `Load_DotnetBlockScalar_Throws` (`dotnet: http`), `Load_DotnetBlockExcludeNotBool_Throws` (`excludeLaunchProfile: maybe`), and Review Focus 1: `Load_DotnetBlockOnServiceWithUrlDefaultSource_LoadsAndIsIgnored` (service with a `url:` block, `defaultSource: url`, plus `dotnet: { launchProfileName: http }`; asserts it loads and the definition's `Dotnet.LaunchProfileName == "http"`). Definitions come from `catalog.Services["orders"].ToDefinition("f.yaml", "orders", repositories)`.

Also change the comment in the existing `Load_StrayDotnetBlock_ThrowsNamingServiceAndProperty` (it says a `dotnet:` block is always stray) to: `// An unknown key inside 'dotnet:' is still rejected at load, naming the service and block.` Do not change its assertions.

- [ ] **Step 2: Run to verify failure.** `dotnet test test/Aspire.Hosting.ServiceSources.Tests -f net10.0 --filter "FullyQualifiedName~DotnetMetadataTests|FullyQualifiedName~Load_Dotnet"`. Expected: compile failure (`DotnetMetadata` not defined).

- [ ] **Step 3: Implement.** Create `Config/DotnetMetadata.cs`:

```csharp
using Aspire.Hosting.ServiceSources.Messages;

namespace Aspire.Hosting.ServiceSources.Config;

internal sealed class DotnetMetadata
{
    public string? LaunchProfileName { get; set; }

    public bool? ExcludeLaunchProfile { get; set; }

    internal static (string? Name, bool Exclude) Resolve(DotnetMetadata? metadata)
    {
        var name = string.IsNullOrWhiteSpace(metadata?.LaunchProfileName) ? null : metadata!.LaunchProfileName;
        return (name, name is null && metadata?.ExcludeLaunchProfile == true);
    }

    internal static void Validate(string serviceName, string kind, DotnetMetadata? metadata)
    {
        if (metadata is null) return;

        if (kind != LocalKinds.Dotnet)
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': a 'dotnet' block only applies to services of kind 'dotnet', but this service's kind is '{new Name(kind)}'. Remove the 'dotnet' block or change the kind.");

        if (metadata.ExcludeLaunchProfile == true && !string.IsNullOrWhiteSpace(metadata.LaunchProfileName))
            throw ServiceSourcesConfigurationException.For(
                $"Service '{new Name(serviceName)}': 'launchProfileName' and 'excludeLaunchProfile: true' contradict each other (excluding launch profiles ignores any named profile). Drop one of the two.");
    }
}
```

Add `public DotnetMetadata? Dotnet { get; set; }` (one-line summary) to `ServiceMetadata` and `public DotnetMetadata? Dotnet { get; init; }` to `ServiceDefinition`; add `Dotnet = Dotnet,` to `ToDefinition`. In `ServiceCatalogLoader.Load`, right after the `Kind` normalization and before `if (!raw.Services.TryGetValue(...))` (so it also runs when there is no raw entry), add `DotnetMetadata.Validate(name, metadata.Kind, metadata.Dotnet);`. Replace the comment block above `var kindBlockKey = ...` so it says: the built-in `dotnet` kind has no opaque options block; its typed `dotnet:` block is an ordinary `ServiceMetadata` property, validated like `kubernetes:` (delete "so a `dotnet:` block is always stray or misspelled ... silently ignored").

- [ ] **Step 4: Run to verify pass.** Same filter, then `--filter "FullyQualifiedName~ServiceCatalogLoaderTests"` (the reserved-kind tests must still pass; adjust one only if it asserts the opposite of spec section 3). Expected: PASS.

- [ ] **Step 5: Commit.** `git add -A && git commit -m "Add the typed dotnet: catalog block and its cross-field checks (#415)"` with the Co-Authored-By trailer.

---

### Task 2: Code catalog API (`AsDotnet`)

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Dotnet/DotnetKindOptionsBuilder.cs`, `Dotnet/DotnetServiceSourcesBuilderExtensions.cs`
- Modify: `Catalog/ServiceDefinitionBuilder.cs`
- Test: `test/.../Catalog/ServiceDefinitionBuilderTests.cs`; run `Catalog/CatalogExportsTests.cs`

**Interfaces:**
- Consumes: `DotnetMetadata` and `DotnetMetadata.Validate` from Task 1.
- Produces: `public static ServiceDefinitionBuilder AsDotnet(this ServiceDefinitionBuilder, Action<DotnetKindOptionsBuilder>)` (namespace `Aspire.Hosting.ServiceSources`, class `DotnetServiceSourcesBuilderExtensions`); `DotnetKindOptionsBuilder.WithLaunchProfileName(string)` and `.ExcludeLaunchProfile()` both returning the options builder; `internal DotnetMetadata Build()` on the options builder; `internal ServiceDefinitionBuilder ServiceDefinitionBuilder.WithDotnet(DotnetMetadata)`.

- [ ] **Step 1: Write failing tests** in `ServiceDefinitionBuilderTests.cs`, matching that file's construction style (find how it creates a `ServiceDefinitionBuilder` and calls the internal `Build()`):
  - `AsDotnet_WithLaunchProfileName_SetsDefinitionDotnet` (`Build().Dotnet!.LaunchProfileName == "http"`, `Kind == "dotnet"`).
  - `AsDotnet_ExcludeLaunchProfile_SetsExcludeTrue`.
  - `AsDotnet_CalledTwice_ThrowsAlreadyCalled` (message contains `AsDotnet` and `already called`).
  - `AsDotnet_ThenWithKindJava_Build_Throws` and `WithKindJava_ThenAsDotnet_Build_Throws` (either order; message names service and `java`).
  - `AsDotnet_NameAndExclude_Build_ThrowsNamingBothFields`.
  - `WithLaunchProfileName_NullOrBlank_ThrowsImmediately` (`[Theory]` null, "", "  "; message names the service and `WithLaunchProfileName`).
  - `AsDotnet_NoCall_DotnetIsNull`.
  `CatalogExportsTests` needs no new test but must still pass (unique capability ids, builder methods return their own type).

- [ ] **Step 2: Run to verify failure** (`--filter "FullyQualifiedName~AsDotnet|FullyQualifiedName~WithLaunchProfileName"`). Expected: compile failure.

- [ ] **Step 3: Implement.** `Dotnet/DotnetKindOptionsBuilder.cs`: mirror `JavaKindOptionsBuilder` (`[AspireExport(ExposeMethods = true)]`, XML docs, `internal DotnetKindOptionsBuilder(string serviceName)`, private `DotnetMetadata _options = new()`):

```csharp
public DotnetKindOptionsBuilder WithLaunchProfileName(string launchProfileName)
{
    if (string.IsNullOrWhiteSpace(launchProfileName))
        throw ServiceSourcesConfigurationException.For(
            $"Service '{new Name(_serviceName)}': {Raw.Literal(nameof(WithLaunchProfileName))} - a launch profile name is required and cannot be empty or whitespace.");
    _options.LaunchProfileName = launchProfileName;
    return this;
}

public DotnetKindOptionsBuilder ExcludeLaunchProfile()
{
    _options.ExcludeLaunchProfile = true;
    return this;
}

internal DotnetMetadata Build() => _options;
```

`Dotnet/DotnetServiceSourcesBuilderExtensions.cs` (namespace `Aspire.Hosting.ServiceSources`, XML docs with an example):

```csharp
[AspireExport]
public static ServiceDefinitionBuilder AsDotnet(
    this ServiceDefinitionBuilder builder, Action<DotnetKindOptionsBuilder> configure)
{
    var options = new DotnetKindOptionsBuilder(builder.ServiceName);
    configure(options);
    return builder.WithDotnet(options.Build());
}
```
(`ServiceName` is already an internal property on `ServiceDefinitionBuilder`.) In `ServiceDefinitionBuilder` add `private DotnetMetadata? _dotnet;` and:

```csharp
internal ServiceDefinitionBuilder WithDotnet(DotnetMetadata dotnet)
{
    RequireUnset(_dotnet, "AsDotnet");
    _dotnet = dotnet;
    return this;
}
```
In `Build()` (convert to a block body): `DotnetMetadata.Validate(_serviceName, _kind ?? LocalKinds.Dotnet, _dotnet);` first, then set `Dotnet = _dotnet` on the definition.

- [ ] **Step 4: Run to verify pass**: the filter above plus `--filter "FullyQualifiedName~CatalogExportsTests"`. Expected: PASS.

- [ ] **Step 5: Commit** "Add AsDotnet to the code catalog (#415)".

---

### Task 3: Launch-profile reader and the must-exist check

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LandedLaunchProfile.cs`
- Create: `src/Aspire.Hosting.ServiceSources/Sources/LaunchProfileCheck.cs`
- Test: `test/.../Sources/LaunchProfileCheckTests.cs` (new)

**Interfaces:**
- Produces:
  - `internal enum LaunchSettingsState { Absent, Unreadable, Read }` and `internal readonly record struct LaunchProfileNames(LaunchSettingsState State, IReadOnlyList<string> Names)`; `internal static LaunchProfileNames LandedLaunchProfile.ProfileNames(string projectFile)`: object-valued keys of `profiles` in file order, tolerant parse (comments, trailing commas); a parseable file with no `profiles` object is `Read` with no names.
  - `internal static void LaunchProfileCheck.Verify(string serviceName, string projectFile, string? profileName)`: no-op when `profileName` is null or the file is `Unreadable`; throws `ServiceSourcesConfigurationException` when `Absent` or the name is not among the names (ordinal match, no trimming).

- [ ] **Step 1: Write failing tests** in `LaunchProfileCheckTests.cs` with a temp project dir (`TempDirectories.CreateSubdirectory()`; write `Orders.csproj` and `Properties/launchSettings.json`):
  - `Verify_NameNull_NeverReadsTheFile` (no file present, `profileName: null`, no throw).
  - `Verify_NameInFile_Passes`.
  - `Verify_NameMissing_ThrowsListingServiceNameFileAndAvailableProfiles` (message contains `orders`, the configured value, `launchSettings.json`, and both actual profile names).
  - `Verify_NoLaunchSettingsAtAll_ThrowsSayingTheFileIsAbsent`.
  - `Verify_PaddedName_Fails` (`" http "` against `http`).
  - `Verify_MatchIsCaseSensitive` (`HTTP` vs `http` fails).
  - Review Focus 2: `Verify_FileWithCommentsAndTrailingCommas_Passes`.
  - Review Focus 3: `Verify_UnparseableFile_SkipsTheCheck` (`"{ not json"`, no throw).
  - Review Focus 4: `Verify_HostileProfileKey_IsNotReproducedRaw`: a profile key `"evil\u001b[31mred"`, configured name `x`; assert `ex.Message` does not contain `'\u001b'`.
  - `ProfileNames_*`: `Absent` for no file, `Unreadable` for bad JSON, names in file order for a good one, a non-object profile value excluded.

- [ ] **Step 2: Run to verify failure** (`--filter "FullyQualifiedName~LaunchProfileCheckTests"`). Expected: compile failure.

- [ ] **Step 3: Implement.** In `LandedLaunchProfile` add `ProfileNames` beside `Read`. Extract the settings-path expression (`Path.Combine(Path.GetDirectoryName(projectFile) ?? ".", "Properties", "launchSettings.json")`) into a private static `SettingsPath(string projectFile)` and the `JsonDocumentOptions` into a private static field, both used by `Read` and `ProfileNames`, so the two readers cannot diverge. `Read` behaviour must not change (existing `LandedLaunchProfile_*` tests are the guard).

`LaunchProfileCheck.Verify` message shapes (use `Name`, `Raw.Escaped`, `Raw.Join`, `Raw.Compose`):

```
Service 'orders': launch profile 'x' was not found in '<file>'. Profiles in that file: a, b. Fix 'launchProfileName' under the service's 'dotnet' block (or the AsDotnet(o => o.WithLaunchProfileName(...)) call).
Service 'orders': launch profile 'x' was requested but '<file>' does not exist. <same fix sentence>
```
Escape the profile names and the path with `Raw.Escaped`; the configured value with `new Name(...)`. For an empty list say "The file declares no profiles."

- [ ] **Step 4: Run to verify pass**, plus `--filter "FullyQualifiedName~DeferredCheckoutTests"` to confirm `Read` is unchanged. Expected: PASS.

- [ ] **Step 5: Commit** "Read launch profile names and verify a configured one exists (#415)".

---

### Task 4: Eager and `path` sources

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs` (dotnet branch of `Resolve`), `Sources/PathSource.cs` (dotnet branch of `Resolve`)
- Test: `test/.../Sources/LocalProjectSourceTests.cs`, `PathSourceTests.cs`, `UrlSourceTests.cs`

**Interfaces:**
- Consumes: `DotnetMetadata.Resolve` (Task 1), `LaunchProfileCheck.Verify` (Task 3), `ServiceDefinition.Dotnet`.
- Produces:
  - `internal static ProjectResourceOptions? LocalProjectSource.ToProjectResourceOptions(DotnetMetadata? dotnet)`: null when neither option is set, else options with `LaunchProfileName = name`, `ExcludeLaunchProfile = exclude`. `ProjectResourceOptions` is `[Experimental("ASPIREPROJECTS001")]`: wrap uses in the same `#pragma warning disable ASPIREPROJECTS001` (with a justification) the deferred code uses.
  - `internal static IResourceBuilder<ProjectResource> LocalProjectSource.AddDotnetProject(IDistributedApplicationBuilder builder, string serviceName, string projectPath, DotnetMetadata? dotnet)`: runs `LaunchProfileCheck.Verify(serviceName, projectPath, name)`, then two-arg `AddProject` when `ToProjectResourceOptions` is null, else `AddProject(serviceName, projectPath, options => { copy both properties })`.

- [ ] **Step 1: Write failing tests.** Follow the neighbouring tests in each file: they put a project on disk (LocalProjectSourceTests: `FakeGitClient` writes `Orders.csproj`, so write `Properties/launchSettings.json` into the planted checkout first; PathSourceTests: `CreateAppHostDirectoryWithService`), build the definition via `new ServiceMetadata { ..., Dotnet = new() { ... } }.ToDefinition(...)` (add an optional `DotnetMetadata? dotnet = null` parameter to each file's local `Definition(...)` helper), call `Resolve`, and inspect `service.Resource` annotations (`LaunchProfileAnnotation`, `ExcludeLaunchProfileAnnotation`, `DefaultLaunchProfileAnnotation`, `EndpointAnnotation`). Same tests in both files (prefix by source):
  - `Resolve_NamedLaunchProfile_AddsLaunchProfileAnnotationWithThatName` (file has `https` first and `http` second; annotation is `http`; endpoints follow the `http` profile's `applicationUrl`).
  - `Resolve_NamedLaunchProfile_BeatsAppHostDefaultLaunchProfileName` (set `AppHost:DefaultLaunchProfileName=https` via `builder.Configuration` before `Resolve`; the selected `LaunchProfileAnnotation` is `http`).
  - `Resolve_ExcludeLaunchProfile_AddsExcludeAnnotationAndNoProfileEndpoints`.
  - `Resolve_NamedProfileMissingFromFile_ThrowsListingProfiles`.
  - `Resolve_NamedProfileWithNoLaunchSettingsFile_Throws`.
  - `Resolve_NoDotnetBlock_IsUnchanged` (no `LaunchProfileAnnotation`, no `ExcludeLaunchProfileAnnotation`; default-profile endpoints as before).
  - Review Focus 1: in `UrlSourceTests.cs`, `Resolve_DotnetBlockPresent_IsIgnored` (definition with a `Dotnet` block resolved through the `url` source does not throw and behaves as without it).
  - The "both fields set" contradiction is a load/`Build()` error, deliberately not re-tested at resolution (spec section 3).

- [ ] **Step 2: Run to verify failure.** Expected: compile errors on the new `Definition` parameter, then assertion failures.

- [ ] **Step 3: Implement.** Add both helpers to `LocalProjectSource`:

```csharp
#pragma warning disable ASPIREPROJECTS001 // ProjectResourceOptions is [Experimental]; the options overload sets name and exclude independently.
internal static ProjectResourceOptions? ToProjectResourceOptions(DotnetMetadata? dotnet)
{
    var (name, exclude) = DotnetMetadata.Resolve(dotnet);
    return name is null && !exclude
        ? null
        : new ProjectResourceOptions { LaunchProfileName = name, ExcludeLaunchProfile = exclude };
}
#pragma warning restore ASPIREPROJECTS001
```

In `LocalProjectSource.Resolve` replace `builder.AddProject(serviceName, projectPath)` with `AddDotnetProject(builder, serviceName, projectPath, definition.Dotnet)`; same in `PathSource`. Append one sentence to the existing comment above the call: the options overload is used because it sets name and exclude independently. The check runs after `ResolveProjectFile` (already true at both call sites) and before `AddProject`, so a missing named profile never reaches Aspire's silent no-op. If the build shows the `Action<ProjectResourceOptions>` overload has a different shape in 13.5.2, adapt the call but keep the two-arg call for the unconfigured case.

- [ ] **Step 4: Run to verify pass**: `--filter "FullyQualifiedName~LocalProjectSourceTests|FullyQualifiedName~PathSourceTests|FullyQualifiedName~UrlSourceTests"`, then the full `dotnet test -f net10.0`. Expected: PASS.

- [ ] **Step 5: Commit** "Apply the dotnet launch profile options on the eager and path sources (#415)".

---

### Task 5: Deferred-checkout path

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/DeferredProjectMetadata.cs`, `Sources/DeferredCheckout.cs` (`Register`, `RestoreLaunchProfile`, `ReportFailureAsync`)
- Test: `test/.../Sources/DeferredCheckoutTests.cs`

**Interfaces:**
- Consumes: `LocalProjectSource.ToProjectResourceOptions` (Task 4), `DotnetMetadata.Resolve` (Task 1), `LaunchProfileCheck.Verify` (Task 3).
- Produces: `DeferredProjectMetadata(string projectPath, string? launchProfileName = null)`; `RestoreLaunchProfile(IResource resource, ServiceResource facade, string relativeProject, string repoRoot, string? launchProfileName, ILogger logger)`.

- [ ] **Step 1: Write failing tests** in `DeferredCheckoutTests.cs`, reusing its `FakeGitClient` (`WithLaunchSettings(repositoryUrl, json)`), `Definition`/`DevConfig` helpers (add an optional `DotnetMetadata? dotnet = null` to `Definition`), and the existing deferred tests as the model for opting in (`builder.UseDeferredCheckout()`, cold managed checkout), for driving the post-clone step, and for reading `FailedToStart`/logs/environment (see the tests near the `context.EnvironmentVariables["DOTNET_LAUNCH_PROFILE"]` assertions and the failure-notice tests):
  - `Deferred_NamedProfile_SurvivesCompositionWithTheCheckoutAbsent`: `Register` does not throw for `launchProfileName: "http"` while no `.csproj` exists (fails today with Aspire's not-found throw); the resource carries `LaunchProfileAnnotation("http")`.
  - `DeferredProjectMetadata_MissingProjectWithName_ReturnsPlaceholderContainingTheName` (`LaunchSettings!.Profiles["http"].CommandName == "Project"`, no `ApplicationUrl`), `..._ProjectPresent_ReturnsNull`, `..._NoName_KeepsTheEmptyPlaceholder`.
  - `Deferred_NamedProfile_AfterLanding_RestoresThatProfilesEnvironment` (two profiles with different `environmentVariables`; landed environment has the named one's, and `DOTNET_LAUNCH_PROFILE == "http"`).
  - `Deferred_NamedProfile_AppUrlNotDeclared_WarnsWithThatProfilesUrl`.
  - `Deferred_NamedProfileMissingFromLandedRepo_FailsAllResourcesAndDoesNotClaimTheCloneFailed`: every `AllResources` entry `FailedToStart`; the logged text contains the profile message and does not contain `did not complete`.
  - `Deferred_NamedProfileWithNoLaunchSettingsInLandedRepo_Fails`.
  - `Deferred_ExcludeLaunchProfile_RegisterDoesNotThrow` and `Deferred_ExcludeLaunchProfile_ColdRun_RestoresNothingAndWarnsNothing` (annotation present; no profile env; no endpoint warning).
  - Review Focus 5: `Deferred_NamedProfile_WarmCheckout_ResolvesEagerlyWithTheRealProfile` (planted existing checkout with launchSettings; opted in; not deferred; annotation `http`; endpoints from that profile).
  - The existing deferred tests are the guard for the unconfigured case.

- [ ] **Step 2: Run to verify failure** (`--filter "FullyQualifiedName~DeferredCheckoutTests"`). Expected: named-profile tests fail (throw at `Register`).

- [ ] **Step 3: Implement.**
  - `DeferredProjectMetadata`: primary constructor gains `string? launchProfileName = null`; `LaunchSettings` becomes `File.Exists(ProjectPath) ? null : Placeholder()`, where `Placeholder()` returns a fresh `new LaunchSettings()` when the name is null, else one whose `Profiles` maps the name to `new LaunchProfile { CommandName = "Project" }` (adapt to the real settability of `LaunchSettings.Profiles` found at build time). Rewrite the class remark that calls `ExcludeLaunchProfile` "worse" for the cold case: it is now the explicit, chosen behavior when the catalog asks for it; and note a named profile is placeholder-backed so composition passes and the real name is verified after the clone.
  - `Register`: `var (profileName, _) = DotnetMetadata.Resolve(definition.Dotnet);` pass `profileName` to `new DeferredProjectMetadata(projectPath, profileName)`; use `.WithProjectDefaults(LocalProjectSource.ToProjectResourceOptions(definition.Dotnet) ?? new ProjectResourceOptions())`; pass `profileName` into the `RestoreLaunchProfile` callback.
  - `RestoreLaunchProfile`: after `ResolveProjectFile`, call `LaunchProfileCheck.Verify(resource.Name, projectFile, launchProfileName)` before `LandedLaunchProfile.Read`.
  - `ReportFailureAsync`: replace "its checkout was deferred past startup and did not complete, so the service was never started." with wording covering both a failed clone and a failed post-clone check, e.g. "its deferred checkout could not be completed or checked, so the service was never started." Update any test asserting the old text.
  - One-sentence comment in `Register`: `DOTNET_LAUNCH_PROFILE` is set at composition from the catalog name; the post-clone check confirms it before the process starts.

- [ ] **Step 4: Run to verify pass**: the filter above, then the full `dotnet test -f net10.0`. Expected: PASS.

- [ ] **Step 5: Commit** "Support launch profile selection on the deferred checkout path (#415)".

---

### Task 6: Docs, changelog, full verification

**Files:**
- Modify: `docs/guides/yaml-catalog.md`, `docs/guides/catalog-in-code.md`, `docs/sources/repository.md`, `docs/sources/path.md`, `CHANGELOG.md`
- Test: none new (documentation); the verify legs are the test.

- [ ] **Step 1: Docs.** `yaml-catalog.md`: add the `dotnet:` block to the schema with the spec section 3 example and field table (blank name means absent; exact match; the contradiction and missing-profile errors). `catalog-in-code.md`: `AsDotnet` example next to the `AsJava`/`AsJavaScript` text (note it does not call `WithKind`). `repository.md` cold-checkout section (lost profile endpoints): a named profile on a cold run restores that profile's environment and warns for an undeclared `applicationUrl`; `excludeLaunchProfile` is the explicit form of that trade; a named profile outranks `AppHost:DefaultLaunchProfileName`. `path.md`: one paragraph on the option. Build the docs with mkdocs if available locally; otherwise name it not run.

- [ ] **Step 2: Changelog.** Under `## [Unreleased]` add `### Added` with one entry: the `dotnet:` block (`launchProfileName`, `excludeLaunchProfile`), `AsDotnet`, and the two new configuration errors (contradiction, missing profile). Not Breaking/Changed/Fixed (0.7.0 is the last tag; see the file header rules).

- [ ] **Step 3: Verify legs.** From the repo root run and record the output in the notes file: `dotnet restore`, then `dotnet build -c Release --no-restore -warnaserror` (Expected: 0 warnings, 0 errors), then `dotnet test -f net10.0` (Expected: all pass). The full matrix `dotnet test` (net8/9/10) is deferred to Phase 6 per CLAUDE.md. CI-only legs (smoketest scripts, TypeScript SDK typecheck, pack checks) cannot run locally; name them as not run.

- [ ] **Step 4: Commit** "Document the dotnet launch profile options (#415)".
