# Short git directory for managed clones on Windows (#479) Implementation Plan

> **Threshold correction (round 3).** This plan was written against a pack-path model (`root + 85 <= 259`, in place up to a root of 174, AppHost about 129 deep). That model was wrong: git refuses any command once the git directory passes 220 characters (`'$GIT_DIR' too big`, `PATH_MAX - 40`, measured on git 2.52.0.windows.1 only), so the in-place limit is a root of 215 (AppHost 170 deep), and the separate git directory ceiling stays 235 (AppHost 190 deep). The git root budget is 190, not 149. `PackPathLimit` became `GitPathLimit` with `MaxGitDirLength = 220`, `MaxInPlaceRootLength = 215`, `MaxWorkingTreeRootLength = 235`, `MaxGitRootLength = 190`. Where the steps below quote 259, 85, 174/175, 149 or 80, read the corrected values; the spec is authoritative.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** On Windows, clone a managed checkout with `--separate-git-dir` into a short per-user directory when the in-place git directory would be too long for git (`'$GIT_DIR' too big`), so AppHost directories up to 190 characters work instead of being refused at about 129 (the old, wrong model).

**Architecture:** A pure decision (`CloneLayout`) picks in-place or separate. A separate git directory lives at `%LOCALAPPDATA%\ServiceSources\git\<16 hex>-<8 hex>.git`, is paired with the clone's `ScratchDirectory`, carries an ownership record, and is found again through one resolver (`ManagedGitDirectory.Resolve`) that every `.git`-is-a-directory site calls. An orphan sweep deletes unreferenced git directories after 14 days.

**Tech Stack:** C# on net10.0, xUnit, real `git` in tests (`TestRepository`), `Microsoft.CodeAnalysis.PublicApiAnalyzers`.

**Spec:** `docs/superpowers/specs/2026-11-14-479-short-git-dir-design.md` (Approved; both open questions resolved: 14-day auto-delete, notice-only for existing deep checkouts).

## Global Constraints

- Everything new is `internal`; `PublicAPI.Unshipped.txt` must not change (spec section 7). Task 8 verifies it.
- Build with `-warnaserror`; tests target `net10.0` only.
- Every new test class carries `[Trait("IO", "true")]`; a class that races two clones, a sweep and `Resolve` also carries `[Trait("Concurrency", "true")]`. No new class uses `Timing`.
- Separate git dir is Windows only and only when `215 < L <= 235`, `L = max(final, staging)` path length (D1). Constants (corrected): `MaxGitDirLength = 220`, `MaxInPlaceRootLength = 215`, `MaxWorkingTreeRootLength = 235`; superseded pack-model values: `MaxPathLength = 259`, in-place suffix 85, in-git-dir suffix `\objects\pack\.tmp-<10>-pack-<40>.pack` = 80.
- Git root: `Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "ServiceSources", "git")`, not configurable, at most 190 characters; empty `LocalApplicationData` means unusable (D2).
- Git dir name matches `^[0-9a-f]{16}-[0-9a-f]{8}\.git$`; hash = first 16 hex of SHA-256 over the upper-invariant full final checkout path with trailing separators trimmed; id = the scratch's 8-hex id (D2).
- Owner record `servicesources-owner.json` = `{ "checkout", "createdUtc", "lastUsedUtc" }`, always written via temp file then move (D4).
- Retention 14 days; sweep never deletes from a path read out of a record, never follows reparse points, swallows `IOException` and `UnauthorizedAccessException` (D4, section 4).
- `--separate-git-dir=<path>` is one argument, placed after `clone` (D3).
- Code comments only for the non-obvious WHY, one short sentence.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

## Review Focus

Inputs the spec implies but no single task's happy path covers; each has a pinning test in the task named.

