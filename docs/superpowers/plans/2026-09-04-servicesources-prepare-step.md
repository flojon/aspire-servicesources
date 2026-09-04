# The `prepare` Step Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a `"local"` catalog entry declare a bootstrap command that runs inside the
materialized checkout, once, before the kind is allowed to judge that checkout.

**Architecture:** One `prepare` block on the catalog entry and one on the developer entry's `local`
block, merged per field into a single validated `PrepareStep`; the step runs from one shared
executor called from two places — `LocalProjectSource.Resolve` between `GetRepoRoot` and the kind's
`Validate` (eager), and `DeferredCheckout.StartDeferredAsync` between the landed clone and
`OnCheckoutLanded` (deferred). Once-ness is a marker file keyed on the resolved argv's hash plus the
checked-out commit.

**Tech Stack:** C# / .NET (net8.0;net9.0;net10.0), Aspire 13.5.x hosting, YamlDotNet,
Microsoft.Extensions.Configuration, xUnit.

**Spec:** `docs/superpowers/specs/2026-08-28-servicesources-prepare-step-design.md`
(design amended by #193; resolves #118, also resolves #123)

## Global Constraints

- Package targets `net8.0` as its floor: no `System.Threading.Lock`, no C# 13-only library APIs.
  Plain `object` gates, as `DeferredCheckout` does.
- `ServiceSourcesConfigurationException` is the only exception type a configuration problem is
  reported as, and every message names the service.
- Comments say what the code does, never what a PR changed.
- Public API is frozen for this feature: **no member is added to `ILocalResourceKind`** (spec
  finding 5). `IPrepareCommandRunner`, `PrepareMetadata` and everything else added here is
  `internal`.
- The command must never go through a shell: `UseShellExecute = false`, argv as a list.
- Four modes, spelled `oncePerCommit` (default), `once`, `always`, `never`, accepted
  case-insensitively and rejected by name with all four listed.
- The marker is written **only on success**, through a temporary file and a rename.
- `LocalGitCheckout` stays purely about git: the tool-directory housekeeping moves out of it rather
  than growing a second caller.

---

## File Structure

**Created**

| File | Responsibility |
| --- | --- |
| `src/…/CheckoutRelativePath.cs` | The lexical "is this path absolute / does it climb out" checks, shared by `JavaKindOptions` and the prepare command's first element. |
| `src/…/ToolDirectory.cs` | `<AppHostDirectory>/.servicesources` and the `.gitignore` that hides it — created lazily by whoever needs it. |
| `src/…/Config/PrepareMetadata.cs` | The catalog's `prepare:` block. |
| `src/…/Config/PrepareDeveloperConfig.cs` | The developer's `local.prepare` block. |
| `src/…/Config/DeveloperConfigField.cs` | Whether a developer-config field is a scalar, a list or a nested block. |
| `src/…/Prepare/PrepareMode.cs` | The four modes and their parse. |
| `src/…/Prepare/PrepareStep.cs` | One resolved, validated step: platform-selected argv, mode, command hash. |
| `src/…/Prepare/PreparePlan.cs` | What composition settles: the step to run, or the notice a `path` service gets instead. |
| `src/…/Prepare/PrepareMarker.cs` | The completion record: read, compare, write-by-rename, and where it lives. |
| `src/…/Prepare/IPrepareCommandRunner.cs` | The launch seam, plus `PrepareLaunchException`. |
| `src/…/Prepare/ProcessPrepareCommandRunner.cs` | The process-backed default. |
| `src/…/Prepare/IPrepareOutputSink.cs` | Where a step's lines go — console on the eager path, the resource's logger on the deferred one. |
| `src/…/Prepare/CheckoutPreparation.cs` | The executor both paths call: decide, announce, run, record, fail. |
| `test/…/Prepare/PrepareStepTests.cs` | Schema, merge, modes, confinement, `path` rules. |
| `test/…/Prepare/PrepareMarkerTests.cs` | Marker location, atomicity, malformed handling. |
| `test/…/Prepare/CheckoutPreparationTests.cs` | Mode/marker decisions, reasons reported, failure text. |
| `test/…/Prepare/PrepareEagerPathTests.cs` | Ordering against the kind's `Validate`, the `path` notice, `.servicesources` creation. |
| `test/…/Prepare/PrepareDeferredTests.cs` | Runs after the clone, before `ValidateCheckout` and the helpers; failure is resource state; two services do not serialize. |

**Modified**

| File | Change |
| --- | --- |
| `src/…/Config/ServiceMetadata.cs` | `PrepareMetadata? Prepare`. |
| `src/…/Config/LocalDeveloperConfig.cs` | `PrepareDeveloperConfig? Prepare`. |
| `src/…/Config/DeveloperConfigShape.cs` | A block field can itself be a block or a list. |
| `src/…/Config/DeveloperConfigValidator.cs` | Walk one level deeper; accept indexed children for a list. |
| `src/…/Config/DeveloperConfiguration.cs` | `NormalizeBlankToAbsent` recurses into a nested block. |
| `src/…/Git/IGitClient.cs` | `GetHeadCommitSha`. |
| `src/…/Git/GitCliClient.cs` | Implements it. |
| `src/…/Git/LocalGitCheckout.cs` | `EnsureToolDirectory` delegates the directory and `.gitignore` to `ToolDirectory`. |
| `src/…/Java/JavaKindOptions.cs` | Delegates its three path checks to `CheckoutRelativePath`. |
| `src/…/Sources/LocalProjectSource.cs` | Plans the step before the clone; runs it between `GetRepoRoot` and `Validate`; passes the plan to deferral. |
| `src/…/Sources/DeferredCheckout.cs` | Carries the plan and the runner; runs the step after the clone lands, publishing a "Preparing" state. |
| `src/…/ServiceConfigurationWarnings.cs` | `AddNotice` for a verbatim line buffered to `BeforeStartEvent`. |
| `README.md`, `CHANGELOG.md` | The `prepare` subsection and the release entry. |

---

### Task 1: The shared path checks

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/CheckoutRelativePath.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Java/JavaKindOptions.cs:259-308`
- Test: covered by the existing `JavaKindOptionsTests` (no behaviour changes)

**Interfaces:**
- Produces: `internal static class CheckoutRelativePath` with
  `bool IsAbsolute(string path)`, `bool EscapesRoot(string relativePath)`,
  `string NormalizeSeparators(string relativePath)` — the three methods lifted verbatim from
  `JavaKindOptions`, keeping their remarks.

- [ ] **Step 1:** Create `CheckoutRelativePath` holding the three methods, moved unchanged from
  `JavaKindOptions` along with their XML docs.
- [ ] **Step 2:** Replace `JavaKindOptions`'s private copies with calls to it.
- [ ] **Step 3:** `dotnet test test/Aspire.Hosting.ServiceSources.Java.Tests` — every existing case
  still passes, which is the whole assertion for this task.
- [ ] **Step 4:** Commit.

---

### Task 2: The tool directory, lazily

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/ToolDirectory.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Git/LocalGitCheckout.cs:38-39,164,568-598`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/LocalGitCheckoutTests.cs` (existing coverage)

**Interfaces:**
- Produces:
  ```csharp
  internal static class ToolDirectory
  {
      public const string Name = ".servicesources";
      public static string PathIn(string appHostDirectory);
      public static string Ensure(string appHostDirectory);   // creates it and its .gitignore
  }
  ```

- [ ] **Step 1:** Create `ToolDirectory` with `PathIn`, `Ensure` and the `EnsureGitignore` body moved
  out of `LocalGitCheckout` (`FileMode.CreateNew`, `"*\n!.gitignore\n"`, the same tolerated
  `IOException`/`UnauthorizedAccessException`).
- [ ] **Step 2:** `LocalGitCheckout.EnsureToolDirectory` becomes
  `CheckoutBuildBarrier.Ensure(ToolDirectory.Ensure(appHostDirectory));`, and `ManagedRepoRoot`
  composes from `ToolDirectory.PathIn`. `CheckoutBuildBarrier.Ensure` stays where it is: it is about
  the checkouts, not about the directory.
- [ ] **Step 3:** `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter LocalGitCheckout`.
- [ ] **Step 4:** Commit.

---

### Task 3: `IGitClient.GetHeadCommitSha`

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Git/IGitClient.cs`,
  `src/Aspire.Hosting.ServiceSources/Git/GitCliClient.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/GitCliClientTests.cs`

**Interfaces:**
- Produces: `string? GetHeadCommitSha(string repositoryPath) => null;` on `IGitClient` — defaulted
  for the same reason `EnsureAvailable` is, and because "cannot verify" has to mean "run" rather
  than "assume done".

- [ ] **Step 1:** Write the failing test in `GitCliClientTests`:

```csharp
[Fact]
public void GetHeadCommitSha_ReturnsTheCommitHeadSitsOn()
{
    using var repo = TestRepository.Create();
    var client = new GitCliClient(TestRepository.IsolatedEnvironment);

    var sha = client.GetHeadCommitSha(repo.Path);

    Assert.NotNull(sha);
    Assert.Equal(40, sha!.Length);
}

[Fact]
public void GetHeadCommitSha_IsNullWhereThereIsNoRepository()
{
    var dir = Directory.CreateTempSubdirectory().FullName;

    Assert.Null(new GitCliClient(TestRepository.IsolatedEnvironment).GetHeadCommitSha(dir));
}
```

- [ ] **Step 2:** Run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter GetHeadCommitSha`.
  Expected: FAIL, no such member.
- [ ] **Step 3:** Add the defaulted interface member, and the implementation:

```csharp
public string? GetHeadCommitSha(string repositoryPath)
{
    var result = TryRun(repositoryPath, ["rev-parse", "--verify", "--quiet", "HEAD"]);
    return result.Succeeded && result.FirstLine.Length > 0 ? result.FirstLine : null;
}
```

- [ ] **Step 4:** Re-run the filter. Expected: PASS.
- [ ] **Step 5:** Commit.

---

### Task 4: The catalog block

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/PrepareMetadata.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/ServiceCatalogLoaderTests.cs`,
  `test/Aspire.Hosting.ServiceSources.Tests/Sources/LocalKindRegistryTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed class PrepareMetadata
  {
      public string[]? Command { get; set; }
      public string[]? WindowsCommand { get; set; }
      public string? Mode { get; set; }        // parsed by PrepareMode.Parse, not by YamlDotNet
  }
  ```
  and `ServiceMetadata.Prepare`. `Mode` is a `string?` rather than the enum so an unknown value is
  rejected by name with the four accepted spellings, in the same words from both files.

- [ ] **Step 1:** Write the failing loader tests: a `prepare:` block with `command`,
  `windowsCommand` and `mode` binds; `comand:` inside it is rejected naming the expected set; a
  kind named `prepare` is refused by `LocalKindRegistry.Register` (which reads
  `IsReservedKindName`, itself derived from `ServiceMetadata`'s yaml keys, so this needs no code of
  its own — the test is what says so).
- [ ] **Step 2:** Run the two filters. Expected: FAIL.
- [ ] **Step 3:** Add `PrepareMetadata` and the `ServiceMetadata` property.
- [ ] **Step 4:** Re-run. Expected: PASS.
- [ ] **Step 5:** Commit.

---

### Task 5: The developer block, and the level the config layer gains

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Config/PrepareDeveloperConfig.cs`,
  `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigField.cs`
- Modify: `src/…/Config/LocalDeveloperConfig.cs`, `src/…/Config/DeveloperConfigShape.cs`,
  `src/…/Config/DeveloperConfigValidator.cs`, `src/…/Config/DeveloperConfiguration.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigValidatorTests.cs`,
  `test/Aspire.Hosting.ServiceSources.Tests/Config/DeveloperConfigurationTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  ```csharp
  internal sealed class PrepareDeveloperConfig
  {
      public string[]? Command { get; set; }
      public string[]? WindowsCommand { get; set; }
      public string? Mode { get; set; }

      /// <summary>Whether the developer wrote anything here — an empty block is not a declaration.</summary>
      public bool IsDeclared => Command is not null || WindowsCommand is not null || Mode is not null;
  }

  internal static class DeveloperConfigField
  {
      public static bool IsList(Type type);                                  // string[] and friends
      public static IReadOnlyDictionary<string, Type>? BlockFieldsOf(Type type);  // null for a scalar or a list
  }
  ```
  `LocalDeveloperConfig.Prepare` is **nullable**, unlike the source blocks: "the developer declared
  no block" and "the developer declared one" are different answers on a `path` service, so there is
  something for null to mean.

- [ ] **Step 1:** Write the failing validator tests:

```csharp
[Fact]
public void PrepareBlock_Binds()
{
    var config = Configuration("""
        { "services": { "orders": { "source": "local",
            "local": { "prepare": { "command": ["./prepare.sh"], "mode": "once" } } } } }
        """);

    DeveloperConfigValidator.ValidateAll(
        config.GetSection(DeveloperConfiguration.ServicesKey).GetChildren(),
        DeveloperConfigShape.Service);   // does not throw

    var bound = config.GetSection(DeveloperConfiguration.ServicesKey)
        .Get<Dictionary<string, ServiceDeveloperConfig>>()!;

    Assert.Equal(["./prepare.sh"], bound["orders"].Local.Prepare!.Command);
    Assert.Equal("once", bound["orders"].Local.Prepare!.Mode);
}

[Fact]
public void UnknownKeyInsidePrepare_IsRejectedByName()      // "comand" → names local.prepare's keys
[Fact]
public void CommandWrittenAsAScalar_IsRejected()            // "command": "./prepare.sh"
[Fact]
public void CommandList_IsNotReportedAsABlock()             // finding 9's trap, both halves
[Fact]
public void PrepareWrittenAsAValue_IsRejected()             // "prepare": "./prepare.sh"
[Fact]
public void PrepareUnderANonLocalSource_IsInert()           // a block nothing reads
```

- [ ] **Step 2:** Run: `dotnet test … --filter DeveloperConfigValidator`. Expected: FAIL — the
  current validator reports `prepare` as "takes a value, not a block of settings", and `command`
  the same way, which is exactly what these tests are added to stop.
- [ ] **Step 3:** Implement:
  - `DeveloperConfigField.IsList` — an array, or a closed generic `IEnumerable<T>` that is not
    `string`. Asked **before** the block question, because `string[]` passes `IsClass`.
  - `DeveloperConfigShape`: exclude lists from `Blocks`, so a list at the entry root is never walked
    for fields it does not have.
  - `DeveloperConfigValidator.Collect`: after the field-name lookup, branch on the field's type —
    a list accepts indexed children and refuses a scalar; a nested block refuses a value and
    recurses with a `"local.prepare"`-shaped block path; anything else keeps today's scalar checks.
  - `DeveloperConfiguration.NormalizeBlankToAbsent`: recurse into a non-null nested block so an
    empty `mode` means unset there too.
- [ ] **Step 4:** Re-run the filter, then the whole config test class — the existing near-miss and
  misplaced-key messages must be unchanged.
- [ ] **Step 5:** Commit.

---

### Task 6: `PrepareMode`, `PrepareStep` and the merge

**Files:**
- Create: `src/…/Prepare/PrepareMode.cs`, `src/…/Prepare/PrepareStep.cs`,
  `src/…/Prepare/PreparePlan.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Prepare/PrepareStepTests.cs`

**Interfaces:**
- Consumes: `PrepareMetadata`, `PrepareDeveloperConfig`, `CheckoutRelativePath`.
- Produces:
  ```csharp
  internal enum PrepareMode { OncePerCommit, Once, Always, Never }

  internal static class PrepareModes
  {
      public const PrepareMode Default = PrepareMode.OncePerCommit;
      public static PrepareMode Parse(string serviceName, string? written, string writtenAt);
  }

  internal sealed record PrepareStep(IReadOnlyList<string> Command, PrepareMode Mode)
  {
      public string CommandHash { get; }              // sha256 of the resolved argv
      public string Describe();                        // the argv as one readable line
  }

  internal sealed record PreparePlan(PrepareStep? Step, string? IgnoredCatalogNotice)
  {
      public static readonly PreparePlan Nothing = new(null, null);

      public static PreparePlan For(
          string serviceName,
          PrepareMetadata? catalog,
          PrepareDeveloperConfig? developer,
          bool managedCheckout,
          bool windows);
  }
  ```
  `For` does the whole of composition-time prepare validation and no filesystem work, so it can run
  in front of the clone and ahead of `ShouldDefer` — covering both paths.

- [ ] **Step 1:** Write the failing tests, one per rule the spec states:
  - default mode is `oncePerCommit`; each of the four parses case-insensitively; an unknown value is
    rejected naming all four and the key it was written at.
  - `windowsCommand` replaces `command` when `windows: true` and is ignored otherwise; `command`
    runs on Windows with no `windowsCommand` set.
  - the developer's `mode` overrides alone; the developer's `command` replaces the catalog's
    `command` **and** `windowsCommand` together; a developer block with no catalog block stands on
    its own; an absent developer block leaves the catalog's alone — the override table, row by row.
  - a first element climbing out of the checkout (`../../thing`) or absolute (`/bin/sh`, `C:\x`) is
    rejected; a bare `make` is left for `PATH`; `./prepare.sh` is kept relative.
  - `managedCheckout: false` (a `path` service) never inherits the catalog block, and yields
    `IgnoredCatalogNotice` naming the service and the command when the developer declared nothing;
    a developer block on the same service yields a step and no notice; `mode` without `command`
    there is rejected by name, except `mode: never`, which stands alone and runs nothing.
  - `mode: never` yields no step from either layer.
  - the command hash changes with the platform variant, so switching platforms re-runs.
- [ ] **Step 2:** Run: `dotnet test … --filter PrepareStepTests`. Expected: FAIL.
- [ ] **Step 3:** Implement the three files.
- [ ] **Step 4:** Re-run. Expected: PASS.
- [ ] **Step 5:** Commit.

---

### Task 7: The marker

**Files:**
- Create: `src/…/Prepare/PrepareMarker.cs`
- Test: `test/…/Prepare/PrepareMarkerTests.cs`

**Interfaces:**
- Consumes: `ToolDirectory`.
- Produces:
  ```csharp
  internal sealed record PrepareMarker(string CommandHash, string? Commit, string CompletedUtc, string? Path)
  {
      public static string LocationFor(string serviceName, string repoRoot, string appHostDirectory, bool managedCheckout);
      public static PrepareMarker? Read(string markerPath);                  // null if absent, unreadable or malformed
      public static void Write(string markerPath, PrepareMarker marker, string appHostDirectory, bool managedCheckout);
  }
  ```
  Managed: `<repoRoot>/.git/servicesources-prepare.json`. `path`:
  `<AppHostDirectory>/.servicesources/prepare/<service>.json`, carrying the normalized absolute
  checkout path as a fourth key, and creating the tool directory (with its `.gitignore`) on the way.

- [ ] **Step 1:** Write the failing tests: location per checkout kind; a round trip; an absent file
  reads as `null`; a file of garbage reads as `null` rather than throwing; the write goes through a
  temporary file and a rename, so a concurrent reader sees the old record or the new one and never a
  truncated file; a `path` marker creates `.servicesources/` **and** its `.gitignore`.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement with `System.Text.Json`, `File.Move(scratch, path, overwrite: true)`.
- [ ] **Step 4:** Re-run. Expected: PASS.
- [ ] **Step 5:** Commit.

---

### Task 8: The runner and the output sink

**Files:**
- Create: `src/…/Prepare/IPrepareCommandRunner.cs`,
  `src/…/Prepare/ProcessPrepareCommandRunner.cs`, `src/…/Prepare/IPrepareOutputSink.cs`
- Test: `test/…/Prepare/CheckoutPreparationTests.cs` (fake runner; no test spawns a process)

**Interfaces:**
- Produces:
  ```csharp
  internal interface IPrepareCommandRunner
  {
      /// <summary>Runs the command in <paramref name="workingDirectory"/>, streaming each line of
      /// its output to <paramref name="onLine"/> as it arrives, and returns its exit code.</summary>
      /// <exception cref="PrepareLaunchException">The command could not be started at all.</exception>
      int Run(string workingDirectory, IReadOnlyList<string> command, Action<string> onLine);
  }

  internal sealed class PrepareLaunchException(string message, Exception? inner = null)
      : Exception(message, inner);

  internal interface IPrepareOutputSink
  {
      /// <summary>One line about this step: why it is running, what it runs, or what it wrote.</summary>
      void Report(string line);
  }
  ```
  `ProcessPrepareCommandRunner` resolves a first element that looks like a path against the working
  directory with `Path.GetFullPath(first, workingDirectory)` — explicitly, because a relative
  `FileName` resolves against the *process's* working directory rather than the one in
  `ProcessStartInfo`.

- [ ] **Step 1:** Implement (no unit test spawns a process — the seam is what the tests substitute).
- [ ] **Step 2:** `dotnet build`. Expected: clean.
- [ ] **Step 3:** Commit.

---

### Task 9: The executor

**Files:**
- Create: `src/…/Prepare/CheckoutPreparation.cs`
- Test: `test/…/Prepare/CheckoutPreparationTests.cs`

**Interfaces:**
- Consumes: `PrepareStep`, `PrepareMarker`, `IPrepareCommandRunner`, `IPrepareOutputSink`,
  `IGitClient.GetHeadCommitSha`.
- Produces:
  ```csharp
  internal static class CheckoutPreparation
  {
      public static void Run(
          string serviceName,
          PrepareStep step,
          string repoRoot,
          string appHostDirectory,
          bool managedCheckout,
          IGitClient gitClient,
          IPrepareCommandRunner runner,
          IPrepareOutputSink sink);
  }
  ```

- [ ] **Step 1:** Write the failing tests, per the spec's Testing section: marker absent → runs;
  matching → skips; command changed → re-runs; commit changed → re-runs under `oncePerCommit` and
  **not** under `once`; `always` ignores a matching marker; `never` runs nothing and writes nothing;
  a failed step writes no marker, so it re-runs; a null commit runs rather than skips; every
  decision to run reports its reason and the resolved command, and a skip reports nothing; a
  non-zero exit throws naming service, command and exit code with the output tail; a launch failure
  is distinguished and, on Windows with no `windowsCommand`, names that as the likely cause.
- [ ] **Step 2:** Run: `dotnet test … --filter CheckoutPreparation`. Expected: FAIL.
- [ ] **Step 3:** Implement: read the marker, compare against `(step.CommandHash, headSha)`, run,
  stream each line as `[prepare <service>] <line>` while keeping the last 20 in a ring for the
  failure text, write the marker on success.
- [ ] **Step 4:** Re-run. Expected: PASS.
- [ ] **Step 5:** Commit.

---

### Task 10: The eager path

**Files:**
- Modify: `src/…/Sources/LocalProjectSource.cs`, `src/…/ServiceConfigurationWarnings.cs`
- Test: `test/…/Prepare/PrepareEagerPathTests.cs`

**Interfaces:**
- Consumes: `PreparePlan.For`, `CheckoutPreparation.Run`.
- Produces: `LocalProjectSource(IGitClient gitClient, IPrepareCommandRunner? prepareRunner = null)`
  — defaulted so every existing construction site compiles, and so production gets the process-backed
  one; `ServiceConfigurationWarnings.AddNotice(string message)`.

- [ ] **Step 1:** Write the failing tests: the step runs after the checkout is reconciled and before
  the kind's `Validate` (a kind whose `Validate` requires the file the step produces passes, and the
  same pair swapped fails); a `dotnet` service whose `.csproj` the step produces resolves; a catalog
  block on a `path` service runs nothing and emits the notice naming the command; a developer block
  on that service runs and suppresses the notice; `.servicesources/` is created for a `path`-only
  AppHost that prepares and not for one that declares no step.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement: plan the step where the kind-name probe already sits (before the
  prefetch and before `ShouldDefer`, so it covers both paths); run it between `GetRepoRoot` and the
  kind dispatch; buffer the `path` notice.
