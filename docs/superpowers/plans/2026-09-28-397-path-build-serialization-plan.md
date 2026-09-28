# Path-service build serialization Implementation Plan (#397)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Two `"path"` dotnet services in one repository no longer race on shared `bin/`/`obj/`: a per-group gate serializes a `dotnet build` in a `BeforeResourceStartedEvent` handler, then releases before the process runs.

**Architecture:** `BuildGroupKey` (pure) derives a key (`g:<buildGroup>` | `p:<git root>` | `p:<service dir>`). `PathBuildGate` (per-builder, `ConditionalWeakTable`, same shape as `CheckoutNameLock.For`) holds a member registry and one `SemaphoreSlim` per key, and runs the build through an `IBuildRunner` seam (fake in tests, `dotnet build` process in production). `PathSource.Resolve` registers dotnet-kind path services in run mode and subscribes `OnBeforeResourceStarted` on the real `AddProject` builder. Optional catalog field `buildGroup` overrides the key.

**Tech Stack:** C# multi-target `net8.0;net9.0;net10.0`, xunit, Aspire 13.5.2 (`AspireVersion` in `Directory.Build.props`).

**Spec:** [`docs/superpowers/specs/2026-09-28-397-path-build-serialization-design.md`](../specs/2026-09-28-397-path-build-serialization-design.md)

---

## Global Constraints

- **Verify legs.** Every task: `dotnet test -f net10.0` (cheap leg, CLAUDE.md). Before landing: `dotnet test` (full matrix) and `dotnet restore` then `dotnet build -c Release --no-restore -warnaserror` (CI parity; `-warnaserror` decides green from red). CI-only, never claim green locally: `smoketest-*.sh`, the TypeScript export-surface job, packing/prerelease-bounds scan, `aspire-matrix`, `net11-preview`.
- **Gate only the build.** The semaphore is released before the resource's process runs; never held across a run (deadlock with `WaitFor`).
- **The handler never fails a start.** Catch everything except `OperationCanceledException`; log to the resource and proceed.
- **Managed checkouts unchanged.** `"repository"`, `"url"`, `"container"`, `"kubernetes"`, `"disabled"` and non-dotnet kinds subscribe nothing. Publish mode subscribes nothing.
- **Config messages** use the `ServiceSourcesConfigurationException.For(...)` seam with `Name`/`Raw` holes (see `Messages/`); no raw string holes.
- **Sibling PR #404** touches `PathSourceTests.cs`, `docs/sources/path.md`, `docs/sources/repository.md`, `CHANGELOG.md`, `ServiceSourcesBuilderExtensions.cs` (and others). Do not rebase onto it; put new tests in new files where possible; locate edits by text, not line numbers.
- **Code comments:** only the non-obvious WHY, one short sentence; no ticket/PR references in code.
- Test project: `test/Aspire.Hosting.ServiceSources.Tests/` (new tests under `Sources/`, `Config/`, `Catalog/`).

## File Structure

**Created**

| File | Responsibility |
|---|---|
| `src/Aspire.Hosting.ServiceSources/Sources/BuildGroupKey.cs` | Key derivation, namespacing, comparer. |
| `src/Aspire.Hosting.ServiceSources/Sources/PathBuildGate.cs` | Per-builder registry, per-key semaphores, the gated build with never-fail semantics. |
| `src/Aspire.Hosting.ServiceSources/Sources/IBuildRunner.cs`, `ProcessBuildRunner.cs` | Seam and real `dotnet build` runner (argument list, no shell, capped output, tree-kill on cancel). |
| `test/.../Sources/BuildGroupKeyTests.cs`, `PathBuildGateTests.cs`, `ProcessBuildRunnerTests.cs`, `PathBuildGateWiringTests.cs` | Tests below. |

**Modified:** `PathSource.cs`, `Config/ServiceMetadata.cs`, `Config/Catalog/ServiceDefinition.cs`, `Catalog/ServiceDefinitionBuilder.cs`, `Config/ServiceCatalogLoader.cs` (reserved key), `docs/sources/path.md`, `docs/sources/repository.md`, `CHANGELOG.md`.