- A `.git` file whose `gitdir:` uses `..` segments, backslashes, a different case, or points at a sibling of the git root: refused, never run through git (Task 2).
- Two AppHosts cloning one final path at once: distinct git dir names, the loser's git dir removed, the winner's kept (Task 3, Task 6).
- A crash after the move but before disposal, and one between record write and move: the first leaves a live dir the sweep keeps; the second leaves an unreferenced dir with a fresh record, kept until the 14-day window passes and then collected (Task 5).
- A record that is malformed, empty, names another drive or a UNC path: the sweep skips or ages it by directory write time, never deletes by record content (Task 5).
- A checkout moved or renamed with its AppHost: refused with the "moved or renamed" reason, its git dir collected after the window (Task 2, Task 5 `Sweep_RecordNamesMovedAwayCheckout_AgesOutAfterRetention`).

---

## File Structure

- Create `src/Aspire.Hosting.ServiceSources/Git/CloneLayout.cs`: the D1 decision and the reworded refusal.
- Create `src/Aspire.Hosting.ServiceSources/Git/ManagedGitDirectory.cs`: naming, `OwnerRecord`, `Resolve`, record write/refresh.
- Create `src/Aspire.Hosting.ServiceSources/Git/SeparateGitDirectories.cs`: default root, `SweepOrphans`.
- Modify `Git/ScratchDirectory.cs`, `Git/IGitClient.cs`, `Git/GitCliClient.cs`, `Git/LocalGitCheckout.cs`, `Git/GitPathLimit.cs`, `Prepare/PrepareMarker.cs`, `Prepare/CheckoutPreparation.cs` (marker call site).
- Tests in `test/Aspire.Hosting.ServiceSources.Tests/Git/`, one new file per new source file, plus the touched existing ones; one new `WindowsFactAttribute.cs` in the test project root.

---

### Task 1: CloneLayout decision and reworded refusal

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Git/CloneLayout.cs`
- Modify: `src/Aspire.Hosting.ServiceSources/Git/GitPathLimit.cs` (add `LongestPackFileInGitDir`, `MaxWorkingTreeRootLength`; `EnsureFits` stays until Task 6)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/CloneLayoutTests.cs` (no trait: `CloneLayout` does no I/O, per CLAUDE.md "no trait = pure unit test")

**Interfaces:**
- Produces: `internal enum CloneLayoutKind { InPlace, Separate }`; `internal sealed record CloneLayoutOptions(string? GitRoot, bool? IsWindows = null, bool ForceSeparate = false)` with `static CloneLayoutOptions Default` (its `GitRoot` is `SeparateGitDirectories.DefaultRoot()`, a pure string computation that touches no disk; Task 1 creates `Git/SeparateGitDirectories.cs` containing only that method, Task 5 adds the rest). `ForceSeparate` only replaces the Windows gate: the 235 ceiling and the root-usable rule still apply, so a Linux test of the ceiling passes `IsWindows: true`; `internal static class CloneLayout { static CloneLayoutKind Decide(Raw label, string checkoutPath, string stagingPath, CloneLayoutOptions options) }`; `GitPathLimit.LongestPackFileInGitDir` (80 chars), `GitPathLimit.MaxWorkingTreeRootLength = 235`, `GitPathLimit.MaxGitRootLength = 190`.

- [ ] **Step 1: Write failing tests** in `CloneLayoutTests`: `Windows_AtInPlaceLimit_IsInPlace` (L=215), `Windows_OneOverInPlaceLimit_IsSeparate` (L=216), `Windows_AtCeiling_IsSeparate` (L=235), `Windows_OneOverCeiling_Throws` (message contains the length, `at least 1 character shorter`, `Move the AppHost`, and not `.tmp-<pid>-pack`), `Windows_UsesTheLongerOfFinalAndStaging`, `Windows_GitRootNull_OverInPlaceLimit_ThrowsWithGitDirNumbers` (message names 220 and the `'$GIT_DIR' too big` wording), `Windows_GitRootOver190_IsUnusable`, `NotWindows_IsInPlaceWhateverTheLength`, `ForceSeparate_BeyondCeiling_StillThrows`, `ForceSeparate_OnNonWindows_IsSeparate`, `LongestPackFileInGitDir_Is80Characters`. Rewrite nothing in `GitPathLimitTests` yet.
- [ ] **Step 2: Run** `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~CloneLayoutTests"`. Expected: build FAIL (types missing).
- [ ] **Step 3: Implement** the types above. `Decide` returns `InPlace` unless Windows (or `ForceSeparate`); in the table's middle row it requires a usable root else throws the pack-path message (today's wording and numbers); beyond 235 it throws the ceiling message naming `L`, the 235 limit and `L - 235` excess. The 235 constant carries the section 2 measurement as its comment.
- [ ] **Step 4: Run** the filter again. Expected: PASS.
- [ ] **Step 5: Commit** `git add -A && git commit -m "Decide in-place or separate clone layout from path lengths (#479)"` (plus the Co-Authored-By trailer).

