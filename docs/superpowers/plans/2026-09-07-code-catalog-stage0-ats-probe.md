# Code Catalog Stage 0 — ATS Probe Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Measure, on the real ATS toolchain, whether the five shapes finding 7 of the code-catalog
design flags as unmeasured actually cross Aspire's Type System — before any public signature in
Stage 1 is frozen — and write up the result as a findings document. No production code ships from
this stage; the probe code is written, measured, and reverted.

**Architecture:** A throwaway C# file under `src/Aspire.Hosting.ServiceSources/` declares five
minimal `[AspireExport]`-marked members, one per unmeasured shape, using names that cannot collide
with anything real. `samples/DemoAppHostTypeScript` already points its `aspire.config.json` at
`../../src`, so `aspire restore` regenerates the TypeScript SDK against the probe the moment it is
added — no new sample project needed. `apphost.mts` gets temporary calls added, is strict-`tsc`'d
against `tsconfig.apphost.json` (the same file and command the `📘 typescript export surface` CI job
runs), then both the probe file and the `apphost.mts` edit are reverted. Only the findings document
and, if a shape fails, a note in the design spec's own findings section survive.

**Tech Stack:** C# (Aspire.Hosting.ServiceSources), TypeScript (`samples/DemoAppHostTypeScript`),
Aspire CLI 13.5.3 (installed to a tool path — the CLI on `PATH` here is 13.5.1 and fails `aspire
restore` with NU1605 against this repo's `$(AspireVersion)` floor of 13.5.2), `npx tsc --noEmit`.

**Spec:** `docs/superpowers/specs/2026-09-05-servicesources-code-catalog-design.md`, finding 7 (the
five unmeasured shapes table) and the Staging section (Stage 0's own row).

## Global Constraints

- No shipped code. Every file this plan creates or edits outside the findings document itself is
  reverted in Task 5, before the branch is committed. (Design's Staging table: "Stage 0 … No shipped
  code.")
- The Aspire CLI used for `aspire restore` must be ≥ `$(AspireVersion)` (13.5.2, checked in
  `Directory.Build.props:74`); the CLI writes its own version into the generated host project, so a
  CLI below the floor fails restore with NU1605 before codegen runs (design, finding 8).
- The whole `.aspire/` directory under `samples/DemoAppHostTypeScript` must be deleted between
  `aspire restore` runs, not just `.aspire/modules/` — a stale generator survives in a prior restore's
  `IntegrationRestore` project and keeps emitting old output while reporting success
  (microsoft/aspire#19603; design, finding 8).
- Two `[AspireExport]` members cannot share a generated capability ID
  (`{AssemblyName}/{camelCaseMethodName}`, unless one carries an explicit `MethodName`) — the
  standing rule `ServiceConfigurationExports.cs`'s remarks already document. Probe method names must
  therefore avoid colliding with anything already exported by this assembly, **except** shape 5's
  probe, which deliberately reuses the exact name `AddService` to test whether receiver type
  disambiguates it from `ServiceSourcesBuilderExtensions.AddService` — see Task 1.

---

## Task 1: Add the throwaway probe declarations and confirm a clean build

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Catalog/Stage0AtsProbe.cs`

**Interfaces:**
- Produces: `Stage0ProbeMode` (enum: `Once`, `Always`), `Stage0ProbeCatalogBuilder` (sealed class,
  `[AspireExport(ExposeMethods = true)]`), `Stage0ProbeJavaOptionsBuilder` (sealed class,
  `[AspireExport(ExposeMethods = true)]`, one method `MavenGoal(string goal)`), and five
  `[AspireExport]` extension methods on `Stage0AtsProbeExtensions` — see Step 1's listing. Task 3
  consumes all of these by name.

- [ ] **Step 1: Write the probe file**

```csharp
using Aspire.Hosting;

namespace Aspire.Hosting.ServiceSources.Catalog;

// Throwaway ATS probe for the five shapes docs/superpowers/specs/2026-09-05-servicesources-code-
// catalog-design.md (finding 7) flags as unmeasured. Reverted before this stage's PR — see
// docs/superpowers/specs/2026-09-07-code-catalog-stage0-ats-probe-findings.md, the document this
// probe produces. Not part of the shipped API; if you are reading this in a merged commit, Task 5
// of the stage-0 plan was skipped.

/// <summary>Shape 4 probe value: an enum used as a plain parameter and as an optional one.</summary>
public enum Stage0ProbeMode
{
    Once,
    Always,
}

/// <summary>Shape 2/3 probe receiver: a package-owned class exported to ATS as a handle.</summary>
[AspireExport(ExposeMethods = true)]
public sealed class Stage0ProbeCatalogBuilder
{
}

/// <summary>Shape 3 probe: the nested lambda's own receiver, one level deeper than the catalog.</summary>
[AspireExport(ExposeMethods = true)]
public sealed class Stage0ProbeJavaOptionsBuilder
{
    public Stage0ProbeJavaOptionsBuilder MavenGoal(string goal) => this;
}

public static class Stage0AtsProbeExtensions
{
    // Shape 1: optional/named parameters, and a nullable string array.
    [AspireExport]
    public static Stage0ProbeCatalogBuilder Stage0ProbeWithRepository(
        this Stage0ProbeCatalogBuilder catalog, string url, string? defaultRef = null) => catalog;

    [AspireExport]
    public static Stage0ProbeCatalogBuilder Stage0ProbeWithPrepare(
        this Stage0ProbeCatalogBuilder catalog,
        string[] command,
        string[]? windowsCommand = null,
        Stage0ProbeMode mode = Stage0ProbeMode.Once) => catalog;

    // Shape 2: an extension method whose receiver is a package-owned exported class (not an Aspire
    // type, and not IDistributedApplicationBuilder).
    // Shape 3 (combined with shape 2's receiver, exercised by the call site in Task 3): a lambda
    // nested inside AddServiceCatalog's own lambda.
    [AspireExport]
    public static Stage0ProbeCatalogBuilder Stage0ProbeAsJava(
        this Stage0ProbeCatalogBuilder catalog, Action<Stage0ProbeJavaOptionsBuilder> configure) => catalog;

    // Shape 4 in isolation: an enum as a required parameter, not entangled with shape 1's optional
    // parameters (Stage0ProbeWithPrepare's `mode` already covers the optional-enum case).
    [AspireExport]
    public static Stage0ProbeCatalogBuilder Stage0ProbeWithMode(
        this Stage0ProbeCatalogBuilder catalog, Stage0ProbeMode mode) => catalog;

    // Shape 3's outer lambda, entry point: RunSyncOnBackgroundThread = true, matching the design's
    // AddServiceCatalog signature exactly.
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static IDistributedApplicationBuilder Stage0ProbeAddServiceCatalog(
        this IDistributedApplicationBuilder builder, Action<Stage0ProbeCatalogBuilder> configure) => builder;

    // Shape 5: the generated capability name "addService" on two receivers. Deliberately named
    // AddService with no explicit id/MethodName, to test whether ATS disambiguates by receiver type
    // or collides with ServiceSourcesBuilderExtensions.AddService(this IDistributedApplicationBuilder, …)
    // at src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs:84.
    [AspireExport]
    public static Stage0ProbeCatalogBuilder AddService(
        this Stage0ProbeCatalogBuilder catalog, string name) => catalog;
}
```

- [ ] **Step 2: Build the package**

Run: `dotnet build src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj -c Release`
Expected: `Build succeeded.` — the probe file is plain C#; nothing here should fail to compile. If it
does, fix the probe file (not the shipped source) before continuing.

- [ ] **Step 3: Commit nothing yet**

This file is reverted in Task 5. Do not commit it. Proceed directly to Task 2.

---

## Task 2: Regenerate the ATS surface and strict-typecheck the ambient declarations

**Files:**
- None created or modified — this task only runs the existing CI toolchain (`aspire restore`,
  `npx tsc --noEmit`) against the probe added in Task 1, unmodified `apphost.mts`.

**Interfaces:**
- Consumes: the probe types from Task 1 (not called yet — this task only checks that their generated
  ambient declarations compile without error alongside the rest of the generated SDK).
- Produces: a captured `tsc` transcript, recorded in Task 5's findings document.

- [ ] **Step 1: Ensure the Aspire CLI floor is on PATH for this shell**

Run (adjust the tool path to wherever it was installed for this job):
`export PATH="<tool-path>:$PATH" && aspire --version`
Expected: `13.5.3` (or any version ≥ 13.5.2 — the repo's `$(AspireVersion)` floor). If it prints
`13.5.1`, `aspire restore` in the next step will fail with NU1605; install the floor first:
`dotnet tool install --tool-path <tool-path> aspire.cli --version 13.5.3`.

- [ ] **Step 2: Delete the whole `.aspire/` directory, not just `modules/`**

Run: `rm -rf samples/DemoAppHostTypeScript/.aspire`
Expected: no output. Skipping this step risks a stale generator surviving in a previous restore's
`IntegrationRestore` project bin/ output and silently emitting old code (microsoft/aspire#19603) —
the Global Constraints section states why.

- [ ] **Step 3: Regenerate the SDK**

Run in a subshell, so the shell's working directory is unchanged for every later step in this plan
(every other command in this plan is written relative to the repo root):
`(cd samples/DemoAppHostTypeScript && aspire restore --non-interactive --nologo)`
Expected: exits 0, and `samples/DemoAppHostTypeScript/.aspire/modules/aspire.mts` now exists. This
does not by itself prove the SDK is valid — the next step is what gates it.

- [ ] **Step 4: Strict-typecheck the generated SDK against the unmodified sample**

Run, also in a subshell: `(cd samples/DemoAppHostTypeScript && npx tsc --noEmit -p tsconfig.apphost.json)`
Expected: `0 errors` (the sample as committed already type-checks clean per the design's finding 8
measurement). A new error here — referencing a probe-derived symbol, or a duplicate-identifier
diagnostic on `addService` — is itself a finding about shape 5 or about a declaration-level
collision; record the exact diagnostic text verbatim in Task 5, then continue to Task 3 regardless
(the call-site probes in Task 3 are still worth running even if a declaration-level collision already
shows a shape failing).

---

## Task 3: Exercise realistic call syntax for all five shapes

**Files:**
- Modify (temporarily — reverted in Task 5): `samples/DemoAppHostTypeScript/apphost.mts`

**Interfaces:**
- Consumes: the generated `.mts` bindings for every member Task 1 declared. The exact generated
  identifier names (e.g. whether `Stage0ProbeWithRepository` projects as
  `stage0ProbeWithRepository`) are only knowable after Task 2's `aspire restore` — read them out of
  `samples/DemoAppHostTypeScript/.aspire/modules/aspire.mts` before writing this step's code, rather
  than guessing the casing convention.
- Produces: a captured `tsc` transcript per shape, recorded in Task 5's findings document.

- [ ] **Step 1: Read the generated declarations for the probe surface**

Run: `grep -n "stage0Probe\|Stage0Probe" samples/DemoAppHostTypeScript/.aspire/modules/aspire.mts`
Expected: one declaration per probe member from Task 1, each under the receiver type ATS generated
for `Stage0ProbeCatalogBuilder` / `Stage0ProbeJavaOptionsBuilder`. Use these exact generated names in
Step 2 — do not assume a casing convention without reading it here.

- [ ] **Step 2: Append probe calls to `apphost.mts`, using the exact generated names read in Step 1**

Add before the final `await builder.build().run();` line, replacing the placeholder call names below
with whatever Step 1 actually read out of the generated `.mts` file (they are expected to be the
camelCase form of the C# names, e.g. `stage0ProbeAddServiceCatalog`, `stage0ProbeWithRepository`,
`stage0ProbeWithPrepare`, `stage0ProbeAsJava`, `stage0ProbeWithMode`, `addService` on the catalog
receiver — but this step's whole point is confirming that against the real output, not assuming it):

```typescript
// Stage 0 ATS probe — temporary, reverted before this stage's PR. See
// docs/superpowers/plans/2026-09-07-code-catalog-stage0-ats-probe.md, Task 3.
await builder.stage0ProbeAddServiceCatalog(async (catalog) => {
  // Shape 1: optional/named parameters, and a nullable string array.
  const withRepo = catalog.stage0ProbeWithRepository('https://github.com/example/probe', { defaultRef: 'main' });
  const withPrepare = withRepo.stage0ProbeWithPrepare(['./prepare.sh'], {
    windowsCommand: ['pwsh', '-File', 'prepare.ps1'],
    mode: 'Once',
  });
  // Shape 4 in isolation: enum as a required parameter.
  withPrepare.stage0ProbeWithMode('Always');
  // Shape 2 + 3: extension method on a package-owned receiver, called from inside the outer
  // lambda — a second level of guest-to-host re-entrancy.
  withPrepare.stage0ProbeAsJava(async (options) => {
    options.mavenGoal('spring-boot:run');
  });
  // Shape 5: the generated name addService on a second receiver, alongside the existing
  // builder.addService(...) used earlier in this file.
  withPrepare.addService('probe-nested-service');
});
```

Adjust each call's argument style (positional vs. an options object for the optional parameters,
whether the enum is passed as a string literal or a numeric value, whether `async`/`await` is
required inside the nested lambda) to whatever the generated types in Step 1 actually declare — the
generated signature is the ground truth, not this listing.

- [ ] **Step 3: Strict-typecheck with the probe calls in place**

Run, in a subshell: `(cd samples/DemoAppHostTypeScript && npx tsc --noEmit -p tsconfig.apphost.json)`
Expected: recorded verbatim regardless of outcome. For each of the five shapes, note in Task 5 whether
this run reported zero errors attributable to that shape's call, or the exact diagnostic (code and
message) it produced.

- [ ] **Step 4: Do not commit**

`apphost.mts` is reverted in Task 5.

---

## Task 4: Apply the documented fallback for any shape that failed, and re-measure only that shape

**Files:**
- Modify (temporarily — reverted in Task 5): `src/Aspire.Hosting.ServiceSources/Catalog/Stage0AtsProbe.cs`
- Modify (temporarily — reverted in Task 5): `samples/DemoAppHostTypeScript/apphost.mts`

**Interfaces:**
- Consumes: Task 3's per-shape pass/fail results.
- Produces: a second, fallback-shape transcript for Task 5, only for shapes that failed. Skip this
  task entirely if Task 3 found all five shapes clean.

The design (finding 7) already names the fallback for each shape if it fails to cross:

| Shape | If it fails, the fallback is |
| --- | --- |
| 1 — optional/named parameters, `string[]?` | No optional parameters: split into two required-parameter overloads-by-name (e.g. `Stage0ProbeWithRepository` / `Stage0ProbeWithRepositoryAndRef`), and pass `string[]` (never-null, empty array for "none") instead of `string[]?`. |
| 2 — extension method on a package-owned receiver | An instance method on the builder class itself (`[AspireExport(ExposeMethods = true)]` already projects those) instead of a separate extension-method class. |
| 3 — nested lambda | Flatten one level: the inner configuration becomes a second top-level call (e.g. `catalog.stage0ProbeAsJava()` returning the options handle directly, configured by further chained calls) rather than a lambda argument. |
| 4 — enum as a parameter | Accept the enum's string spelling as a `string` parameter instead, parsed with the same total `Spellings.First(...)` lookup `PrepareModes.Written` already uses (design, finding 3), guarded by `Enum.IsDefined`-equivalent validation against the string. |
| 5 — `addService` name collision | An explicit `MethodName` (e.g. `[AspireExport(MethodName = "addServiceToCatalog")]`) on the catalog builder's method. |

- [ ] **Step 1: For each failed shape, edit `Stage0AtsProbe.cs` to the fallback shape from the table above**

Make the smallest edit that changes only the failed shape's declaration — do not touch the four
shapes that already passed.

- [ ] **Step 2: Rebuild**

Run: `dotnet build src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj -c Release`
Expected: `Build succeeded.`

- [ ] **Step 3: Regenerate and re-typecheck, per Task 2 Steps 2–4 and Task 3**

Same commands, same `rm -rf .aspire` discipline. Update `apphost.mts`'s call site for the shape that
changed shape, using the fallback's new generated name (read fresh from
`.aspire/modules/aspire.mts`, per Task 3 Step 1 — do not reuse the old name).
Expected: `0 errors` for the previously-failing shape. If it still fails, record that too — the
finding is "no ATS shape probed here crosses cleanly for this construct", which is exactly the kind
of result the design's own fallback table exists to be wrong about, and worth knowing before Stage 1.

---

## Task 5: Write the findings document, revert the probe, and confirm the tree is clean

**Files:**
- Create: `docs/superpowers/specs/2026-09-07-code-catalog-stage0-ats-probe-findings.md`
- Modify: `docs/superpowers/specs/2026-09-05-servicesources-code-catalog-design.md` (Status header
  only — add a line noting Stage 0 is measured and linking the findings document, following the
  pattern `2026-08-28-servicesources-prepare-step-design.md:4` and this design's own Status header
  already use for revision notes)
- Delete: `src/Aspire.Hosting.ServiceSources/Catalog/Stage0AtsProbe.cs`
- Revert: `samples/DemoAppHostTypeScript/apphost.mts` (and its `.aspire/` directory, which is
  gitignored and does not need a revert, only cleanup)

**Interfaces:**
- Consumes: Task 2, 3 and 4's captured transcripts.
- Produces: the findings document Stage 1's plan reads before any public signature there is frozen.

- [ ] **Step 1: Write the findings document**

Follow the structure of `docs/superpowers/specs/2026-08-30-19507-already-fixed-findings.md` — a
measured-not-reasoned-about report, not a narrative. Include, for each of the five shapes: the exact
C# declaration probed, the exact call site probed, the verbatim `tsc` output (or "0 errors"), and —
if it failed — the fallback applied and its result. Close with a one-paragraph verdict on whether
Stage 1 can proceed on the design's signatures as specified, or which ones must change and to what,
referencing the fallback table in Task 4.

- [ ] **Step 2: Update the design spec's Status header**

Add a line after the existing Status paragraph in
`docs/superpowers/specs/2026-09-05-servicesources-code-catalog-design.md`, e.g.:

```markdown
Stage 0's ATS probe is measured; see
[the stage-0 findings](2026-09-07-code-catalog-stage0-ats-probe-findings.md). <one clause stating
whether all five shapes crossed as specified, or which did not>.
```

- [ ] **Step 3: Revert the throwaway probe**

Run:
```bash
rm -f src/Aspire.Hosting.ServiceSources/Catalog/Stage0AtsProbe.cs
git checkout -- samples/DemoAppHostTypeScript/apphost.mts
rm -rf samples/DemoAppHostTypeScript/.aspire
rmdir src/Aspire.Hosting.ServiceSources/Catalog 2>/dev/null || true
```
Expected: `git status --short` shows only the two new/modified docs files from Steps 1–2 — nothing
under `src/` or `samples/`.

- [ ] **Step 4: Confirm the reverted tree still builds and tests clean**

Run: `dotnet build -c Release -warnaserror && dotnet test -c Release --no-build`
Expected: both succeed, unchanged from `main` — this stage ships no code, so there is nothing here
that should differ from the baseline measured before Task 1.

- [ ] **Step 5: Commit**

```bash
git add docs/superpowers/specs/2026-09-07-code-catalog-stage0-ats-probe-findings.md \
        docs/superpowers/specs/2026-09-05-servicesources-code-catalog-design.md
git commit -m "Measure the five unmeasured ATS shapes the code-catalog design depends on (#134)"
```

---

## Self-Review Notes

- **Spec coverage:** every row of finding 7's table (optional/named parameters + `string[]?`,
  extension-method receiver, nested lambda, enum parameter, `addService` name collision) has a
  dedicated probe member in Task 1 and a dedicated call in Task 3. The Staging table's Stage 0 row
  ("no shipped code") is enforced by Task 5's revert step and its `git status` check.
- **No placeholders:** Task 3's call-site code is intentionally written as "read the real generated
  name before finalizing this step" rather than a guessed identifier, because the generated casing
  convention is verifiable in minutes and guessing it risks the plan asserting a false fact about
  codegen — exactly the mistake this probe exists to avoid elsewhere. This is a deliberate
  verify-before-writing instruction, not a missing detail: Task 3 Step 1 names the exact command that
  resolves it.
- **Type consistency:** `Stage0ProbeCatalogBuilder`, `Stage0ProbeJavaOptionsBuilder` and
  `Stage0ProbeMode` are declared once in Task 1 and referenced by the same names in Tasks 3 and 4;
  no task introduces a same-purpose type under a different name.