---

## Task 1: Aspire behaviour spike (throwaway; findings recorded, nothing shipped)

**Test first:** a scratch harness OUTSIDE the repo tree (scratchpad dir) that is itself the test: it must FAIL its assertions if the hypotheses below are false. Do not commit the harness.

Hypotheses (spec finding 7 and Testing "Spike"):
- (a) `BeforeResourceStartedEvent` (Aspire 13.5.2) holds that resource's launch until the handler task completes, is raised per resource concurrently for two resources, and is raised again on restart.
- (b) Two real projects sharing a `ProjectReference` (created in scratch), started through a gated pair (a semaphore around `dotnet build`), never produce `MSB4018`/`CS2012`; an ungated control reproduces the collision (run the control several times on a cold tree; report the rate).
- (c) After the gated build, whether the follow-up `dotnet run`'s own build ever collides, including with a multi-targeted shared project; if it does, whether `--no-build` (or single-TFM `-f`) removes it.
- (d) `dotnet build "<proj>"` versus `dotnet run --project` configuration parity (`--configuration` from the AppHost option).

- [ ] **Step 1:** Write the harness with assertions for (a)-(d); run; record outcomes.
- [ ] **Step 2:** Append a `## Spike findings (executed)` section to this plan (versions, measured results, decision on `--no-build`/`-f`) and to the notes file. Commit only the plan edit.
- [ ] **Step 3 (decision gate):** If (a) fails, STOP and return `open_questions` (design premise broken). If (c) shows collisions, Task 5 adds the `--no-build`/TFM handling per the findings (spec Open Question 4).

## Task 2: Group key

**Test first:** `BuildGroupKeyTests`:
- two service dirs under one `.git` root share a key; dirs under different roots differ;
- a `.git` file (worktree/submodule) counts as a root;
- no `.git` but a configured `ServiceSources:RepositoryRoot` applies (reuse `PathSource.ConfinementRootOf`);
- neither: the key is the service dir; two services in one dir share, unrelated dirs differ;
- `buildGroup` beats derivation; `g:x` never equals a `p:` key even when the path text equals `x`;
- trailing separators trimmed; comparer is ordinal-ignore-case on Windows, ordinal elsewhere (branch on `OperatingSystem.IsWindows()`).

- [ ] Run, see fail. [ ] Implement `BuildGroupKey.For(serviceDir, buildGroup, configuredRoot)` reusing `PathSource.ConfinementRootOf` (the local named `repoRoot` in `Resolve` is the service dir, not the repository root). [ ] Green. [ ] Commit.

## Task 3: Gate core (registry, semaphore, never-fail build)

**Test first:** `PathBuildGateTests` with a fake `IBuildRunner` recording enter/exit:
- two same-key members' builds never overlap (max concurrency 1, both complete);
- different keys run concurrently (max concurrency 2);
- a lone member (member count not above 1) calls the runner zero times and takes no lock;
- membership is evaluated at start time (a member registered after the first registration but before its start makes it gated);
- runner returns a non-zero exit code: handler completes, no throw, semaphore released (next waiter proceeds), failure logged to the resource logger;
- runner throws (`Win32Exception`/`InvalidOperationException`): same, no throw;
- cancellation while waiting: `OperationCanceledException` propagates and the semaphore count is unchanged (does not release what it did not acquire);
- cancellation during the build: runner receives the cancel, semaphore released, rethrown;
- release happens before the handler returns, so a later "run" phase never holds the gate (assert ordering with a second waiter).

- [ ] Fail. [ ] Implement `IBuildRunner`, `PathBuildGate.For(builder)`, `Register(key)`, `RunGatedBuildAsync(key, projectFile, configuration, log, ct)`. [ ] Green. [ ] Commit.

## Task 4: Real build runner