### Task 2: Naming, owner record and `ManagedGitDirectory.Resolve`

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Git/ManagedGitDirectory.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/ManagedGitDirectoryTests.cs` (`IO`)

**Interfaces:**
- Produces: `internal sealed record OwnerRecord(string Checkout, DateTimeOffset CreatedUtc, DateTimeOffset LastUsedUtc)`; `internal static class ManagedGitDirectory` with `const string OwnerFileName = "servicesources-owner.json"`, `static readonly Regex NamePattern`, `string NameFor(string finalCheckoutPath, string id)`, `string HashOf(string finalCheckoutPath)`, `void WriteOwner(string gitDir, OwnerRecord record)` (temp file then move), `OwnerRecord? ReadOwner(string gitDir)` (null on missing or malformed), and `ResolveResult Resolve(string repoRoot, string? gitRoot)` (takes the raw root and applies no length limit; a null or empty root rejects a `.git` file with a refusal naming the cause, while an in-place `.git` directory still resolves) where `ResolveResult` is `{ string? GitDirectory; string? Refusal }` (`GitDirectory` set for an in-place `.git` directory or an accepted `.git` file; `Refusal` carries the specific D6 reason for a rejected file; both null when there is no `.git` entry).

- [ ] **Step 1: Write failing tests**: `NameFor_IsHashDashIdDotGit_AndMatchesPattern`; `HashOf_IgnoresCaseAndTrailingSeparator`; `Resolve_InPlaceGitDirectory_ReturnsIt`; `Resolve_NoGitEntry_ReturnsNeither`; `Resolve_ValidGitFile_ReturnsTheGitDirectory` (builds a real record-bearing directory under a temp git root, writes `gitdir: <forward-slash path>`); one test per reject, each asserting `GitDirectory is null` and a distinctive `Refusal` fragment: `Resolve_FileOver4KiB`, `_TwoLines`, `_NotGitdirPrefix`, `_TargetWithDotDot`, `_TargetNotDirectChildOfRoot`, `_NameNotMatchingPattern`, `_TargetMissing` (git dir gone), `_RecordMissing`, `_RecordMalformed`, `_RecordNamesAnotherCheckout` (message says moved or renamed, delete and re-run), `_TargetOutsideThisMachinesRoot` (message names both roots), `_GitRootIsReparsePoint`, `_TargetIsReparsePoint`, `_GitRootAncestorIsReparsePoint` (create directory symlinks; skip when creation throws `UnauthorizedAccessException`; the ancestor walk goes to the filesystem root for an injected root and stops at `LocalApplicationData` when the root lies under it), `Resolve_GitRootOver149_StillResolvesAnExistingCheckout`, `Resolve_NullGitRoot_RejectsGitFileNamingTheCause_ButAcceptsGitDirectory`, `_BackslashesAndOtherCaseAccepted_OnWindowsOnly` (use `OperatingSystem.IsWindows()` guard inside the test, assertion branch per platform).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ManagedGitDirectoryTests"`. Expected: build FAIL.
- [ ] **Step 3: Implement** per D2 and D6 rules 1-4. Comparison is `OrdinalIgnoreCase` on Windows, `Ordinal` elsewhere. Records are read with `System.Text.Json` into `OwnerRecord`, like `PrepareMarker`.
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** "Resolve a managed checkout's separate git directory (#479)".

