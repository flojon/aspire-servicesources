# CheckoutsDirectory setting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `ServiceSources:CheckoutsDirectory` places managed checkouts outside the AppHost directory, namespaced per AppHost, with the default layout unchanged.

**Architecture:** A pure, per-builder-cached `CheckoutsLocation` (default or `<root>\<hash10>\checkouts`, plus a lexical `Problem`) replaces the AppHost string in `LocalGitCheckout.ManagedRepoRoot` and its callers. An ownership-checked `OwnedDirectory` receives the `.gitignore`, barrier files and `owner.json` before any clone; the staging sweep, locks, borrowing and refusal wording all work from the location.

**Tech Stack:** C# net10.0, xUnit, `Microsoft.CodeAnalysis.PublicApiAnalyzers` (no public API change expected), MkDocs docs.

**Spec:** `docs/superpowers/specs/2026-11-19-475-checkouts-directory-design.md` (Approved). Section numbers below (3.x) refer to it.

## Global Constraints

- Setting key `ServiceSources:CheckoutsDirectory`; file key `checkoutsDirectory`; env var `ServiceSources__CheckoutsDirectory`. Blank or unset means the default.
- Layout `<root>\<hash10>\checkouts\<name>`; `<hash10>` = first 10 hex of SHA-256 over the AppHost directory, input built as `ManagedGitDirectory.HashOf` does (`GetFullPath`, trailing separator trimmed, upper-cased, UTF-8).
- Value must be absolute and local; relative, UNC, `\\?\`, `\\.\`, the AppHost directory, an ancestor of it, or anything inside `<AppHost>\.servicesources` is a `Problem`. No `~` or `%VAR%` expansion.
- Computing a location is pure string work: no filesystem access, no throw.
- `.gitignore`, barrier files and `owner.json` go in the owned directory, never the configured root. `selection.json`, `worktree-seed.json` and `path`-checkout prepare markers stay under `ToolDirectory.PathIn(appHost)`.
- The tool never adopts or deletes an owned directory it did not create (file, reparse point, no record, other AppHost's record: refuse).
- No public API change: `PublicAPI.*.txt` untouched. `-warnaserror` build must stay green.
- Test classes get traits per CLAUDE.md: filesystem or process use gets `[Trait("IO", "true")]`; real threads `Concurrency`; wall-clock waits `Timing`. Pure tests carry none.
- Code comments: only the non-obvious WHY, under 15 words, no ticket references.
- Out of scope: borrowing from a default-location home, a runtime notice about old checkouts, moving existing checkouts, a builder API.

## Review Focus

- A value with a trailing separator, mixed `/` and `\`, or `..` segments (`C:\ss\..\x`): must normalise before the AppHost and `.servicesources` checks, and hash identically to the clean form.
- A root on a drive that does not exist, or one that is a file: refused at clone time naming the key, never a raw `IOException`, and never from the prefetch.
- Setting present but whitespace only: treated as unset, not as a relative-path `Problem`.
- Two services of one AppHost cloning in parallel into a fresh owned directory: both must pass the ownership check and `owner.json` creation must not race into a refusal.
- A checkout name that collides with `owner.json`, `.gitignore` or a barrier file name: cannot happen, since those live one level above `checkouts\`.

---

### Task 1: The setting and `CheckoutsLocation`

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Git/CheckoutsLocation.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfiguration.cs`, `src/Aspire.Hosting.ServiceSources/Config/DeveloperConfigFileSource.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/CheckoutsLocationTests.cs` (no traits), plus a case beside the existing `repositoryRoot` file-source test in `test/Aspire.Hosting.ServiceSources.Tests/Config/`

