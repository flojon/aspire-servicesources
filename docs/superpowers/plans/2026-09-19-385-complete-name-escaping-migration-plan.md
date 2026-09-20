# Complete the Structural Name-Escaping Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Finish migrating every `ServiceSourcesConfigurationException` throw and the `KubernetesSecretException`/`Git*Exception` throws onto the structural escaping seam (`ServiceSourcesConfigurationException.For($"...")` with `Name`/`Raw` holes), flip RS0030 from a tolerated warning to a build-breaking error, and close the `ServiceSourcesLog` sink so a caller-controlled name can no longer reach a reader unescaped through *any* path this package owns.

**Architecture:** The seam already exists (`Messages/ServiceTextHandler.cs`, `Messages/Name.cs`, `Messages/Raw.cs`, `ServiceSourcesConfigurationException.For`) and 14 call sites already use it (landed in PR #381). This plan is a mechanical sweep of the remaining 156 `ServiceSourcesConfigurationException(...)` constructor calls across 31 files, two hand-restructured composition sites that don't fit the sweep, then three sequencing tasks (delete `WarningsNotAsErrors`, unwrap `ServiceSourcesWarnings.Label`, build `ServiceSourcesLog`), then two more exception-type sweeps (`KubernetesSecretException`, `Git*Exception`). Each mechanical task uses the same procedure: rename the constructor call to `.For(...)`, rebuild, and let the compiler's `CS1503` (no implicit conversion to `ServiceTextHandler`) point at every caller-controlled hole that still needs `new Name(...)` or a `Raw.*` wrapper — the type system is the oracle, not manual review.

**Tech Stack:** C# / .NET (net8.0;net9.0;net10.0), xUnit, Roslyn banned-API analyzer (RS0030).

**Spec:** GitHub issue [#385](https://github.com/flojon/aspire-servicesources/issues/385) (follow-up to #375 / PR #381).

## Global Constraints

- Every reader-facing message goes through `ServiceSourcesConfigurationException.For(ServiceTextHandler)` (or `.For(ServiceTextHandler, Exception)`) — never the raw two-constructor call, which stays `internal`-only-reachable-from-`.For` after Task 12.
- A hole inside the interpolated string may only be a `Name`, a `Raw`, an `int`, or an `int?` — nothing else compiles (`Messages/ServiceTextHandler.cs:16-46`). Wrap a caller-controlled string in `new Name(value)`; wrap already-safe/joined/never-capped text in the matching `Raw.*` factory (`Raw.Compose`, `Raw.Literal`, `Raw.Escaped`, `Raw.Join`, `Raw.Origin`, `Raw.Cause`).
- Verify progress with the exact command the issue specifies — `--no-incremental` is load-bearing, a plain rebuild silently reports 0:
  ```bash
  dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 \
    | grep -oE "[^ ]+\.cs\([0-9]+,[0-9]+\): warning RS0030" | sort -u | wc -l
  ```
- Run `dotnet restore` once at the start of the session before the first build (a clean checkout needs it; `--no-restore` after that is what makes `--no-incremental` fast enough to iterate with).
- Test with `dotnet test --no-restore -c Release -f net10.0` after each task's build is clean — this worktree's `CLAUDE.md` scopes intermediate rounds to net10.0 only, since TFM-specific failures are rare enough that the other two legs waste time for no signal. Run the full three-TFM `dotnet test --no-restore -c Release` (no `-f`) exactly once, after Task 16's final rebase and before marking the PR ready for review — not after every task. Every "run the full test suite" step below (Task 11 Step 7, Task 12 Step 5, Task 16 Step 7) means the cheap `-f net10.0` leg unless it explicitly says "full three-TFM matrix, once before landing." The suite leaks temp directories on a full run (pre-existing, unrelated to this work) — don't investigate that if seen.
- **Out of scope for this plan:** issue #385 items 7a and 7b (the paste-ready-name contract and the unescaped truncation marker) are explicitly undecided design questions, not implementation tasks. Do not touch `Messages/Name.cs`'s escaping spelling or truncation logic here.

---

## File Map

| File (throw-site count) | Task |
|---|---|
| `Git/LocalGitCheckout.cs` (17), `Git/GitCliClient.cs` (2) | 1 |
| `Java/JavaKindOptions.cs` (16), `Java/JavaLocalResourceKind.cs` (3) | 2 |
| `JavaScript/JavaScriptLocalKind.cs` (15) | 3 |
| `Config/ServiceCatalogLoader.cs` (14, four restructured separately as `+`-concatenation sites), `Config/DeveloperConfigShape.cs` (1) | 4 |
| `Sources/LocalProjectSource.cs` (12), `Sources/DeferredCheckout.cs` (6), `Sources/ContainerSource.cs` (3), `Sources/LocalKindRegistry.cs` (2), `Sources/UrlSource.cs` (1, restructured separately) | 5 |
| `Config/ServiceSourcesConfigCache.cs` (9), `LocalKindConfig.cs` (4) | 6 |
| `BackingServices/KubernetesBackingServiceSource.cs` (9, config-exception throws only), `BackingServices/DirectBackingServiceSource.cs` (3), `BackingServices/ConnectionStringTemplate.cs` (1), `BackingServiceBuilderExtensions.cs` (3) | 7 |
| `Catalog/ServiceCatalogBuilder.cs` (8), `Catalog/ServiceDefinitionBuilder.cs` (4), `Catalog/RepositoryBuilder.cs` (1), `Catalog/PrepareMetadataFactory.cs` (1) | 8 |
| `Prepare/PrepareStep.cs` (6), `Prepare/CheckoutPreparation.cs` (2), `Prepare/PreparePlan.cs` (1), `Prepare/PrepareMode.cs` (1), `Prepare/PrepareMarker.cs` (1) | 9 |
| `ServiceSourcesBuilderExtensions.cs` (2), `ServiceConfigurationExtensions.cs` (2), `ServiceEndpointExtensions.cs` (1), `Config/DeveloperConfiguration.cs` (3, two restructured separately) | 10 |
| `UrlSource.cs:117-126`, `ServiceSourcesWarnings.cs:346-354`, `Config/ServiceCatalogLoader.cs` (four `+`-chain "unknown property" sites, not one — re-grep for `unknown property` before starting), `Config/DeveloperConfiguration.cs` (both `AmbiguousCatalogSpellingError` and `NothingConfiguredError`, re-grep for current lines — see Task 11 Step 4) — seven `+`-concatenation restructures total | 11 |
| `Config/DeveloperConfigValidator.cs:680-712` (the `StringBuilder` composer) + its 2 remaining raw-constructor sites | 11 |
| Delete `WarningsNotAsErrors`, flip RS0030 to error | 12 |
| `ServiceSourcesWarnings.Label` call sites + `EndpointMutationDetector`'s local alias | 13 |
| New `ServiceSourcesLog`, ban `LoggerExtensions.Log*`, convert composers to return `Raw`, close `ConsolePrepareOutputSink`'s `Console.Out.WriteLine` path and `ServiceSourcesWarnings.cs`'s `logger?.LogWarning` call | 14 |
| `KubernetesSecretException` throws (12) | 15 |
| `Git*Exception` throws (5) + `PrepareLaunchException` throws + `KubernetesBackingServiceSource.cs:1051`/`:717` label/cap gaps | 16 |

Line numbers in the issue and above were read against `origin/main` at commit `275ceb4` (2026-09-19) — re-grep before editing since later merges shift lines.

---

### Task 1: Git package cluster

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs` (17 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/Git/GitCliClient.cs` (2 sites)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/LocalGitCheckoutTests.cs`

**Interfaces:**
- Consumes: `ServiceSourcesConfigurationException.For(ServiceTextHandler)`, `ServiceSourcesConfigurationException.For(ServiceTextHandler, Exception)` (both already public/internal in `ServiceSourcesConfigurationException.cs`), `Messages.Name`, `Messages.Raw`.
- Produces: no new public surface — this task only removes RS0030 warnings from these two files.

- [ ] **Step 1: Confirm the baseline warning set for these two files**

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 \
  | grep -E "Git/LocalGitCheckout\.cs|Git/GitCliClient\.cs" | grep RS0030 | sort -u
```
Expected: 19 lines (17 + 2), each naming a `ServiceSourcesConfigurationException(string)` or `(string, Exception)` constructor.

- [ ] **Step 2: Mechanically rename every raw constructor call to `.For`**

For each `throw new ServiceSourcesConfigurationException(` in the two files, change it to `throw ServiceSourcesConfigurationException.For(` (and `new ServiceSourcesConfigurationException(msg, ex)` to `ServiceSourcesConfigurationException.For(msg, ex)` at the two-argument sites). Do this with the editor, not a blind global sed — some of these files also construct *other* types positionally, and a few sites hold the exception in a local before throwing rather than throwing directly; rename the constructor call in place wherever it appears.

- [ ] **Step 3: Rebuild and fix every `CS1503` by wrapping the hole**

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 | grep -E "CS1503|Git/"
```
Each `CS1503: Argument 1: cannot convert from 'string' to 'Aspire.Hosting.ServiceSources.Messages.ServiceTextHandler'` names a file and line. Open it: the interpolated string now has a hole that isn't a `Name`/`Raw`/`int`/`int?`. Follow the convention already established in this same file for what kind of value it is:
  - A repository, checkout, or service name the developer wrote → `new Name(value)`.
  - A filesystem path or URL that must not be truncated → `Raw.Escaped(value)` if it's caller text, or the value is likely already flowing through an existing `Raw`-producing helper in this file (e.g. `PreparePlan.ServiceLabel`/`RepositoryLabel`, which already return escaped/capped strings) — in that case wrap the call result in `Raw.Compose($"{result}")` only if it isn't already typed as `Raw`; check the helper's return type first.
  - `ex.Message` inside a caught-exception message → `Raw.Cause(ex)`.
  - A compile-time-constant fragment with no runtime value → leave as string literal (`AppendLiteral` accepts constants).

  Repeat build-and-fix until the grep for `CS1503` in these two files is empty.

- [ ] **Step 4: Confirm RS0030 is gone from these two files**

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 \
  | grep -E "Git/LocalGitCheckout\.cs|Git/GitCliClient\.cs" | grep RS0030
```
Expected: no output.

- [ ] **Step 5: Run the Git tests**

```bash
dotnet test --no-restore -c Release -f net10.0 --filter "FullyQualifiedName~LocalGitCheckoutTests"
```
Expected: all pass, same count as before this task (this is a message-composition change, not a behavior change — no test should need updating unless it asserts on exact exception text, in which case update the expected string to match the new escaped form).

- [ ] **Step 6: Commit**

```bash
git add src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs src/Aspire.Hosting.ServiceSources/Git/GitCliClient.cs
git commit -m "$(cat <<'EOF'
Migrate Git package exception messages onto the structural escaping seam

Part of #385.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Java cluster

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Java/JavaKindOptions.cs` (16 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/Java/JavaLocalResourceKind.cs` (3 sites)
- Test: search `test/Aspire.Hosting.ServiceSources.Java.Tests/` for the exercising tests.

**Interfaces:**
- Consumes: same as Task 1.
- Produces: nothing new.

- [ ] **Step 1: Baseline** — same grep pattern as Task 1 Step 1, scoped to these two files (expect 19 lines).
- [ ] **Step 2: Rename** every raw constructor call to `.For` in both files (same procedure as Task 1 Step 2).
- [ ] **Step 3: Compiler-fix loop** — rebuild, wrap every `CS1503` hole per the Global Constraints rule, repeat until clean (same procedure as Task 1 Step 3).
- [ ] **Step 4: Confirm zero RS0030** in these two files (same grep as Task 1 Step 4).
- [ ] **Step 5: Run the Java tests**
  ```bash
  dotnet test --no-restore -c Release -f net10.0 --filter "FullyQualifiedName~Java"
  ```
- [ ] **Step 6: Commit**
  ```bash
  git add src/Aspire.Hosting.ServiceSources/Java/JavaKindOptions.cs src/Aspire.Hosting.ServiceSources/Java/JavaLocalResourceKind.cs
  git commit -m "$(cat <<'EOF'
Migrate Java package exception messages onto the structural escaping seam

Part of #385.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
  ```

---

### Task 3: JavaScript cluster

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/JavaScript/JavaScriptLocalKind.cs` (15 sites)
- Test: search `test/Aspire.Hosting.ServiceSources.JavaScript.Tests/` for the exercising tests.

Same six-step procedure as Task 1/2, scoped to this one file. Filter: `dotnet test --no-restore -c Release -f net10.0 --filter "FullyQualifiedName~JavaScript"`.

- [ ] **Step 1: Baseline grep** (expect 15 lines).
- [ ] **Step 2: Rename constructor calls to `.For`.**
- [ ] **Step 3: Compiler-fix loop** for `CS1503`.
- [ ] **Step 4: Confirm zero RS0030 in this file.**
- [ ] **Step 5: Run JavaScript tests.**
- [ ] **Step 6: Commit** with message "Migrate JavaScript package exception messages onto the structural escaping seam".

---

### Task 4: Config catalog-loading cluster

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs` (10 of its 14 sites — leave the four "unknown property" `+`-concatenation sites flagged in the File Map for Task 11)
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigShape.cs` (1 site)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`

- [ ] **Step 1: Baseline grep**, scoped to both files, and note which lines are the `+`-chain sites. There are **four** of them, not one — `grep -n "unknown property" src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs` currently finds them near lines 132-133, 145-146, 230-231 and 253-254 (two pairs: a top-level-property throw and a nested-property throw, each duplicated once for repositories and once for services). Re-grep before editing since lines shift; leave all four alone here — Task 11 restructures them together.
- [ ] **Step 2: Rename** every *other* raw constructor call to `.For` in both files.
- [ ] **Step 3: Compiler-fix loop.** Note `ServiceCatalogLoader.cs` already imports `Aspire.Hosting.ServiceSources.Messages` conventions used by `LocalGitCheckout.IsContainedCheckoutDirectoryName`'s caller — the `name` hole in most of these throws is a catalog/repository key the developer wrote, so it takes `new Name(name)`.
- [ ] **Step 4: Confirm RS0030 count in this file is exactly 4** (the four deliberately-skipped `+`-chain "unknown property" sites).
- [ ] **Step 5: Run the catalog loader tests.**
  ```bash
  dotnet test --no-restore -c Release -f net10.0 --filter "FullyQualifiedName~ServiceCatalogLoaderTests"
  ```
- [ ] **Step 6: Commit** ("Migrate ServiceCatalogLoader/DeveloperConfigShape exception messages onto the structural escaping seam").

---

### Task 5: Sources cluster

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalProjectSource.cs` (12 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/DeferredCheckout.cs` (6 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/ContainerSource.cs` (3 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalKindRegistry.cs` (2 sites)
- Do **not** touch `src/Aspire.Hosting.ServiceSources/Sources/UrlSource.cs` here — its one remaining site is the `+`-chain restructure in Task 11.
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalProjectSourceTests.cs`, `.../DeferredCheckoutTests.cs`, `.../LocalKindRegistryTests.cs`

- [ ] **Step 1: Baseline grep** across the four files (expect 23 lines: 12+6+3+2).
- [ ] **Step 2: Rename** constructor calls to `.For` in the four files.
- [ ] **Step 3: Compiler-fix loop.** `DeferredCheckout.cs` already has 6 migrated composer methods from PR #381 nearby (per issue #385's "Not in scope" note, its structured-argument log sites at `:433/:653/:752/:861/:1042` are Task 14's concern, not this one — leave `logger.LogInformation`/`logger.LogWarning` calls alone here) — follow the wrapping pattern those neighbors already use for consistency.
- [ ] **Step 4: Confirm zero RS0030** in the four files.
- [ ] **Step 5: Run the affected test files.**
  ```bash
  dotnet test --no-restore -c Release -f net10.0 --filter "FullyQualifiedName~LocalProjectSourceTests|FullyQualifiedName~DeferredCheckoutTests|FullyQualifiedName~LocalKindRegistryTests"
  ```
  DeferredCheckout tests have a documented history of flakiness unrelated to message text (fixed via a subscribe-handshake pattern) — a single flaky failure unrelated to escaping is not this task's regression; rerun once before investigating.
- [ ] **Step 6: Commit** ("Migrate Sources package exception messages onto the structural escaping seam").

---

### Task 6: Config cache cluster

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceSourcesConfigCache.cs` (9 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/LocalKindConfig.cs` (4 sites)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/LocalKindConfigTests.cs`

Same six-step procedure. Baseline expects 13 lines. Test filter: `--filter "FullyQualifiedName~LocalKindConfigTests"` plus whatever exercises `ServiceSourcesConfigCache` (grep the test tree for `ServiceSourcesConfigCache` if no dedicated test file surfaces — if none exists, integration coverage through `AddServiceTests.cs` is the gate; run that filter too).

---

### Task 7: BackingServices cluster (config-exception sites only)

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/BackingServices/KubernetesBackingServiceSource.cs` — **only** its `ServiceSourcesConfigurationException` throws (9 sites); leave its `KubernetesSecretException` throws for Task 16.
- Modify: `src/Aspire.Hosting.ServiceSources/BackingServices/DirectBackingServiceSource.cs` (3 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/BackingServices/ConnectionStringTemplate.cs` (1 site)
- Modify: `src/Aspire.Hosting.ServiceSources/BackingServiceBuilderExtensions.cs` (3 sites)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/BackingServices/BackingServiceConfigAuditTests.cs`

- [ ] **Step 1: Baseline grep, filtered to `ServiceSourcesConfigurationException` only** (RS0030's message already distinguishes the symbol name, so `grep RS0030 | grep ServiceSourcesConfigurationException` separates it from any `KubernetesSecretException` warnings the file also carries — expect 16 lines: 9+3+1+3).
- [ ] **Step 2–5:** same rename / compiler-fix / confirm / test procedure as prior tasks. Test filter: `--filter "FullyQualifiedName~BackingServiceConfigAuditTests"`.
- [ ] **Step 6: Commit** ("Migrate BackingServices config-exception messages onto the structural escaping seam").

---

### Task 8: Catalog cluster

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceCatalogBuilder.cs` (8 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs` (4 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/RepositoryBuilder.cs` (1 site)
- Modify: `src/Aspire.Hosting.ServiceSources/Catalog/PrepareMetadataFactory.cs` (1 site)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Catalog/CatalogCompositionTests.cs`, `.../ServiceDefinitionBuilderTests.cs`

Same six-step procedure. Baseline expects 14 lines. Test filter: `--filter "FullyQualifiedName~CatalogCompositionTests|FullyQualifiedName~ServiceDefinitionBuilderTests"`.

---

### Task 9: Prepare cluster

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Prepare/PrepareStep.cs` (6 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/Prepare/CheckoutPreparation.cs` (2 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/Prepare/PreparePlan.cs` (1 site)
- Modify: `src/Aspire.Hosting.ServiceSources/Prepare/PrepareMode.cs` (1 site)
- Modify: `src/Aspire.Hosting.ServiceSources/Prepare/PrepareMarker.cs` (1 site)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Prepare/CheckoutPreparationTests.cs`

Same six-step procedure. Baseline expects 11 lines. Test filter: `--filter "FullyQualifiedName~CheckoutPreparationTests"`.

---

### Task 10: Top-level extensions cluster

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs` (2 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceConfigurationExtensions.cs` (2 sites)
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceEndpointExtensions.cs` (1 site)
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfiguration.cs` — **only** the single site inside `NotConfiguredError` (currently the `new ServiceSourcesConfigurationException($"Service '{serviceName}' has no source configured...` branch, `grep -n "has no source configured" src/Aspire.Hosting.ServiceSources/Config/DeveloperConfiguration.cs` to find its current line). This file's other two sites are both `+`-chain composers left for Task 11: `AmbiguousCatalogSpellingError` (its raw-constructor call is inside the method's body, currently starting around line 361 — re-grep for the method name, not a line range, since it shifts) and `NothingConfiguredError` (currently around line 537 — also a `+`-chain via a target-typed `new(...)`, not a plain interpolation, so do **not** treat it as one of this task's mechanical sites even though it looks like a single simple throw).
- Test: `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs`, `.../AddServiceTests.cs`

- [ ] **Step 1: Baseline grep** across the four files, excluding the two known `+`-chain sites in `DeveloperConfiguration.cs` (expect 6 lines: 2+2+1+1).
- [ ] **Step 2–5:** same procedure. Test filter: `--filter "FullyQualifiedName~ServiceSourcesBuilderExtensionsTests|FullyQualifiedName~AddServiceTests"`.
- [ ] **Step 6: Commit** ("Migrate top-level extension exception messages onto the structural escaping seam").

---

### Task 11: Restructure the `+`-concatenation sites and the `StringBuilder` composer

These don't fit the rename-and-wrap procedure: each builds its message from run-time `+`-concatenated fragments or a loop, which a single `$"..."` interpolation can't express as-is. Each needs the loop/concatenation rewritten to build a `Raw` first (via `Raw.Join` or `Raw.Compose`), then that `Raw` becomes one hole in the final `.For($"...")` call.

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/UrlSource.cs:117-126`
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesWarnings.cs:346-354` (`RevertReason`)
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs` (**four** "unknown property" throws left over from Task 4, not one — re-grep for `unknown property`; the file currently has duplicate top-level/nested pairs for both repository and service properties)
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfiguration.cs` (`AmbiguousCatalogSpellingError`, method currently starting around line 361 — re-grep for the method name, not a line range; **and** `NothingConfiguredError`, currently around line 537, which the plan's earlier drafts wrongly classified as a mechanical Task 10 site)
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigValidator.cs:680-712` (`Failure` and `CombinedFailure`)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`, relevant `DeveloperConfigValidator`/`DeveloperConfiguration` tests (grep the test tree for `AmbiguousCatalogSpelling`, `NothingConfigured` and `CombinedFailure`/`ServiceSourcesConfigurationException` assertions).

**Interfaces:**
- Consumes: `Raw.Join(string separator, IEnumerable<Raw> parts)`, `Raw.Compose(ServiceTextHandler)`, `new Name(string)`.
- Produces: nothing new — same exception type, escaped message text.

- [ ] **Step 1: `UrlSource.cs:117-126` — re-grep to confirm current lines, then rewrite**

Before:
```csharp
throw new ServiceSourcesConfigurationException(
    // The service name is a catalog key, so it is escaped and capped; consumer.Name is
    // an Aspire resource name, which Aspire's own validator already bounds.
    $"Container '{consumer.Name}' references service " +
    $"'{ServiceSourcesWarnings.Label(urlService.Name)}', whose source is 'url'. " +
    "A 'url'-sourced service has no resource for Aspire to run, so DCP has no Service object to " +
    "plumb container-to-host networking through, and the container would fail to start. " +
    "Reference it from a project or executable instead, or " +
    OutOfBandSourceAdvice.SwitchSource + ". " +
    "Tracked as issue #72.");
```
After:
```csharp
throw ServiceSourcesConfigurationException.For(
    // consumer.Name is an Aspire resource name, which Aspire's own validator already bounds,
    // so it is safe as Raw; urlService.Name is a catalog key and goes through Name.
    $"Container '{Raw.Escaped(consumer.Name)}' references service " +
    $"'{new Name(urlService.Name)}', whose source is 'url'. " +
    $"A 'url'-sourced service has no resource for Aspire to run, so DCP has no Service object to " +
    $"plumb container-to-host networking through, and the container would fail to start. " +
    $"Reference it from a project or executable instead, or {OutOfBandSourceAdvice.SwitchSource}. " +
    $"Tracked as issue #72.");
```
Note every segment becomes a `$"..."` (a `ServiceTextHandler` is built by `+`-concatenating multiple handlers of the same type, which C# supports for `InterpolatedStringHandler`s the same way it does for `string`). Also delete the now-unused `ServiceSourcesWarnings.Label(urlService.Name)` call in favor of `new Name(urlService.Name)` directly — `Label` is being removed in Task 13, don't reintroduce a call to it here.

- [ ] **Step 2: `ServiceSourcesWarnings.cs:350-355` — `RevertReason`, same `+`-chain-to-`$"..."` chain conversion**

Before:
```csharp
private static string RevertReason(
    string serviceName,
    string source,
    IReadOnlyList<string> reverts,
    bool everyRevertWasAnAddition,
    IReadOnlyList<string> registeredEndpoints) =>
    $"Service '{Label(serviceName)}': {string.Join("; ", reverts)}. Its source is '{source}' — " +
    $"{OutOfBandSourceAdvice.SourceDetail(source)}. An out-of-band service's endpoints are fixed by its source, so " +
    $"configure the service where it actually runs. {WhereToGoInstead(source, everyRevertWasAnAddition, registeredEndpoints)}" +
    $"{SwitchSourceRemedy}";
```
After (this returns `string`, used to build a larger message elsewhere in the file — leave its signature as `Raw` rather than `string` so callers can't accidentally drop it into a raw-string hole; check its one caller and update the call site to consume `Raw`):
```csharp
private static Raw RevertReason(
    string serviceName,
    string source,
    IReadOnlyList<string> reverts,
    bool everyRevertWasAnAddition,
    IReadOnlyList<string> registeredEndpoints) =>
    Raw.Compose(
        $"Service '{new Name(serviceName)}': {Raw.Join("; ", reverts.Select(Raw.Escaped))}. Its source is '{new Name(source)}' — " +
        $"{Raw.Escaped(OutOfBandSourceAdvice.SourceDetail(source))}. An out-of-band service's endpoints are fixed by its source, so " +
        $"configure the service where it actually runs. {Raw.Escaped(WhereToGoInstead(source, everyRevertWasAnAddition, registeredEndpoints))}" +
        $"{Raw.Escaped(SwitchSourceRemedy)}");
```
Find `RevertReason`'s one caller in this file (`grep -n "RevertReason(" src/Aspire.Hosting.ServiceSources/ServiceSourcesWarnings.cs`) and update the hole it's placed in from a `string` interpolation to a `Raw` interpolation (drop the surrounding quotes it may currently add, since `Raw` supplies its own).

- [ ] **Step 3: `ServiceCatalogLoader.cs` — all four "unknown property" throws**

There are two pairs, not one site: a top-level-property throw and a nested-property throw, each duplicated once under the repository parser and once under the service parser (currently near lines 132-133/145-146 for repositories, 230-231/253-254 for services — re-grep for `unknown property` first). Apply the same rewrite to all four.

Before (repository, top-level — the other three follow the identical shape with `Repository`→`Service` and `KnownRepositoryProperties`→`KnownServiceProperties`/the matching nested-property set):
```csharp
throw new ServiceSourcesConfigurationException(
    $"Repository '{name}': unknown property '{key}'. Expected one of: " +
    string.Join(", ", KnownRepositoryProperties) + ".");
```
After:
```csharp
throw ServiceSourcesConfigurationException.For(
    $"Repository '{new Name(name)}': unknown property '{new Name(key)}'. Expected one of: " +
    $"{Raw.Join(", ", KnownRepositoryProperties.Select(Raw.Literal))}.");
```
Each known-properties collection (`KnownRepositoryProperties`, `KnownServiceProperties`, and whatever the two nested-property sites reference) is a fixed, compile-time-known set of property names (not caller-controlled), so `Raw.Literal` is correct here — confirm by reading each declaration (`grep -n "KnownRepositoryProperties\|KnownServiceProperties" src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs`) before assuming; if any turns out to include a runtime-sourced entry, use `Raw.Escaped` instead. `Raw.Literal` takes `[ConstantExpected] string`, so it cannot bind through `Select(Raw.Literal)` as a method group if any of these collections isn't itself a `const`/literal array — verify the collection is declared as one (`static readonly string[]` initialized from literals is fine to pass a delegate over only if each element is inlined as a constant at the call; if `dotnet build` reports `CS1503`/`CS0428` on this line, switch to `KnownRepositoryProperties.Select(Raw.Escaped)` instead, which is always safe and does not require the argument to be constant).

- [ ] **Step 4: `DeveloperConfiguration.cs` — `AmbiguousCatalogSpellingError` and `NothingConfiguredError`**

Re-grep for both method names before editing — line numbers drift (`AmbiguousCatalogSpellingError`'s body is currently declared around line 361, not the 388-394 an earlier draft of this plan cited, which actually falls inside `NotConfiguredError`/`NothingConfiguredError` instead).

Before:
```csharp
private static ServiceSourcesConfigurationException AmbiguousCatalogSpellingError(
    string configuredName, IEnumerable<string> catalogNames)
{
    var spellings = catalogNames
        .Where(name => string.Equals(name, configuredName, StringComparison.OrdinalIgnoreCase))
        .Select(name => $"'{name}'");

    return new ServiceSourcesConfigurationException(
        $"Configuration names service '{configuredName}', which 'servicesources.yaml' declares more than "
        + $"once under names differing only by case ({string.Join(", ", spellings)}). Configuration keys are "
        + "case-insensitive, so there is no key that reaches one of them and not the other — rename them in "
        + "'servicesources.yaml' so they differ by more than case.");
}
```
After:
```csharp
private static ServiceSourcesConfigurationException AmbiguousCatalogSpellingError(
    string configuredName, IEnumerable<string> catalogNames)
{
    var spellings = catalogNames
        .Where(name => string.Equals(name, configuredName, StringComparison.OrdinalIgnoreCase))
        .Select(name => Raw.Compose($"'{new Name(name)}'"));

    return ServiceSourcesConfigurationException.For(
        $"Configuration names service '{new Name(configuredName)}', which 'servicesources.yaml' declares more than " +
        $"once under names differing only by case ({Raw.Join(", ", spellings)}). Configuration keys are " +
        $"case-insensitive, so there is no key that reaches one of them and not the other — rename them in " +
        $"'servicesources.yaml' so they differ by more than case.");
}
```

`NothingConfiguredError` (currently around line 537) is a second `+`-chain site in this same file, missed by an earlier draft of this plan's Task 10:

Before:
```csharp
private ServiceSourcesConfigurationException NothingConfiguredError(string serviceName) =>
    new("No service sources are configured: "
        + (NearMissRootKey is not null
            ? $"'{FilePath}' has a top-level key '{NearMissRootKey}'. Did you mean 'services'?"
            : $"'{ServicesKey}' is empty in every configuration source, "
              + $"so no service has a source — including '{serviceName}'. "
              + $"Create '{FilePath}' ({(FileFound ? "found, but it configures no services" : "not found")}) with "
              + $"{{ \"services\": {{ \"{serviceName}\": {{ \"source\": \"...\" }} }} }}, "
              + $"or set the environment variable {EnvironmentVariableFor(serviceName)}. "
              + "Sources consulted: that file, appsettings.json, appsettings.{Environment}.json, user secrets, "
              + "environment variables and command-line arguments."));
```
After:
```csharp
private ServiceSourcesConfigurationException NothingConfiguredError(string serviceName) =>
    ServiceSourcesConfigurationException.For(
        NearMissRootKey is not null
            ? $"No service sources are configured: '{Raw.Escaped(FilePath)}' has a top-level key '{new Name(NearMissRootKey)}'. Did you mean 'services'?"
            : $"No service sources are configured: '{Raw.Literal(ServicesKey)}' is empty in every configuration source, " +
              $"so no service has a source — including '{new Name(serviceName)}'. " +
              $"Create '{Raw.Escaped(FilePath)}' ({Raw.Literal(FileFound ? "found, but it configures no services" : "not found")}) with " +
              $"{{ \"services\": {{ \"{new Name(serviceName)}\": {{ \"source\": \"...\" }} }} }}, " +
              $"or set the environment variable {Raw.Escaped(EnvironmentVariableFor(serviceName))}. " +
              $"Sources consulted: that file, appsettings.json, appsettings.{{Environment}}.json, user secrets, " +
              $"environment variables and command-line arguments.");
```
`FilePath` is a filesystem path this package resolved, not caller-controlled text a developer typed into a config value, so it takes `Raw.Escaped` (never truncated) rather than `Name`. The ternary's two literal-string arms can't bind to `Raw.Literal`'s `[ConstantExpected]` parameter through a runtime ternary expression — if `dotnet build` rejects that call, fall back to `Raw.Escaped(...)` there too, same as the `shape.Kind` case in Step 5 below.

- [ ] **Step 5: `DeveloperConfigValidator.cs:680-712` — `Failure` and `CombinedFailure`**

Before:
```csharp
private static ServiceSourcesConfigurationException Failure(
    string serviceName, IReadOnlyList<string> problems, DeveloperConfigShape shape) =>
    new(problems.Count == 1
        ? $"{shape.Kind} '{ConfiguredValue.Bare(serviceName)}': {problems[0]}"
        : $"{shape.Kind} '{ConfiguredValue.Bare(serviceName)}': {problems.Count} problems with the entry:"
          + string.Concat(problems.Select(p => $"{Environment.NewLine}  - {p}")));

private static ServiceSourcesConfigurationException CombinedFailure(
    IReadOnlyList<(string Service, IReadOnlyList<string> Problems)> faulted, DeveloperConfigShape shape)
{
    var total = faulted.Sum(entry => entry.Problems.Count);

    var message = new StringBuilder()
        .Append($"{total} problems across {faulted.Count} {shape.Noun} entries:");

    foreach (var (service, problems) in faulted)
    {
        message.Append(Environment.NewLine)
            .Append($"  {shape.Kind} '{ConfiguredValue.Bare(service)}':");

        foreach (var problem in problems)
        {
            message.Append(Environment.NewLine).Append($"    - {problem}");
        }
    }

    return new ServiceSourcesConfigurationException(message.ToString());
}
```
`shape.Kind`/`shape.Noun` are `DeveloperConfigShape` instance properties (`get`-only, assigned in its constructor — `Config/DeveloperConfigShape.cs:81,84`), not `const` fields, so `Raw.Literal` — whose parameter is `[ConstantExpected]` (`Messages/Raw.cs`) — **cannot** take `shape.Kind`/`shape.Noun` directly; that call fails to compile, and `CA1857` is a build error unconditionally in this project (`<WarningsAsErrors>...CA1857</WarningsAsErrors>` in the csproj), not only under `-warnaserror`. Use `Raw.Escaped(shape.Kind)` / `Raw.Escaped(shape.Noun)` everywhere below instead.

`problems` is `IReadOnlyList<string>`, and each element is the return value of one of ~28 private composer methods in this file (`NotValidHere`, `EntryExpected`, `ValueExpected`, `Blank`, `BlockExpected`, etc. — `grep -n "problems.Add(" src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigValidator.cs` lists every call). These methods already build a full, developer-facing sentence themselves, escaping the caller-controlled fragments they embed (via `ConfiguredValue.Bare`/`Escaped`) while also writing plenty of their own literal punctuation, including illustrative JSON snippets with real quote characters (e.g. `NotValidHere`: `"'{home}' block: \"{serviceName}\": {{ ..., \"{home}\": ... }}."`). Wrapping the *finished* sentence in `Raw.Escaped(problems[0])` would run `Name.Escape` over that whole sentence, backslash-escaping every quote in the illustrative JSON along with the actual caller value — mangling text that was never unsafe. The fix is the same shape Task 14 uses for the logging composers: change each of these ~28 methods to return `Raw` (wrap their existing body in `Raw.Compose($"...")`, using the `Name`/`Raw.Escaped` wrapping their own interpolations already need) instead of `string`, and change `problems`'s type from `IReadOnlyList<string>` to `IReadOnlyList<Raw>` everywhere it's threaded through (`problems.Add(...)`, the `Failure`/`CombinedFailure` parameters, and the `(string Service, IReadOnlyList<string> Problems)` tuple used by `CombinedFailure`'s caller). Do this as its own compiler-fix loop before touching `Failure`/`CombinedFailure` — rebuild after retyping `problems`, and the `CS1503`s point at exactly the composer methods still returning `string`.

After (using `Raw.Join` to replace the `StringBuilder`/loop, and taking `problems`/`Problems` as `Raw` now that the composers return it):
```csharp
private static ServiceSourcesConfigurationException Failure(
    string serviceName, IReadOnlyList<Raw> problems, DeveloperConfigShape shape) =>
    ServiceSourcesConfigurationException.For(problems.Count == 1
        ? $"{Raw.Escaped(shape.Kind)} '{Raw.Escaped(ConfiguredValue.Bare(serviceName))}': {problems[0]}"
        : $"{Raw.Escaped(shape.Kind)} '{Raw.Escaped(ConfiguredValue.Bare(serviceName))}': {problems.Count} problems with the entry:" +
          $"{Raw.Join(string.Empty, problems.Select(p => Raw.Compose($"{Environment.NewLine}  - {p}")))}");

private static ServiceSourcesConfigurationException CombinedFailure(
    IReadOnlyList<(string Service, IReadOnlyList<Raw> Problems)> faulted, DeveloperConfigShape shape)
{
    var total = faulted.Sum(entry => entry.Problems.Count);

    var entries = faulted.Select(entry => Raw.Compose(
        $"{Environment.NewLine}  {Raw.Escaped(shape.Kind)} '{Raw.Escaped(ConfiguredValue.Bare(entry.Service))}':" +
        $"{Raw.Join(string.Empty, entry.Problems.Select(p => Raw.Compose($"{Environment.NewLine}    - {p}")))}"));

    return ServiceSourcesConfigurationException.For(
        $"{total} problems across {faulted.Count} {Raw.Escaped(shape.Noun)} entries:{Raw.Join(string.Empty, entries)}");
}
```
`ConfiguredValue.Bare(serviceName)` returns a plain `string` per its existing use elsewhere in this file — confirm the convention neighboring call sites already use to fit it into a `ServiceTextHandler` hole (`grep -n "ConfiguredValue.Bare" src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigValidator.cs`); wrapping it in `Raw.Escaped` above is safe regardless, since `Bare` only strips invisible characters and does not itself neutralize quotes.

- [ ] **Step 6: Rebuild everything, confirm RS0030 dropped to exactly 0 project-wide**

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 \
  | grep -oE "[^ ]+\.cs\([0-9]+,[0-9]+\): warning RS0030" | sort -u | wc -l
```
Expected: `0`. If not, re-run the full grep (no file filter) to find what Tasks 1–11 missed and fix it before continuing — the number is the completeness gate for item 3 below.

- [ ] **Step 7: Run the full test suite (cheap leg — net10.0 only)**
  ```bash
  dotnet test --no-restore -c Release -f net10.0
  ```
  Expected: same pass count as `main` before this plan started (record the baseline count before Task 1 if you haven't already, via `dotnet test --no-restore -c Release -f net10.0` on a clean checkout). The full three-TFM matrix runs once, in Task 16 Step 7, right before landing.

- [ ] **Step 8: Commit**
  ```bash
  git add src/Aspire.Hosting.ServiceSources/Sources/UrlSource.cs src/Aspire.Hosting.ServiceSources/ServiceSourcesWarnings.cs src/Aspire.Hosting.ServiceSources/Config/ServiceCatalogLoader.cs src/Aspire.Hosting.ServiceSources/Config/DeveloperConfiguration.cs src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigValidator.cs
  git commit -m "$(cat <<'EOF'
Restructure the four concatenation-composed exception messages onto Raw.Join

The StringBuilder/`+`-chain composers in ServiceCatalogLoader, ServiceSourcesWarnings,
DeveloperConfiguration and DeveloperConfigValidator don't fit a plain interpolated-string
rename, since each assembles its message from a runtime loop or multi-fragment chain.
Raw.Join gives them the same seam: build the repeated/loop body as Raw first, then splice
it into the final ServiceTextHandler as one hole. RS0030 is now at 0 project-wide.

Part of #385.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
  ```

---

### Task 12: Flip RS0030 to an error

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj:17`

**Interfaces:**
- Consumes: Task 11's confirmed zero-warning state.
- Produces: a project that no longer builds if a future PR adds a raw `ServiceSourcesConfigurationException(string)` call outside `.For`.

- [ ] **Step 1: Confirm RS0030 is still 0** (repeat Task 11 Step 6's command — do this immediately before editing, in case anything landed on `main` since).

- [ ] **Step 2: Delete the line**

```xml
<!-- before -->
<WarningsNotAsErrors>$(WarningsNotAsErrors);RS0030</WarningsNotAsErrors>
<!-- after: line removed entirely -->
```
Read the surrounding 5 lines first (`sed -n '10,22p' src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj`) — if the `<WarningsNotAsErrors>` element carries other symbols besides `RS0030` (unlikely per the issue's own description of a "one-line deletion", but verify), remove only the `;RS0030` token rather than the whole element.

- [ ] **Step 3: Rebuild with `-warnaserror` and confirm the build still succeeds**

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 | tail -20
```
Expected: `Build succeeded.` / `0 Error(s)`. If any RS0030 now shows as `error`, Task 11's zero-count check was stale or missed a file — go find it before proceeding (do not re-add the exclusion line as a workaround).

- [ ] **Step 4: Confirm the non-`-warnaserror` CI workflows still build** (per the issue's "Not in scope" note, `aspire-matrix.yml` and `net11-preview.yml` don't pass `-warnaserror` — this task doesn't touch them, just confirm a plain build is still clean too)

```bash
dotnet build -c Release --no-restore --no-incremental 2>&1 | tail -5
```

- [ ] **Step 5: Run the full test suite (cheap leg — net10.0 only)**
  ```bash
  dotnet test --no-restore -c Release -f net10.0
  ```

- [ ] **Step 6: Commit**
  ```bash
  git add src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj
  git commit -m "$(cat <<'EOF'
Let RS0030 become a build error now that every call site is migrated

Every ServiceSourcesConfigurationException constructor call in this package now goes
through .For, so the banned-API warning has nothing left to tolerate. Removing the
exclusion means a future raw call fails the build instead of silently reintroducing an
unescaped name into a reader-facing message.

Part of #385.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
  ```

---

### Task 13: Unwrap `ServiceSourcesWarnings.Label` and delete it

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceConfigurationExtensions.cs:127`
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs` (its local `Label` alias and its 7 call sites)
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesWarnings.cs` (delete the public `Label` method once nothing calls it; keep its internal uses at lines ~303/378 as direct `new Name(...)` calls, or leave them calling a private helper if the file still needs one internally — check what remains after the public removal)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/EndpointMutationDetectorTests.cs`, `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesBuilderExtensionsTests.cs`

**Interfaces:**
- Consumes: `new Aspire.Hosting.ServiceSources.Messages.Name(string)`.
- Produces: `ServiceSourcesWarnings.Label` no longer exists as a public/internal entry point — grep the whole `src/` tree for `\.Label(` after this task to confirm nothing outside this package's own message-composition helpers still calls it.

- [ ] **Step 1: Find every caller of `ServiceSourcesWarnings.Label`**

```bash
grep -rn "ServiceSourcesWarnings\.Label(\|(?<!Capability)Label(" src/Aspire.Hosting.ServiceSources --include="*.cs" -P
```
Confirm the full set matches (or supersedes) what Task 385's issue text names: `ServiceConfigurationExtensions.cs:127`, the local alias in `EndpointMutationDetector.cs` and its ~6–7 call sites, plus `ServiceSourcesWarnings.cs`'s own internal uses.

- [ ] **Step 2: `ServiceConfigurationExtensions.cs:127` — replace with `new Name(...)`**

```csharp
// before
var name = ServiceSourcesWarnings.Label(annotation?.ServiceName ?? resource.Name);
// after
var name = new Name(annotation?.ServiceName ?? resource.Name).ToString();
```
(Keep `.ToString()` only if `name`'s declared type / downstream use is `string` — check the surrounding lines; if `name` only ever gets interpolated into a `ServiceTextHandler` hole afterward, drop `.ToString()` and change `name`'s type to `Name`, passing it directly into the hole without re-wrapping.)

- [ ] **Step 3: `EndpointMutationDetector.cs` — delete the local alias, update its 7 call sites**

```csharp
// before
private static string Label(string name) => ServiceSourcesWarnings.Label(name);
// after: method deleted
```
Every `Label(recorded.Name)` / `Label(endpoint.Name)` call site in this file becomes `new Name(recorded.Name)` / `new Name(endpoint.Name)` directly, since these are already inside `$"..."` interpolations feeding `ServiceTextHandler` holes (confirm each of the 7 by re-reading `EndpointMutationDetector.cs` around lines 88, 102, 143, 147, 150, 154, 156 — re-grep first, these shifted from the issue's cited `:303` since main advanced).

- [ ] **Step 4: `ServiceSourcesWarnings.cs` — delete the `Label` method, fix its two remaining internal callers**

```bash
grep -n "Label(" src/Aspire.Hosting.ServiceSources/ServiceSourcesWarnings.cs
```
For each remaining internal use (around the current `:303` skip-message and `:378` endpoint-list composer, both already inside `$"..."` interpolations per the file read earlier in this plan), replace `Label(x)` with `new Name(x)` directly, then delete the `internal static string Label(string? name) => new Name(name).ToString();` method itself.

- [ ] **Step 5: Rebuild and confirm no remaining references**

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 | tail -10
grep -rn "\.Label(" src/Aspire.Hosting.ServiceSources --include="*.cs"
```
Expected: build succeeds; the grep only matches unrelated `*Label` identifiers already confirmed distinct in the earlier investigation (`ServiceLabel`, `RepositoryLabel`, `CapabilityLabel`, `IsSecretNameLabel` — none of these are `ServiceSourcesWarnings.Label` and are out of scope here).

- [ ] **Step 6: Run the affected tests**
  ```bash
  dotnet test --no-restore -c Release -f net10.0 --filter "FullyQualifiedName~EndpointMutationDetectorTests|FullyQualifiedName~ServiceSourcesBuilderExtensionsTests"
  ```

- [ ] **Step 7: Commit**
  ```bash
  git add src/Aspire.Hosting.ServiceSources/ServiceConfigurationExtensions.cs src/Aspire.Hosting.ServiceSources/Sources/EndpointMutationDetector.cs src/Aspire.Hosting.ServiceSources/ServiceSourcesWarnings.cs
  git commit -m "$(cat <<'EOF'
Delete ServiceSourcesWarnings.Label now that every caller composes through the seam

Label existed as a remembered-convention shortcut before the structural seam landed.
Every call site now wraps its name directly in Name, so the indirection has no callers
left and is removed rather than kept as an unused alternate spelling.

Part of #385.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
  ```

---

### Task 14: Close sink B — `ServiceSourcesLog`

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/ServiceSourcesLog.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/BannedSymbols.txt` (add `Microsoft.Extensions.Logging.LoggerExtensions.Log*` entries)
- Modify: `src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs`, `Sources/DeferredCheckout.cs`, `ServiceStartupFailureNotices.cs` — replace `logger.LogInformation/LogWarning/LogError/LogDebug` calls with `ServiceSourcesLog` calls; convert the composer methods that feed these calls (grep for methods returning `string`) to return `Raw`.
- Modify: `src/Aspire.Hosting.ServiceSources/ServiceSourcesWarnings.cs:294` — its `logger?.LogWarning("{ServiceSourcesWarning}", message)` call is a second sink-B site this task must close too. It's easy to miss: the inventory grep below (`logger\.Log(...)`) only matches a literal `logger.` immediately before `Log`, and this call site is `logger?.LogWarning` — the `?.` breaks that match, so re-grep with `logger\??\.Log` before trusting the count is complete.
- Modify: `src/Aspire.Hosting.ServiceSources/Prepare/IPrepareOutputSink.cs` (`ConsolePrepareOutputSink.Report`, currently `Console.Out.WriteLine(line)`) and `src/Aspire.Hosting.ServiceSources/Prepare/CheckoutPreparation.cs` (its `sink.Report($"{Tag(checkoutName)} ...")` call around line 129, and `Tag`/`LaunchFailedMessage` which build the lines `Report` prints) — this is a *third* sink this package writes caller-controlled names through, entirely separate from `ILogger` and from `ServiceSourcesConfigurationException`; it reaches the terminal directly via `Console.Out.WriteLine` and was not covered by the issue's original two-sink framing. Wrap the interpolated names going into `sink.Report(...)` the same way `ServiceSourcesLog` wraps a log message (`new Name(checkoutName)` in place of the raw `Tag(checkoutName)` composition, or make `Tag`/the report-building call sites in this file return/consume `Raw` consistently with the rest of this task).
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalCheckoutPrefetchTests.cs`, `test/Aspire.Hosting.ServiceSources.Tests/ServiceStartupFailureNoticeTests.cs`, `test/Aspire.Hosting.ServiceSources.Tests/Prepare/CheckoutPreparationTests.cs`, relevant `ServiceSourcesWarnings` tests, plus a new `test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesLogTests.cs`

**Interfaces:**
- Consumes: `Messages.Raw`, `Messages.ServiceTextHandler`, `Microsoft.Extensions.Logging.ILogger` (the underlying sink `ServiceSourcesLog` wraps).
- Produces: `internal static class ServiceSourcesLog` with methods matching this package's actual call shapes — inspect the 14 existing `logger.Log*` calls first (already listed in the investigation: `BufferingPrepareOutputSink.cs:145`, `LocalCheckoutPrefetch.cs:313,342`, `DeferredCheckout.cs:434,467,660,759,868,957,1008,1034,1049`, `ServiceStartupFailureNotices.cs:252,288`) to derive the exact method signatures needed — e.g. `internal static void Information(ILogger logger, ServiceTextHandler message)`, `internal static void Warning(...)`, `internal static void Error(...)`, `internal static void Debug(ILogger logger, Exception exception, ServiceTextHandler message)` for the `logger.LogDebug(ex, "{What}: {Message}", what, ex.Message)` shape.

- [ ] **Step 1: Inventory every `logger.Log*` call and its message shape**

```bash
grep -rn "logger\??\.Log\(Information\|Warning\|Error\|Debug\|Trace\|Critical\)" src/Aspire.Hosting.ServiceSources --include="*.cs"
```
Note the `\??` before the `.` — a plain `logger\.Log(...)` pattern misses any call written as `logger?.LogWarning(...)` (the nullable-conditional operator breaks the literal match), which is exactly the shape `ServiceSourcesWarnings.cs:294` uses. Don't trust a lower count than expected without checking for this.

For each hit, note: is the message already a `{StructuredField}, value` pair (safe today, per the issue), a literal, or a composed `string` from a local composer method? The issue names five composer methods with "safe bodies but still return `string`" — find them by tracing each `logger.LogInformation(...)`/`LogWarning(...)`/`logger?.LogWarning(...)` call's argument back to its source (a local variable or method call — most sites are in `DeferredCheckout.cs` given it has 8 of the 14 `ILogger` sites; `ServiceSourcesWarnings.cs:294` traces back to `RevertReason`, already converted to return `Raw` in Task 11 Step 2 — that call's hole needs updating from a `string` interpolation to a `Raw` one here, not re-escaped).

- [ ] **Step 2: Write the failing test for `ServiceSourcesLog`**

```csharp
// test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesLogTests.cs
using Aspire.Hosting.ServiceSources;
using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.ServiceSources.Tests;

public class ServiceSourcesLogTests
{
    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public void Information_carries_the_escaped_message_text()
    {
        var logger = new RecordingLogger();
        var name = "bill'ing";

        ServiceSourcesLog.Information(logger, $"Service '{new Name(name)}' started.");

        Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, logger.Entries[0].Level);
        Assert.Contains("bill\\u0027ing", logger.Entries[0].Message);
    }
}
```

- [ ] **Step 3: Run it to see it fail**

```bash
dotnet test --no-restore -c Release -f net10.0 --filter "FullyQualifiedName~ServiceSourcesLogTests"
```
Expected: `CS0246: The type or namespace name 'ServiceSourcesLog' could not be found` (test project doesn't compile yet).

- [ ] **Step 4: Write `ServiceSourcesLog`**

```csharp
using Aspire.Hosting.ServiceSources.Messages;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ServiceSources;

/// <summary>
/// The one way this package writes to an <see cref="ILogger"/> with caller-controlled text —
/// mirrors ServiceSourcesConfigurationException.For's seam for the logging sink.
/// </summary>
internal static class ServiceSourcesLog
{
    internal static void Information(ILogger logger, ServiceTextHandler message) =>
        logger.LogInformation("{ServiceSourcesMessage}", message.Text);

    internal static void Warning(ILogger logger, ServiceTextHandler message) =>
        logger.LogWarning("{ServiceSourcesMessage}", message.Text);

    internal static void Error(ILogger logger, ServiceTextHandler message) =>
        logger.LogError("{ServiceSourcesMessage}", message.Text);

    internal static void Debug(ILogger logger, ServiceTextHandler message) =>
        logger.LogDebug("{ServiceSourcesMessage}", message.Text);

    internal static void Debug(ILogger logger, Exception exception, ServiceTextHandler message) =>
        logger.LogDebug(exception, "{ServiceSourcesMessage}", message.Text);
}
```
Adjust the exact method set to match Step 1's inventory — if a call site needs a second structured field alongside the message (rare in the 14 found, but check `DeferredCheckout.cs:868`'s `{RepoRoot}` placeholder), add an overload rather than forcing every call through one shape.

- [ ] **Step 5: Ban `LoggerExtensions.Log*` in `BannedSymbols.txt`**

Read the existing file first (`cat src/Aspire.Hosting.ServiceSources/BannedSymbols.txt`) to match its format (RS0030 config format — symbol-documentation-ID per line plus a `;`-separated message), then add entries for `M:Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(...)` etc. for each overload actually used in this package, each with a message directing to `ServiceSourcesLog`. `ServiceSourcesLog.cs` itself will need the same `#pragma warning disable RS0030` / `restore` wrapping around its own calls that `ServiceSourcesConfigurationException.For` uses, since it's the one legitimate caller.

- [ ] **Step 6: Migrate each `ILogger` call site (14 plus `ServiceSourcesWarnings.cs:294`) and every composer method that feeds one**

For each site from Step 1's inventory: change `logger.LogInformation(...)`/`logger?.LogWarning(...)` to `ServiceSourcesLog.Information(logger, $"...")`/`ServiceSourcesLog.Warning(...)`, converting any composer method that built the message string into one returning `Raw` (via `Raw.Compose`) so it plugs into the new call's interpolation as a hole, same pattern as prior tasks.

- [ ] **Step 6b: Close the console sink**

Change `ConsolePrepareOutputSink.Report`'s signature (or the call sites feeding it in `CheckoutPreparation.cs`) so a caller-controlled name reaches `Console.Out.WriteLine` only after going through `Name`/`Raw`, the same discipline as the `ILogger` and exception sinks above — `Tag(checkoutName)` and `LaunchFailedMessage(...)` are the two composers to convert. Re-grep `Tag(\|LaunchFailedMessage(\|sink.Report(` in `Prepare/CheckoutPreparation.cs` first, since lines shift.

- [ ] **Step 7: Rebuild and confirm no `LoggerExtensions.Log*` warnings remain**

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 | grep -i "logextensions\|RS0030" | sort -u
```
Expected: only the intentional `ServiceSourcesLog.cs` suppression shows up (if it shows at all — a scoped `#pragma` shouldn't emit anything).

- [ ] **Step 8: Run the new and existing tests**
  ```bash
  dotnet test --no-restore -c Release -f net10.0 --filter "FullyQualifiedName~ServiceSourcesLogTests|FullyQualifiedName~LocalCheckoutPrefetchTests|FullyQualifiedName~ServiceStartupFailureNoticeTests"
  ```
  Expected: all pass, including the new test from Step 2.

- [ ] **Step 9: Commit**
  ```bash
  git add src/Aspire.Hosting.ServiceSources/ServiceSourcesLog.cs src/Aspire.Hosting.ServiceSources/BannedSymbols.txt src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs src/Aspire.Hosting.ServiceSources/Sources/DeferredCheckout.cs src/Aspire.Hosting.ServiceSources/ServiceStartupFailureNotices.cs src/Aspire.Hosting.ServiceSources/ServiceSourcesWarnings.cs src/Aspire.Hosting.ServiceSources/Prepare/IPrepareOutputSink.cs src/Aspire.Hosting.ServiceSources/Prepare/CheckoutPreparation.cs test/Aspire.Hosting.ServiceSources.Tests/ServiceSourcesLogTests.cs
  git commit -m "$(cat <<'EOF'
Close sink B: introduce ServiceSourcesLog and ban raw LoggerExtensions.Log* calls

PR #381 migrated the exception seam but not the structured-argument logging sink, nor the
Console.Out sink CheckoutPreparation writes progress through — all were exempted on the
premise that structured arguments don't need escaping, which held for the call sites with
a literal message but not for the composer methods that built the {ServiceSourcesMessage}
string themselves. Those composers now return Raw, and every call site goes through
ServiceSourcesLog or a Name/Raw-wrapped console line the same way exception messages go
through ServiceSourcesConfigurationException.For.

Part of #385.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
  ```

---

### Task 15: Migrate `KubernetesSecretException` throws

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/BackingServices/KubernetesBackingServiceSource.cs` (12 `throw new KubernetesSecretException(...)` sites)
- Modify: wherever `KubernetesSecretException` is declared (`grep -rn "class KubernetesSecretException" src/Aspire.Hosting.ServiceSources`) — add a `.For` factory mirroring `ServiceSourcesConfigurationException.For`, keyed to the same `ServiceTextHandler`.
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Kubernetes/` (grep for the exercising test file — likely under `Kubernetes/` or `BackingServices/`)

**Interfaces:**
- Consumes: `Messages.ServiceTextHandler`, `Messages.Name`, `Messages.Raw`.
- Produces: `KubernetesSecretException.For(ServiceTextHandler)` / `.For(ServiceTextHandler, Exception)`, same shape as `ServiceSourcesConfigurationException`'s.

- [ ] **Step 1: Read the exception's current definition** (`Read` the file `grep` finds) to see its existing constructors, then add `.For` factories mirroring the *shape* of `ServiceSourcesConfigurationException.cs:22-32` (same `#pragma warning disable RS0030` / `restore` scoping around the `new(...)` calls inside the factories) — not a literal copy-paste, since `KubernetesSecretException`'s own constructor parameter list may not match `ServiceSourcesConfigurationException`'s `(string)`/`(string, Exception)` pair exactly. Add one `.For` overload per existing constructor, keeping any extra parameters (e.g. a flag) in the same position.

- [ ] **Step 2: Add `KubernetesSecretException` to the RS0030 ban list** (or confirm it's already covered if `BannedSymbols.txt` bans by a broader pattern — read the file).

- [ ] **Step 3: Rename all 12 `throw new KubernetesSecretException(` to `throw KubernetesSecretException.For(`** in `KubernetesBackingServiceSource.cs`, using the same procedure as Task 1's Step 2.

- [ ] **Step 4: Compiler-fix loop** for the resulting `CS1503`s. Pay particular attention to the two sites the issue calls out: `KubernetesBackingServiceSource.cs:1051` currently uses `ConfiguredValue.Bare` alone with no cap/quote-neutralization — replace with `new Name(...)` so it gets both; `:717` currently embeds `{ex.Message}` raw — replace with `Raw.Cause(ex)`. Re-grep for the current line numbers first (`grep -n "ConfiguredValue.Bare\|ex.Message" src/Aspire.Hosting.ServiceSources/BackingServices/KubernetesBackingServiceSource.cs`), since these were cited against an earlier commit.

- [ ] **Step 5: Confirm zero remaining raw `KubernetesSecretException` constructor calls**

```bash
grep -n "new KubernetesSecretException(" src/Aspire.Hosting.ServiceSources/BackingServices/KubernetesBackingServiceSource.cs
```
Expected: no output (all now go through `.For`, except inside the `.For` factory itself if it's defined in the same file — check).

- [ ] **Step 6: Run the Kubernetes backing-service tests.**

- [ ] **Step 7: Commit** ("Migrate KubernetesSecretException messages onto the structural escaping seam").

---

### Task 16: Migrate `Git*Exception`/`PrepareLaunchException` throws and close the remaining escaping gaps

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Git/GitCliClient.cs` (3 sites: `GitCommandFailedException` ×2, `GitAuthenticationFailedException` ×1)
- Modify: `src/Aspire.Hosting.ServiceSources/Git/GitCommand.cs` (2 sites: `GitUnavailableException` ×2)
- Modify: `src/Aspire.Hosting.ServiceSources/Prepare/IPrepareCommandRunner.cs:47` (`PrepareLaunchException`) and every `throw new PrepareLaunchException(...)` site (`grep -rn "throw new PrepareLaunchException(" src/Aspire.Hosting.ServiceSources`) — it has the same `(string message, Exception? innerException = null)` shape as the migrated types and was missing from every earlier draft of this plan's File Map, Task 15 and Task 16, despite being a fourth caller-controlled-name-bearing exception type this package throws.
- Modify: wherever `GitCommandFailedException`, `GitAuthenticationFailedException`, `GitUnavailableException`, `PrepareLaunchException`, and any further `Git*`/other exception type are declared — add `.For` factories per the Task 15 pattern to each, matching each type's *actual* constructor shape (see Step 2 below — do not assume every type matches `ServiceSourcesConfigurationException`'s two-constructor pattern).
- Modify: `src/Aspire.Hosting.ServiceSources/BannedSymbols.txt` — add ban entries for the raw constructors of `GitCommandFailedException`, `GitAuthenticationFailedException`, `GitUnavailableException` and `PrepareLaunchException`, the same way Task 15 Step 2 banned `KubernetesSecretException`'s. This step is easy to skip since Tasks 1-15 never needed it for these types — without it, RS0030 stays silent forever on a future raw `throw new GitCommandFailedException(...)`.
- Modify: `src/Aspire.Hosting.ServiceSources/BackingServices/KubernetesBackingServiceSource.cs:473` — leave the bare `.Replace("'", "''")` in place (it's the connection-string escaping convention for a connection-string *value*, explicitly not a name per the issue — do not touch it in this task).
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/`, `test/Aspire.Hosting.ServiceSources.Tests/Prepare/CheckoutPreparationTests.cs` (or wherever `PrepareLaunchException` is exercised)

**Interfaces:**
- Consumes: same as Task 15.
- Produces: `.For` factories on each of the `Git*Exception` types and on `PrepareLaunchException`.

- [ ] **Step 1: Find the fourth `Git*` exception type** the issue mentions (it counts "the four `Git*` types (6)" — Task 1/16 combined found only 3 distinct types across 5 call sites so far; grep to find what's missing):

```bash
grep -rln "class Git.*Exception" src/Aspire.Hosting.ServiceSources
grep -rn "throw new Git" src/Aspire.Hosting.ServiceSources --include="*.cs"
```
Reconcile against the issue's count before proceeding — if a fourth type/site exists elsewhere (e.g. thrown from a different file than `GitCliClient.cs`/`GitCommand.cs`), add it to this task's file list.

- [ ] **Step 2: Add `.For` factories to each `Git*Exception` type and to `PrepareLaunchException`**, same pattern as Task 15 Step 1 — but read each type's real constructor list first, since they don't all match `ServiceSourcesConfigurationException`'s `(string)`/`(string, Exception)` pair:
  - `GitCommandFailedException(string message)` — one constructor only, no `Exception` overload. Add just `.For(ServiceTextHandler message)`; do not add a two-argument overload that has nothing to wrap.
  - `GitAuthenticationFailedException(string message, Exception innerException, bool noCredentialsResolved = false)` — three parameters, and `NoCredentialsResolved` is behavior existing callers rely on to distinguish remediation advice. Add `.For(ServiceTextHandler message, Exception innerException, bool noCredentialsResolved = false)` and thread the flag through to the underlying `new(...)`; do not drop it in favor of copying `ServiceSourcesConfigurationException`'s two-argument shape.
  - `GitUnavailableException(string message, Exception? innerException)` — two required parameters, no default; add `.For(ServiceTextHandler message, Exception? innerException)` matching that.
  - `PrepareLaunchException(string message, Exception? innerException = null)` — add `.For(ServiceTextHandler message, Exception? innerException = null)`.
  - Add each `.For` factory to its own type's file, same `#pragma warning disable RS0030` / `restore` scoping as Task 15 Step 1.

- [ ] **Step 3: Rename each `throw new Git*Exception(`/`throw new PrepareLaunchException(` to `.For(`.**

- [ ] **Step 4: Compiler-fix loop** for the resulting `CS1503`s — `Describe(result)` (used by `GitCommandFailedException`'s two sites) is a helper already in `GitCliClient.cs`; check its return type and whether it needs to change to return `Raw` rather than `string` (same treatment as the composer methods in Task 14). Do the same for `LaunchFailedMessage` in `Prepare/CheckoutPreparation.cs`, which feeds `PrepareLaunchException`.

- [ ] **Step 5: Confirm zero remaining raw constructor calls for all `Git*Exception` types and `PrepareLaunchException`.**

```bash
grep -rn "new Git.*Exception(\|new PrepareLaunchException(" src/Aspire.Hosting.ServiceSources --include="*.cs" | grep -v "\.For("
```
Expected: only the internal `new(...)` calls inside each type's own `.For` factory.

- [ ] **Step 5b: Confirm the new ban-list entries actually fire**

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 | grep RS0030
```
Expected: no output (everything routed through `.For`). Then spot-check the ban actually works: temporarily reintroduce one raw `throw new GitCommandFailedException(...)` call, rebuild, confirm RS0030 fires as an error, then revert the temporary change — a ban entry with a typo'd symbol ID silently bans nothing.

- [ ] **Step 6: Run the Git and Prepare tests.**

- [ ] **Step 7: Full-project sanity pass — this is the expensive leg, run once, right before marking the PR ready for review**

```bash
dotnet build -c Release --no-restore --no-incremental -warnaserror 2>&1 | tail -10
dotnet test --no-restore -c Release
```
Note: no `-f` here — this is the one full three-TFM run this plan calls for; every other test step above deliberately scoped to `-f net10.0`. Expected: build succeeds with RS0030 as an error and zero occurrences; full test suite passes at the same count recorded before Task 1 (plus the one new `ServiceSourcesLogTests` test from Task 14) across net8.0, net9.0 and net10.0.

- [ ] **Step 8: Commit**
  ```bash
  git add -A
  git commit -m "$(cat <<'EOF'
Migrate Git*Exception and PrepareLaunchException messages onto the structural escaping seam

Closes the last exception hierarchies this package throws with a caller-controlled name:
the secondary Kubernetes, Git and Prepare exception types are now migrated the same way
the primary ServiceSourcesConfigurationException path was, with the same .For seam,
Name/Raw hole discipline, and RS0030 ban-list coverage.

Closes #385.

Co-Authored-By: Claude Sonnet 5 <noreply@anthropic.com>
EOF
)"
  ```

---

## Self-Review Notes

**Spec coverage:** Items 1 (156 RS0030 sites, Tasks 1–11), 2 (`DeveloperConfigValidator` restructure, Task 11 Step 5), 3 (delete `WarningsNotAsErrors`, Task 12), 4 (unwrap `Label`, Task 13), 5 (`ServiceSourcesLog`, Task 14), 6 (secondary exception types + the two flagged gaps at `KubernetesBackingServiceSource.cs:1051/717`, Tasks 15–16) are all covered. Item 7 (7a/7b) is deliberately excluded per the issue's own framing — these are undecided design questions about the paste-ready contract and truncation marker, not implementation work; flagged in Global Constraints as out of scope. The "Not in scope" list from the issue (`DeferredCheckout.cs` structured-argument sites, `TreatWarningsAsErrors` repo-wide, catalog-name validation) is likewise excluded here.

**Gaps found and closed during code review of this plan (2026-09-20), before any task landed:** an internal audit against the actual worktree source (not just the issue text) found several plan-doc defects that would have surfaced mid-implementation rather than at planning time — all fixed in place:
- Task 11's `Raw.Literal(shape.Kind, default)` example didn't compile (`shape.Kind` is a runtime property, not a `[ConstantExpected]` constant, and `CA1857` is an unconditional build error in this project) — replaced with `Raw.Escaped(shape.Kind)` throughout.
- Task 11's `Raw.Escaped(problems[0])`/`Raw.Escaped(p)` wrapping would have run `Name.Escape` over already-composed diagnostic sentences containing illustrative literal JSON quotes, mangling them — Task 11 Step 5 now converts the ~28 `problems`-producing composer methods in `DeveloperConfigValidator.cs` to return `Raw` directly (mirroring Task 14's composer treatment) instead of re-escaping their finished output.
- Task 4/Task 11 assumed `ServiceCatalogLoader.cs` has one "unknown property" `+`-chain site; it has four (repository/service × top-level/nested) — both tasks' file lists, counts and gates now account for all four.
- Task 10/11 misidentified `DeveloperConfiguration.cs`'s throw sites by line range (an earlier `388-394` citation actually pointed at `NotConfiguredError`, not `AmbiguousCatalogSpellingError`) and missed that `NothingConfiguredError` is a second `+`-chain composer, not a mechanical Task 10 site — both tasks now cite methods by name (re-grep, don't trust a line range) and Task 11 covers both composers.
- Task 15's constructors for `GitCommandFailedException` (single-arg only) and `GitAuthenticationFailedException` (3-arg, with a `NoCredentialsResolved` flag) don't match `ServiceSourcesConfigurationException`'s two-constructor shape — Task 16 Step 2 now lists each `Git*Exception` type's actual constructor list instead of pointing at a "follow the exact pattern" copy-paste.
- Task 16 never added `Git*Exception`'s raw constructors to `BannedSymbols.txt` (only Task 15 banned `KubernetesSecretException`), which would have left the exact caller-controlled-name leak this plan exists to close open indefinitely for the Git exception family — Task 16 now has its own ban-list step plus a step that verifies the ban actually fires.
- `PrepareLaunchException` (`Prepare/IPrepareCommandRunner.cs:47`) has the identical two-constructor shape as the migrated exception types but was absent from the File Map, Task 15 and Task 16 with no stated reason — added to Task 16.
- Task 14 ("close sink B") only closed the `ILogger` sink, missing `ServiceSourcesWarnings.cs:294`'s `logger?.LogWarning` call (its own inventory grep can't match it — the `?.` operator breaks a literal `logger\.Log` pattern) and the entirely separate `Console.Out.WriteLine` sink `ConsolePrepareOutputSink`/`CheckoutPreparation.cs` writes caller-controlled names through — both added to Task 14, and its inventory grep fixed to `logger\??\.Log(...)`.
- The Global Constraints' test-cadence guidance (full three-TFM `dotnet test` after every task, run three separate times across the plan) contradicted this worktree's own `CLAUDE.md`, which scopes intermediate rounds to `-f net10.0` and reserves the full matrix for once, right before landing — every per-task test invocation now uses `-f net10.0`, and only Task 16 Step 7 runs the full matrix.

**Known open risk carried forward, not fixed by this plan:** the two-argument `Enumerable.Zip` laundering path the issue names under "Known-open, deliberately tolerated by #381" is explicitly out of scope — no task here attempts it.

**Sequencing:** Tasks 1–10 (mechanical) and Task 11 (restructure) must land before Task 12 (flip to error) or the build goes red on whatever's left, per the issue's own item-3 caveat. Task 12 must land before Task 13 only in the sense that Task 13 is independent but should follow for a clean single-purpose diff; Tasks 14–16 are independent of 1–13 and could run in parallel with them, but are sequenced last here since they're separable per the issue's own "items 5–7 are separable" framing.