### Task 3: ScratchDirectory pairs a git directory

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/Git/ScratchDirectory.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/ScratchDirectoryTests.cs` (existing, already `IO`)

**Interfaces:**
- Consumes: `ManagedGitDirectory.NameFor` (Task 2).
- Produces: `static ScratchDirectory Create(string checkoutsRoot, Func<string>? nextId = null)` unchanged, plus instance `void PairGitDirectory(SeparateGit separate)`, called by `CloneIntoPlace` after `CloneLayout.Decide` has measured `scratch.Path` (the decision needs the staging length, so the scratch exists first); `internal sealed record SeparateGit(string GitRoot, string FinalCheckoutPath)`; property `string? GitDirectory`; `void AdoptGitDirectory()` (Dispose keeps it); `static void SweepAbandoned(string checkoutsRoot, IGitProgressSink? log = null, string? gitRoot = null)`.

- [ ] **Step 1: Write failing tests**: `Pair_NamesGitDirFromHashAndId`; `Pair_HoldsGitRootLockUntilDispose` (second `FileStream` open with no sharing throws); `Dispose_RemovesGitDirectoryAndBothLocks`; `Dispose_AfterAdopt_KeepsGitDirectoryRemovesItsLock`; `SweepAbandoned_RemovesPairedGitDirectoryAndLock_WhenStagingLockIsFree`; `SweepAbandoned_KeepsGitDirectory_WhenOnlyOrphanStagingLockExists` (live case, D4.1); `SweepAbandoned_LeavesPairedGitDirectory_WhenStagingLockIsHeld`; `Create_NewScratchAfterDispose_HasNewIdAndGitDirectoryName`; `Pair_GitLockCollision_ReleasesStagingLockAndRetriesWithNewId`. Put in a new class `ScratchDirectoryPairedConcurrencyTests` with class-level `[Trait("IO","true")]` and `[Trait("Concurrency","true")]`: `TwoCreates_SameFinalPath_GetDistinctGitDirectories`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~ScratchDirectory"`. Expected: FAIL.
- [ ] **Step 3: Implement.** The git-root lock file is `<name>.git.lock`, created `CreateNew`/`FileShare.None` right after the staging lock; the git root directory is created on demand. `SweepAbandoned` removes `*-<id>.git` and `.lock` only inside the branch that removes an existing `.incoming-<id>`, via `DeleteTree`, only for names matching `NamePattern`.
- [ ] **Step 4: Run** the filter. Expected: PASS, existing `ScratchDirectoryTests` still pass.
- [ ] **Step 5: Commit** "Pair a staged clone with its separate git directory (#479)".

### Task 4: Git clients take `separateGitDir`

**Files:**
- Modify: `Git/IGitClient.cs`, `Git/GitCliClient.cs`; every test double implementing `IGitClient.Clone` (found by `grep -rn "void Clone(" test`: 14 files, mechanical signature update, doubles ignore the value)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/GitCliClientTests.cs`, `GitCliCloneWithReferenceTests.cs` (existing classes)

**Interfaces:**
- Produces: `void Clone(string repositoryUrl, string destinationPath, IGitProgressSink? progress = null, string? separateGitDir = null)` and `void CloneWithReference(string repositoryUrl, string destinationPath, string referenceRepository, IGitProgressSink? progress = null, string? separateGitDir = null)`.

- [ ] **Step 1: Write failing tests** against a local `TestRepository` origin: `Clone_WithSeparateGitDir_WritesGitFileAndKeepsObjectsInGitDir` (asserts `<dest>\.git` is a file whose first line is `gitdir: ` plus the target, `<gitDir>\objects` exists, `git -C <dest> status` succeeds, `core.longpaths` persisted in `<gitDir>\config` on Windows only); `Clone_WithSeparateGitDir_ThenMoveWorkingTree_StatusStillWorks` (re-checks spec section 2 on this platform); `CloneWithReference_WithSeparateGitDir_Dissociates`; `Clone_SeparateGitDir_IsOneArgumentAfterCloneAndBeforeDoubleDash` (asserts the git argument vector through the existing command-recording seam in `GitCliClientTests`; if none exists, clone with a relative `-x` path and assert it succeeds).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GitCliClientTests|FullyQualifiedName~GitCliCloneWithReferenceTests"`. Expected: build FAIL.
- [ ] **Step 3: Implement**: add `$"--separate-git-dir={separateGitDir}"` after `clone` and the platform options when non-null; update the 14 doubles' signatures.
- [ ] **Step 4: Run** the same filter, then `dotnet build -c Release -warnaserror` for the whole solution. Expected: PASS and 0 warnings.
- [ ] **Step 5: Commit** "Let the git client clone with a separate git directory (#479)".