**Test first:** `ProcessBuildRunnerTests`: (i) the start info is `dotnet` with an argument list of `build`, the project file, and `--configuration <c>` only when set, no shell, working directory the project's directory; (ii) output above the cap is truncated with a marker and treated as text; (iii) cancelling a long-running child kills the process tree (inject a start-info factory so the test can launch a portable sleeper); (iv) a missing executable surfaces as an exception the gate already handles.

- [ ] Fail. [ ] Implement `ProcessBuildRunner` (cap modelled on `BufferingPrepareOutputSink`). [ ] Green. [ ] Commit.

## Task 5: Wire into `PathSource`

**Test first:** `PathBuildGateWiringTests` (using the builders `PathSourceTests` uses; a new file to avoid #404 conflicts):
- run mode, two `path` dotnet services in one repo: the gate registry reports 2 members with one key, and the real `ProjectResource` has a `BeforeResourceStartedEvent` subscription;
- the subscription is on the `AddProject` builder, not the `ServiceResource` facade;
- `buildGroup` on two services in different repos gives one shared key; an explicit name on one of two same-repo services makes the keys differ;
- non-dotnet kind, `"repository"`, `"url"`, `"container"`, `"kubernetes"`, `"disabled"` register and subscribe nothing;
- publish mode registers and subscribes nothing;
- per the Task 1 decision: grouped projects get `--no-build` in their run arguments, or explicitly do not.

- [ ] Fail. [ ] Implement registration in `PathSource.Resolve` (dotnet branch only, `builder.ExecutionContext.IsRunMode`); the subscription is unconditional and the handler asks the registry at start time. [ ] Green. [ ] Commit.

## Task 6: `buildGroup` catalog field

**Test first:**
- `ServiceCatalogLoaderTests` (yaml): `buildGroup: web` reaches `ServiceDefinition.BuildGroup`; empty, whitespace or padded value throws `ServiceSourcesConfigurationException` naming the service via `Name`;
- ignored (no error, no effect) when the resolved source is not `"path"`;
- a yaml kind named `buildGroup` is refused as a well-known-key collision (follow the existing collision test);
- `ServiceDefinitionBuilderTests`: `WithBuildGroup("x")` sets it; null/empty/whitespace/padded throws; a second call throws like the other `RequireUnset` methods;
- `CatalogExportsTests` expectations for the new public method.

- [ ] Fail. [ ] Add `ServiceMetadata.BuildGroup` (and `ToDefinition`), `ServiceDefinition.BuildGroup`, `ServiceDefinitionBuilder.WithBuildGroup`, reserved-key handling in the loader. [ ] Green. [ ] Commit.
- [ ] Check whether the TypeScript export surface lists the new export; the job is CI-only, name it in the PR body.

## Task 7: Docs and changelog

**Test first:** after the edits, `git grep -n "can collide"` in `docs/sources/repository.md` finds no claim that path services collide unguarded and no claim that managed checkouts cannot race.

- [ ] `docs/sources/path.md`: a "Several path services from one repository" section: the auto rule (git root), the `buildGroup` override, build-only serialization, the IDE/`dotnet watch` limit, and git root versus the prepare lock's service-directory grouping.
- [ ] `docs/sources/repository.md`: rewrite the "Two `path` services in one repository can collide" bullet and the sentence after it (managed checkouts cannot race only when ungrouped; a `WithSharedRepository` group shares one clone and is not gated); one sentence each in "Aspire builds a checkout, on every start" and "Several services from one repository".
- [ ] `CHANGELOG.md` `[Unreleased]`: a **Fixed** entry (path build collision, in released 0.7.0) naming the optional `buildGroup` field and the reserved key; add the `[#397]` link definition only if absent.
- [ ] Commit.

## Task 8: Final verification

- [ ] `dotnet test -f net10.0` green; `dotnet restore` then `dotnet build -c Release --no-restore -warnaserror` green.
- [ ] Full `dotnet test` matrix once, after the final pre-land rebase (CLAUDE.md).
- [ ] Record the CI-only legs as not run locally.