- [ ] **Step 4:** Re-run, then the whole `Aspire.Hosting.ServiceSources.Tests` project.
- [ ] **Step 5:** Commit.

---

### Task 11: The deferred path

**Files:**
- Modify: `src/…/Sources/DeferredCheckout.cs`, `src/…/Sources/LocalProjectSource.cs`
- Test: `test/…/Prepare/PrepareDeferredTests.cs`, and one case each in
  `test/Aspire.Hosting.ServiceSources.Java.Tests/JavaDeferredCheckoutTests.cs` and
  `test/Aspire.Hosting.ServiceSources.JavaScript.Tests/JavaScriptDeferredCheckoutTests.cs`

**Interfaces:**
- Consumes: `PreparePlan`, `CheckoutPreparation.Run`.
- Produces: `DeferredCheckout.Register`/`RegisterKind` each take `PreparePlan plan` and
  `IPrepareCommandRunner runner`; the private `Deferred` record carries both; a new
  `PreparingState = "Preparing"` published alongside `"Checking out"`.

- [ ] **Step 1:** Write the failing tests: a deferred service runs its step in the background task
  after the clone lands, before `ValidateCheckout` and before the held-back helpers are started, and
  not during composition; its failure surfaces as resource state rather than as an exception; the
  resource carries "Preparing" while it runs; two deferred services prepare without either waiting
  on the other. Assert on ordering and on the state finally reached, never on a sequence of
  intermediate snapshots — a resource watch replays only the current one, which is what made the
  #189 tests flake.