### Task 5: Orphan sweep and `lastUsedUtc` refresh

**Files:**
- Create: `src/Aspire.Hosting.ServiceSources/Git/SeparateGitDirectories.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/SeparateGitDirectoriesTests.cs` (class-level `IO`); the held-lock test lives in `SeparateGitDirectoriesLockTests.cs` (`IO` + `Concurrency`, lock held from a second task)

**Interfaces:**
- Consumes: `ManagedGitDirectory.Resolve/ReadOwner/WriteOwner`, `NamePattern` (Task 2).
- Produces: `internal static class SeparateGitDirectories` with `static string DefaultRoot()` (moves the helper from Task 1), `static readonly TimeSpan Retention = TimeSpan.FromDays(14)`, `static void SweepOrphans(string gitRoot, DateTimeOffset now, IGitProgressSink? log = null)`, `static void RefreshLastUsed(string gitDir, DateTimeOffset now)` (rewrites only when `lastUsedUtc` is over a day old).

- [ ] **Step 1: Write failing tests**, one per D4 rule 2 branch: `Sweep_LiveGitDirectory_IsKept` (live only when `Resolve(record.Checkout).GitDirectory` equals this enumerated child, case-insensitive on Windows; record old), `Sweep_RecordedCheckoutNowHasAnInPlaceGitDir_IsNotLive`, `Sweep_RecordNamesMovedAwayCheckout_AgesOutAfterRetention`, `Sweep_RecordWrittenButNeverMoved_IsKeptUntilRetention_ThenCollected`, `Sweep_HeldLock_IsKept` (in `SeparateGitDirectoriesLockTests`), `Sweep_StaleLock_RemovedWithItsDirectory`, `Sweep_RecordedDriveRootMissing_IsKept` (Windows-only assertion; a rooted path on a nonexistent drive letter, guarded by `OperatingSystem.IsWindows()`), `Sweep_RecordedPathUncOrRelative_IsKept_AndNeverProbed`, `Sweep_ParsableRecordWithinRetention_IsKept`, `Sweep_ParsableRecordOlderThanRetention_IsDeleted_AndLogged`, `Sweep_NoRecord_UsesDirectoryWriteTime` (both sides of the window), `Sweep_MalformedRecord_UsesDirectoryWriteTime`, `Sweep_NamedNotMatchingPattern_IsKept`, `Sweep_ReparsePointChild_IsKept_TargetUntouched` (skip if symlink creation is denied), `Sweep_ReadOnlyPackFiles_StillDeleted`, `Sweep_NeverDeletesAPathNamedInTheRecord` (record names a sibling temp dir; it survives), `Sweep_IOExceptionOnOneChild_ContinuesWithTheRest` (hold a file open in one child on Windows; elsewhere assert the no-throw path with a missing root), `RefreshLastUsed_RewritesOnlyWhenOverADayOld`, `RefreshLastUsed_LeavesNoTempFileBehind`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~SeparateGitDirectoriesTests"`. Expected: build FAIL.
- [ ] **Step 3: Implement** per D4. Where spec section 4 once said a malformed record is skipped and D4.2 says it ages by directory write time and is then deleted, D4.2 wins (D6 can never adopt such a directory); the spec's section 4 row is corrected in the same commit. Deletion goes through `ScratchDirectory.DeleteTree` on the enumerated child only. Log one line per deletion, stating the path and "unreferenced for more than 14 days".
- [ ] **Step 4: Run** the filter. Expected: PASS.
- [ ] **Step 5: Commit** "Sweep unreferenced separate git directories after 14 days (#479)".

### Task 6: Clone wiring in `PrepareRepoRoot` / `CloneIntoPlace`