**Interfaces:**
- Produces: `internal sealed record CheckoutsLocation(string OwnedDirectory, string CheckoutsRoot, bool IsExternal, string? ConfiguredValue, Raw? Problem)`; `static CheckoutsLocation CheckoutsLocation.Default(string appHostDirectory)`; `static CheckoutsLocation CheckoutsLocation.Resolve(string appHostDirectory, string? configuredValue)`; `static CheckoutsLocation CheckoutsLocation.For(IDistributedApplicationBuilder builder)` (cached in a `ConditionalWeakTable` keyed on the builder, calls `DeveloperConfigFileSource.EnsureRegistered` first, reads `DeveloperConfiguration.CheckoutsDirectoryKey`); `void ThrowIfProblem()` throwing `ServiceSourcesConfigurationException`; `static string HashOf(string appHostDirectory)` (10 hex).
- Produces: `DeveloperConfiguration.CheckoutsDirectoryKey = "ServiceSources:CheckoutsDirectory"`; `DeveloperConfigFileSource.FileCheckoutsDirectoryKey = "checkoutsDirectory"` (internal const) in `ReRootedValues`.

- [ ] **Step 1: Write the failing tests** in `CheckoutsLocationTests`: `Unset_gives_default_layout` and `Whitespace_is_unset` (`OwnedDirectory == <apphost>\.servicesources`, `IsExternal` false, `Problem` null); `Absolute_value_gives_hashed_layout` (`CheckoutsRoot == C:\ss\<HashOf(apphost)>\checkouts`, hash length 10); `Hash_is_case_and_trailing_separator_insensitive_and_differs_per_apphost`; `Trailing_separator_and_dotdot_segments_normalise`; one `[Theory]` `Refused_values_give_a_problem` over: relative `ss`, `\\server\share\x`, `\\?\C:\ss`, `\\.\C:\ss`, the AppHost directory itself, its parent, `<apphost>\.servicesources\x`, each asserting `Problem` text contains the key, the value and `ThrowIfProblem` throws; `A_root_elsewhere_inside_the_apphost_repository_is_allowed`; `A_filesystem_root_is_allowed`; `Resolve_creates_nothing` (temp-free: use a path that does not exist and assert `Directory.Exists` false). Add a file-source test that `{"checkoutsDirectory": "C:\\ss"}` surfaces as configuration key `ServiceSources:CheckoutsDirectory`. Use `OperatingSystem.IsWindows()`-aware paths (POSIX `/ss` forms on Linux) so the suite also runs on ubuntu CI.
- [ ] **Step 2: Run to verify failure.** `dotnet test test/Aspire.Hosting.ServiceSources.Tests --filter "FullyQualifiedName~CheckoutsLocationTests|FullyQualifiedName~DeveloperConfigFileSource"` expects a compile failure (type missing).
- [ ] **Step 3: Implement** the type and keys per 3.1-3.2. Reuse `SeparateGitDirectories.IsLocalRootedPath`'s UNC/device test by extracting its lexical half to an internal helper (the mounted-drive probe stays where it is). Messages name the key, the value and the resolved path.
- [ ] **Step 4: Run the tests; expect PASS.** Then `dotnet build -c Release -warnaserror` expecting 0 warnings.
- [ ] **Step 5: Commit** `Add the CheckoutsDirectory setting and CheckoutsLocation (#475)`.