- [ ] **Step 2:** Run. Expected: FAIL.
- [ ] **Step 3:** Implement: run the step immediately after the resolved-`repoRoot` comparison and
  before `OnCheckoutLanded`, with a sink over the resource's `ILogger`.
- [ ] **Step 4:** Re-run, then the full solution's tests.
- [ ] **Step 5:** Commit.

---

### Task 12: Documentation

**Files:**
- Modify: `README.md` (a `prepare` subsection under `"local"` source options), `CHANGELOG.md`
  (under `[Unreleased]` → `### Added`)

- [ ] **Step 1:** Write the README subsection: the schema, the override table, the marker and how to
  force a re-run, the four modes and the "does the repository define this step?" question that picks
  between `once` and `oncePerCommit`, the requirement that the command be safe to re-run under all
  of them, the rule that a `path` checkout declares its own step rather than inheriting the
  catalog's (and that declaring any block, `{"mode": "never"}` included, silences the notice), and
  the rule that a command must tolerate running alongside a *different* service's command when
  checkouts are deferred.
- [ ] **Step 2:** Add the `CHANGELOG.md` entry.
- [ ] **Step 3:** `dotnet build -warnaserror` and the full test run.
- [ ] **Step 4:** Commit.

---

## Self-Review

**Spec coverage.** Schema (catalog) → Task 4. Schema (developer) and the override rule → Tasks 5, 6.
Where the step runs, both paths → Tasks 10, 11. Concurrency → Task 11 (scoped, no gate: nothing to
implement beyond the test that says two services do not serialize). Once-ness and the marker →
Task 7. Modes and change detection → Tasks 6, 9. Execution and output → Tasks 8, 9. Failure →
Task 9 (text) and Tasks 10, 11 (where it lands). `path` checkouts → Tasks 6, 7, 10. Consequences
accepted → nothing to build. Testing → each task's own step 1. Documentation → Task 12.

**Not in scope, per the spec.** No `ILocalResourceKind` member (finding 5); no `WithPrepare` fluent
method (that lands with #134); no timeout; no injected environment variables; no `ifMissing`,
timestamps or guard command; no reach into backing services.

**Types used consistently.** `PrepareMetadata`/`PrepareDeveloperConfig` are the two bound shapes;
`PrepareStep` is the only validated one; `PreparePlan` is what crosses from composition into either
path; `CheckoutPreparation.Run` is the single executor both call.