**Files:**
- Modify: `Git/LocalGitCheckout.cs` (`PrepareRepoRoot`, `CloneIntoPlace`, `ResolveRepoRoot` pass-through), `Git/GitPathLimit.cs` (delete `EnsureFits`), `Sources/LocalCheckoutPrefetch.cs` only if a new optional parameter must be threaded (default null keeps callers unchanged)
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Git/LocalGitCheckoutSeparateTests.cs` (new, `IO`, `Concurrency` on the race class `LocalGitCheckoutSeparateRaceTests`); `GitPathLimitTests.cs`: the old `EnsureFits` tests are deleted (Task 1's `CloneLayoutTests` already hold their equivalents) and the constants test is updated

**Interfaces:**
- Consumes: `CloneLayout.Decide`, `ScratchDirectory.Create(..., separate)`, `ManagedGitDirectory.Resolve/WriteOwner`, `SeparateGitDirectories.SweepOrphans`, the `separateGitDir` clone parameter.
- Produces: optional trailing `CloneLayoutOptions? layout = null` on `PrepareRepoRoot` and `ReconcileRepoRoot` (null means `CloneLayoutOptions.Default`; `string? gitRoot` is also threaded through `TryReferenceCheckout` and `CloneIntoPlace`, while `ReferenceCheckout` itself changes in Task 7; tests inject a temp `GitRoot` and `ForceSeparate: true` so Linux CI exercises the separate path).

- [ ] **Step 1: Write failing tests** with a real local origin: `Clone_ForcedSeparate_LeavesGitFileAtCheckoutAndGitDirUnderRoot`; `Clone_Separate_OwnerRecordNamesFinalPath` (success case: the record's `checkout` equals the final path), `Clone_Separate_FailedClone_RemovesGitDirectory`; `Clone_Separate_FailedMove_RemovesGitDirectory`; `PrepareRepoRoot_ExistingManagedSeparateCheckout_IsAdoptedNotRecloned` (second call returns `NeedsReconciliation: true`, git client `Clone` count 1); `PrepareRepoRoot_GitFileResolveRejects_KeepsRefusalWithSpecificReason`; `PrepareRepoRoot_DebrisWithoutGitEntry_StillDeleted`; `Clone_Separate_OtherCheckoutsGitFileIsNeverDeleted`; `Clone_Separate_RunsOrphanSweepOnlyForSeparateClones` (in-place clone leaves an old orphan in place); `Clone_Separate_ReferenceFailureRetry_RemovesOldPairAndPairsTheNewScratch` (the retry in `CloneIntoPlace` recreates the scratch and must call `PairGitDirectory` again; asserts the old git dir and both old locks are gone and a new pair exists); `Clone_BeyondCeiling_ThrowsBeforeAnyGitCall` (layout injected with `IsWindows: true`; git client Clone count 0). Race class: `TwoAppHostsSameFinalPath_OneCheckoutOneGitDirectoryKept` (two threads, `Barrier`; exactly one git dir remains under the root, it is the one the winning `.git` file names).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~LocalGitCheckoutSeparate|FullyQualifiedName~CloneLayout|FullyQualifiedName~GitPathLimit"`. Expected: FAIL.
- [ ] **Step 3: Implement** the section 5 rows for `PrepareRepoRoot` (existing-checkout test becomes `Resolve(...).GitDirectory is not null`), the `.git`-file refusal (only when `Resolve` rejects, message built from `Refusal`), the post-clone and post-`Move` `.git` checks (same helper), and the layout: `CloneLayout.Decide` replaces `EnsureFits`; for `Separate`, create the scratch with the pairing, pass `scratch.GitDirectory` to both clone calls, write the owner record before `Directory.Move` (the ordering is a crash-safety invariant with no seam between write and move, so it is covered by review, not a test), call `scratch.AdoptGitDirectory()` after a successful move, run `SweepOrphans` first. Update the stale comment above the `.git`-file refusal. Remove `EnsureFits` and its now-dead tests.
- [ ] **Step 4: Run** the filter, then the whole `Aspire.Hosting.ServiceSources.Tests` project. Expected: PASS.
- [ ] **Step 5: Commit** "Clone into a short separate git directory when the git directory would be too long (#479)".

### Task 7: Existing-checkout sites: reference, marker, reconcile