### Task 2: `ToolDirectory.EnsureAt` and the ownership check

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/ToolDirectory.cs`, `src/Aspire.Hosting.ServiceSources/Git/CheckoutsLocation.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/CheckoutsOwnershipTests.cs` (`IO`, plus `Concurrency` on the parallel case)

**Interfaces:**
- Consumes: Task 1 `CheckoutsLocation`.
- Produces: `ToolDirectory.EnsureAt(string directory)` (creates directory and `.gitignore`, returns the path; `Ensure(appHost)` delegates to it); `CheckoutsLocation.EnsureOwned(string appHostDirectory)` returning the owned directory after: default location, the existing `ToolDirectory.Ensure` behaviour; external, the 3.3 rules (absent: create and write `owner.json` naming the AppHost directory; this AppHost's record: use; file, reparse point, no record, other AppHost's record: throw `ServiceSourcesConfigurationException` naming the directory). Also creates the root if absent, and turns a failure to do so into a configuration error naming the key. Calls `ThrowIfProblem()` first.

- [ ] **Step 1: Write the failing tests** (names are the contract): `Absent_owned_directory_is_created_with_a_record`; `Directory_with_this_apphosts_record_is_used`; `File_at_the_hash_path_is_refused_and_untouched`; `Reparse_point_is_refused` (skip where symlinks cannot be created); `Directory_without_a_record_is_refused_and_nothing_written_into_it`; `Directory_with_another_apphosts_record_is_refused`; `Root_that_is_a_file_gives_a_configuration_error_naming_the_key`; `Gitignore_and_barrier_files_land_in_the_owned_directory_not_the_root`; `Parallel_first_use_of_one_apphost_never_refuses` (many tasks calling `EnsureOwned`); `Default_location_still_writes_gitignore_under_servicesources`.
- [ ] **Step 2: Run to verify failure** (`--filter FullyQualifiedName~CheckoutsOwnershipTests`).
- [ ] **Step 3: Implement.** Create the owned directory then write `owner.json` with `FileMode.CreateNew`; a lost creation race re-reads the record and accepts only this AppHost's. Barrier via `CheckoutBuildBarrier.Ensure(owned)` after the check.
- [ ] **Step 4: Run; expect PASS.**
- [ ] **Step 5: Commit** `Check ownership of the namespaced checkouts directory (#475)`.

### Task 3: Thread the location through `ManagedRepoRoot` and its callers

**Files:**
- Modify: `Git/LocalGitCheckout.cs` (`ManagedRepoRoot`, `IsColdManagedCheckout`, `PrepareRepoRoot`, `ResolveRepoRoot`, `EnsureToolDirectory`, `CloneIntoPlace` comment), `Sources/DeferredCheckout.cs` (four sites plus `BlockedChangedCommand` and its caller `Sources/LocalProjectSource.cs`), `Sources/LocalCheckoutPrefetch.cs` (two `PrepareRepoRoot` calls and the cold filter), `Sources/PathSource.cs` (`ResolveRepoRoot` caller)
- Test: existing 43 call sites in `test/**` (migrate mechanically: pass `CheckoutsLocation.Default(appHostDirectory)`), new `test/Aspire.Hosting.ServiceSources.Tests/Git/LocalGitCheckoutExternalTests.cs` (`IO`)

**Interfaces:**
- Consumes: Tasks 1-2.
- Produces: `ManagedRepoRoot(CheckoutsLocation location, string checkoutName)` (containment check unchanged, returns `Path.Combine(location.CheckoutsRoot, checkoutName)`); `IsColdManagedCheckout(CheckoutsLocation, string checkoutName, ServiceDeveloperConfig)`; `PrepareRepoRoot(..., string appHostDirectory, CheckoutsLocation location, IGitClient, ...)` (the new parameter directly after `appHostDirectory`); `ResolveRepoRoot` likewise; `BlockedChangedCommand(..., string appHostDirectory, CheckoutsLocation location, bool canDefer)`. Callers with a builder pass `CheckoutsLocation.For(builder)`.

- [ ] **Step 1: Write the failing tests** in `LocalGitCheckoutExternalTests`: `Managed_checkout_lands_under_root_hash_checkouts_name` (real clone from a local bare repo using the existing fake/real git fixture the sibling tests use); `Default_location_path_is_unchanged`; `Dotdot_checkout_name_is_still_refused_under_an_external_location`; `Problem_location_throws_from_PrepareRepoRoot_before_any_write`; `Prefetch_with_a_problem_location_does_not_throw_and_AddService_reports_it` (in `LocalCheckoutPrefetchTests`); `Path_checkout_markers_and_selection_json_stay_under_the_apphost` (IO). Migrate the 43 existing call sites first and confirm they pass unchanged before the new tests.
- [ ] **Step 2: Run to verify the new tests fail** (`FullyQualifiedName~LocalGitCheckoutExternalTests|FullyQualifiedName~LocalCheckoutPrefetchTests`).
- [ ] **Step 3: Implement.** `EnsureToolDirectory` becomes `location.EnsureOwned(appHostDirectory)` then `CheckoutBuildBarrier` as in Task 2 (single place). `Problem` is thrown in `PrepareRepoRoot`/deferred `Register` only; prefetch's candidate filter drops a location with a `Problem` instead of calling `ManagedRepoRoot`. Update the "everything under `.servicesources`" comment near `CloneIntoPlace` to "under an owned directory".
- [ ] **Step 4: Run the full non-IO suite plus these IO classes: `dotnet test -c Release --filter "IO!=true&Timing!=true"` and the new class. Expect PASS.**
- [ ] **Step 5: Commit** `Place managed checkouts under the configured CheckoutsDirectory (#475)`.

### Task 4: Staging sweep skips reparse points; two AppHosts, one root

**Files:**
- Modify: `Git/ScratchDirectory.cs` (`SweepAbandoned`, `SweepOne`)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/ScratchDirectoryTests.cs` (existing, `IO`), new `test/Aspire.Hosting.ServiceSources.Tests/Git/SharedCheckoutsRootTests.cs` (`IO`, `Concurrency`)

**Interfaces:**
- Consumes: Task 3 external clone path.

- [ ] **Step 1: Write the failing tests:** `SweepAbandoned_skips_a_reparse_point_named_incoming` (skip where links cannot be created; target survives); `Two_apphosts_one_root_never_see_each_others_staging_or_locks` (create `.incoming-*` and lock under A's `checkouts`, sweep from B, A's remain, and vice versa); `Two_apphosts_clone_in_parallel_into_one_root` (both succeed, distinct directories); `External_checkout_is_seen_as_live_by_SweepOrphans`.
- [ ] **Step 2: Run to verify failure** (`FullyQualifiedName~SharedCheckoutsRootTests|FullyQualifiedName~ScratchDirectoryTests`).
- [ ] **Step 3: Implement** the reparse skip in `SweepOne` and the orphan-lock loop, mirroring `SeparateGitDirectories.SweepOne`. No other sweep change: scoping comes from receiving `<hash>\checkouts`.
- [ ] **Step 4: Run; expect PASS.**
- [ ] **Step 5: Commit** `Skip links in the staging sweep and cover shared roots (#475)`.

### Task 5: Path-length refusals name the setting

**Files:**
- Modify: `Git/CloneLayout.cs` (`Decide` takes `CheckoutsLocation location`), `Git/GitPathLimit.cs` (`GitRootRefusal` takes the location; add `MaxCheckoutsRoot(int workingTreeOrInPlaceLimit)` = limit minus `\.incoming-<id>` (19)), `Git/LocalGitCheckout.cs` (`NoteExistingCheckout`, `CloneIntoPlace` call)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/GitPathLimitTests.cs` and a new `CloneLayoutMessageTests.cs` (no traits)

**Interfaces:**
- Consumes: `CheckoutsLocation.IsExternal`.
- Produces: `CloneLayout.Decide(Raw label, string checkoutPath, string stagingPath, CloneLayoutOptions options, CheckoutsLocation location)`.

- [ ] **Step 1: Write the failing tests:** `Default_location_refusals_suggest_CheckoutsDirectory_and_its_env_var_and_C_ss` for all three refusals (working-tree limit, git root, existing in-place checkout); `External_location_refusals_say_shorten_CheckoutsDirectory_and_do_not_say_move_the_AppHost` and quote the owned-directory budget; `NoteExistingCheckout_names_the_actual_checkouts_directory`; `GitPathLimitTests` figures 170/190 still pass untouched; `Long_external_root_decides_separate_or_refused_by_its_real_length` (use `CloneLayoutOptions(IsWindows: true)`).
- [ ] **Step 2: Run to verify failure.**
- [ ] **Step 3: Implement** wording per 3.5. Default-location text gains "or set `ServiceSources:CheckoutsDirectory` (environment variable `ServiceSources__CheckoutsDirectory`) to a short directory such as `C:\ss`".
- [ ] **Step 4: Run `FullyQualifiedName~GitPathLimit|FullyQualifiedName~CloneLayout`; expect PASS.**
- [ ] **Step 5: Commit** `Point path-too-long refusals at CheckoutsDirectory (#475)`.

### Task 6: Borrowing objects under an external root

**Files:**
- Modify: `Git/LocalGitCheckout.cs` (`ReferenceCheckout`, `TryReferenceCheckout`)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Worktrees/CheckoutSeedingTests.cs` (existing `ReferenceCheckout` tests, `IO`)

**Interfaces:**
- Consumes: Tasks 1, 3. Produces: `ReferenceCheckout(string? homeAppHostDirectory, CheckoutsLocation thisRun, ServiceDefinition, IGitClient, string? gitRoot = null)` computing `Resolve(home, thisRun.ConfiguredValue)` and joining the checkout name.

- [ ] **Step 1: Write the failing tests:** `Home_under_the_same_root_is_found`; `Origin_mismatch_is_still_refused`; `Home_at_a_different_root_or_default_falls_back_to_a_plain_clone_without_error`; `Spelling_difference_misses_and_clones_plainly`.
- [ ] **Step 2: Run to verify failure** (`FullyQualifiedName~CheckoutSeedingTests`).
- [ ] **Step 3: Implement.** Candidate is `<root>\<HashOf(home)>\checkouts\<name>` under this run's configured value; a `Problem` or a null home returns null.
- [ ] **Step 4: Run; expect PASS.**
- [ ] **Step 5: Commit** `Borrow objects from a home worktree under the same CheckoutsDirectory (#475)`.

### Task 7: Docs, XML docs, CHANGELOG, smoke test

**Files:**
- Modify: `docs/sources/repository.md`, `docs/guides/configuration.md`, `docs/troubleshooting.md`, `CHANGELOG.md`, `scripts/smoketest-config-layers.sh`; XML docs in `ServiceSourcesBuilderExtensions.cs`, `Git/CheckoutBuildBarrier.cs`, `Git/LocalGitCheckout.cs` that hard-code `.servicesources/checkouts/<serviceName>` under the AppHost.
- Test: `scripts/smoketest-config-layers.sh` gains a case asserting the key is read from environment and from `servicesources.local.json` and that a checkout lands under `<root>/<hash>/checkouts`; `mkdocs build --strict` if available.

- [ ] **Step 1: Add the smoke-test case first** and run `bash scripts/smoketest-config-layers.sh`; expect that case to pass only now that Tasks 1-6 exist (if it already runs the built package, expect it to fail until a rebuild).
- [ ] **Step 2: Write docs.** `repository.md`: the setting, default, layout, hash namespace and its limits (junction/`subst`/8.3, case), the ownership refusal and how to resolve it, "old checkouts are not moved or used; delete or move them", do not point it at a directory other users can write to, `path:` does not reach the checkouts. `troubleshooting.md`: an entry naming the MSBuild "Path ... too long" failure inside a checkout (about 145-character AppHosts) and the fix. `configuration.md`: the key, env var, file key. XML docs updated.
- [ ] **Step 3: CHANGELOG** entry under `### Added` in the repo's style, with a `[#475]` link reference at the bottom beside the others (the file ends with link references; match their order).
- [ ] **Step 4: Verify** `dotnet build -c Release -warnaserror` clean; docs build if available.
- [ ] **Step 5: Commit** `Document the CheckoutsDirectory setting (#475)`.

### Task 8: Whole-change verification

- [ ] **Step 1:** `dotnet restore`, `dotnet build -c Release --no-restore -warnaserror`, `dotnet test -c Release --no-build` (all traits); expect no failures.
- [ ] **Step 2:** `dotnet pack src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj -c Release --no-build -o ./artifacts/package`; confirm `PublicAPI.*.txt` unchanged (`git diff --stat -- '**/PublicAPI*.txt'` empty).
- [ ] **Step 3 (Windows dev box, recorded in the PR, not CI):** AppHost directory of about 145 characters, `ServiceSources__CheckoutsDirectory=C:\ss`, a managed service: `dotnet run` of the service gets past where the same AppHost without the setting fails with MSBuild "Path ... too long". Note legs that cannot run locally (container smoke test, TypeScript export, aspire-matrix) as CI-only.

## Self-review

Spec coverage: 3.1 and 3.2 (Task 1), 3.3 and 3.4 (Tasks 2-3), 3.5 (Task 5), 3.6 (Task 4), 3.7 (Task 6; seeding needs no change and is pinned by Task 4's live-checkout test and Task 6), 3.8 and 3.9 (no code; docs in Task 7, marker location pinned in Task 3), section 4 (Tasks 1-2, 4), section 6's ticket case (Task 8). Names are consistent: `CheckoutsLocation.Default/Resolve/For/EnsureOwned/ThrowIfProblem/HashOf`. Task 3 is the largest because the signature change cannot compile in halves.