**Files:**
- Modify: `Git/LocalGitCheckout.cs` (`ReferenceCheckout`, `UseExistingCheckout`/`ReconcileRepoRoot`), `Prepare/PrepareMarker.cs` (`LocationFor`, doc comment), `Prepare/CheckoutPreparation.cs` (call site passes the git root default)
- Test: `LocalGitCheckoutSeparateTests.cs` (additions), `test/Aspire.Hosting.ServiceSources.Tests/Prepare/CheckoutPreparationTests.cs` (existing; holds `LocationFor_AManagedCheckout_IsInsideTheCheckoutsOwnGitDirectory`), new class `Prepare/PrepareMarkerSeparateTests.cs` (`IO`)

**Interfaces:**
- Consumes: `ManagedGitDirectory.Resolve`, `SeparateGitDirectories.RefreshLastUsed`, `GitPathLimit.LongestPackFile`.
- Produces: `PrepareMarker.LocationFor(string serviceName, string repoRoot, string appHostDirectory, bool managedCheckout, string? gitRoot = null)` (null `gitRoot` means `SeparateGitDirectories.DefaultRoot()`) returning `string?`: null only when `.git` is a file that `Resolve` rejects; with no `.git` entry or an in-place directory it keeps `repoRoot\.git\servicesources-prepare.json`, so existing tests with a fake root pass unchanged. `CheckoutPreparation.Decide` makes `Decision.MarkerPath` nullable and its later `Read`/`Write` skip on null (test `Decide_RejectedGitFile_RunsStepAndWritesNoMarker`); `ReferenceCheckout(string? home, ServiceDefinition definition, IGitClient gitClient, string? gitRoot = null)` still returns the candidate checkout path; `CloneIntoPlace` converts it to the git directory from `Resolve` before passing it to `CloneWithReference`. It accepts an in-place `.git` directory or a resolved separate checkout.

- [ ] **Step 1: Write failing tests**: `ReferenceCheckout_SeparateManagedCheckout_IsBorrowedFrom` (a recording client asserts `CloneWithReference` receives the resolved git directory); `ReferenceCheckout_RejectedGitFile_ReturnsNull_NoError`; `ReferenceCheckout_LinkedWorktreeGitFile_StillNull`; `LocationFor_Managed_InPlace_IsUnderDotGit` (unchanged); `LocationFor_Managed_Separate_IsInsideResolvedGitDirectory`; `LocationFor_Managed_RejectedGitFile_IsNull_AndReadWriteDegrade`; `Reconcile_SeparateCheckout_RefreshesLastUsedWhenOverADayOld`; `Reconcile_InPlaceCheckoutWhosePackPathWouldNotFit_LogsNoticeNamingDeleteAndReclone` (`IsWindows: true` injected; the notice goes to `ServiceSourcesWarnings`, because the progress stream is already completed before reconcile; text contains `delete`, `checkouts\<name>` and `re-run`); `Reconcile_InPlaceCheckoutThatFits_LogsNothing`; `Reconcile_Never_MutatesAnInPlaceCheckout` (git dir bytes and `.git` directory identity unchanged: no migration).
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~LocalGitCheckoutSeparate|FullyQualifiedName~PrepareMarkerSeparateTests|FullyQualifiedName~CheckoutPreparationTests"`. Expected: FAIL.
- [ ] **Step 3: Implement** per section 5 rows `ReferenceCheckout`, `PrepareMarker.LocationFor` and D4.3 / D5. `GetHeadCommitSha` and the git-following `-C` calls stay untouched.
- [ ] **Step 4: Run** the filter, then the whole test project. Expected: PASS.
- [ ] **Step 5: Commit** "Recognise separate git directories when borrowing, marking and reconciling (#479)".

### Task 8: Windows deep-path test, docs, CHANGELOG, API baseline, full verify

**Files:**
- Create: `test/Aspire.Hosting.ServiceSources.Tests/WindowsFactAttribute.cs` (a `FactAttribute` that sets `Skip` when `!OperatingSystem.IsWindows()`, same shape as `DcpFactAttribute`), `test/Aspire.Hosting.ServiceSources.Tests/Git/DeepPathCloneTests.cs` (`IO`)
- Modify: `docs/troubleshooting.md` ("Long paths on Windows" and the `cannot clone into` subsection), `docs/sources/repository.md` (checkout paragraph near line 74 and the managed-checkout sections that say a `--separate-git-dir` clone is refused), `CHANGELOG.md`, bottom link list

- [ ] **Step 1: Write the Windows tests** with `[WindowsFact]`: `DeepAppHost_200Characters_ClonesWithGitDirUnderGitRoot` (real `GitCliClient`, AppHost directory padded to about 200 characters under a temp root, real default-shaped git root override), `SecondRun_ReusesTheCheckout`, `Fetch_AndGcAuto_AndBorrowedObjectClone_Work` (with `git -C <checkout> gc --auto` and `repack -a -d`, no `Filename too long`), `AppHost_240Characters_IsRefusedWithCeilingMessage`, `GitFileAndMovedWorkingTree_StatusWorks` (re-check of the two section 2 behaviours: `core.worktree` unset, move survives).
- [ ] **Step 2: Run** `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "FullyQualifiedName~DeepPathCloneTests"`. Expected: 5 passed on Windows (paste the output into the PR), 5 skipped elsewhere.
- [ ] **Step 3: Docs.** `troubleshooting.md`: replace the "cannot write a pack file past 259" subsection (a wrong model; see the correction at the top) with the new behaviour (a deep AppHost now clones into `%LOCALAPPDATA%\ServiceSources\git`; the refusal is now the working-tree ceiling, with its new wording and the 190-character AppHost figure for names up to 18 characters; the notice for an existing deep in-place checkout and its remedy, delete `checkouts\<name>` and re-run; orphaned git directories are removed after 14 days). `repository.md`: say a `.git` file the tool wrote itself is recognised and anything else (linked worktree, foreign `--separate-git-dir`, a moved checkout) is still refused with the stated reason.
- [ ] **Step 4: CHANGELOG.** Under `## [Unreleased]` > `### Changed` add a bold-lead entry in the file's register: a managed clone on Windows whose AppHost path is too long for the in-place pack path now clones with a separate git directory under `%LOCALAPPDATA%\ServiceSources\git`, so AppHost directories up to 190 characters work where #474 refused them at about 129; the refusal remains beyond the working-tree ceiling with a new message; working checkouts are never moved; unreferenced git directories are deleted after 14 days; existing deep in-place checkouts get a notice to delete and re-clone. Reference `([#479])` and link `docs/troubleshooting.md#long-paths-on-windows`. Append `[#479]: https://github.com/flojon/aspire-servicesources/issues/479` to the bottom link list next to the other `[#4xx]` lines.
- [ ] **Step 5: API baseline.** Run `git diff --stat -- src/Aspire.Hosting.ServiceSources/PublicAPI.Unshipped.txt src/Aspire.Hosting.ServiceSources/PublicAPI.Shipped.txt`. Expected: empty (everything is `internal`). If the build reports `RS0016`/`RS0017`, a type was made public by mistake: make it `internal` rather than recording it.
- [ ] **Step 6: Full verify** (per `.github/workflows/ci.yml`): `dotnet restore`; `dotnet build -c Release --no-restore -warnaserror`; `dotnet test -c Release --no-build`; then once `dotnet pack src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj -c Release --no-build -o ./artifacts/package`. Expected: 0 warnings, all tests pass. The Docker smoketests, ATS export check and invariants script are CI only.
- [ ] **Step 7: Commit** "Test deep AppHost clones on Windows; document the separate git directory (#479)".

---

## Self-review notes

- Spec coverage: D1 Task 1/6; D2 Tasks 2/5; D3 Tasks 3/4/6; D4 Tasks 3/5/7; D5 Task 7; D6 Task 2; section 5 sites Tasks 6/7 (`GetHeadCommitSha`, `-C` calls, `PathSource`, `WorktreeHome` intentionally unchanged); section 6 and 8 Task 8; section 7 docs/changelog/API Task 8.
- `CloneLayoutOptions` and the optional `layout` parameter are the "internal injection" of the spec's seam; no public surface.
