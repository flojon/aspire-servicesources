# A setting to place managed checkouts outside the AppHost directory (#475)

**Date:** 2026-11-19
**Status:** Draft
**Resolves:** #475.
**Relates to:** #479 / #519 (short separate git directory; merged, not rebuilt here), #474 / #483 (the
path-length refusal this reworks the wording of), #434 (linked-worktree seeding and object borrowing), #224
(checkout names stay path segments), #433 (prepare markers).

## 1. Problem and what the code does today

Every managed checkout is `<AppHost>\.servicesources\checkouts\<name>`. `LocalGitCheckout.ManagedRepoRoot(appHostDirectory,
checkoutName)` is the one pure function that names it, and it is called from `DeferredCheckout` (four sites),
`LocalGitCheckout.IsColdManagedCheckout`, `PrepareRepoRoot` (which takes the AppHost directory as a string and is called from
`LocalCheckoutPrefetch` twice), `DeferredCheckout.BlockedChangedCommand` (a static method with no builder) and
`ReferenceCheckout`. #519 lets git keep its git
directory in a short per-user path, but the working tree is still under the AppHost, so an AppHost directory
past roughly 190 characters is refused, and the ticket's smoke test (comment on #475) shows a 145-character
AppHost already fails later: the service's `dotnet run` hits MSBuild "Path ... too long" inside the checkout.
Deep AppHost paths are routine with worktree tools (`.claude/worktrees/<long-name>/...`).

There is no way to put checkouts elsewhere. This spec adds one.

## 2. Decisions already made by the human

1. A full relocation setting, `ServiceSources:CheckoutsDirectory`: a configuration key, so it is also the
   environment variable `ServiceSources__CheckoutsDirectory`, the command line, user secrets, appsettings, and
   (consistent with `repositoryRoot`) `"checkoutsDirectory"` in `servicesources.local.json`.
2. The default stays `<AppHost>\.servicesources\checkouts`. A short Windows path (for example `C:\ss`) is only
   documented, and suggested in the path-too-long errors. Nothing switches to a per-user location by default.
3. `selection.json` and the `path`-source prepare markers (`.servicesources/prepare/<service>.json`) stay with the
   AppHost. (A managed checkout's own prepare marker lives inside its git directory today and moves with the
   checkout; see 3.9. The wording is amended to say so, it is not a departure from the intent.)
4. Under an external directory, checkouts are namespaced by a hash of the AppHost path, and the leftover sweep and
   lock files are scoped to that subfolder.

## 3. Design

### 3.1 The setting

`DeveloperConfiguration.CheckoutsDirectoryKey = "ServiceSources:CheckoutsDirectory"`, plus
`DeveloperConfigFileSource.FileCheckoutsDirectoryKey = "checkoutsDirectory"` (an `internal const`, like
`FileRepositoryRootKey`) added to `ReRootedValues`. Blank or unset means the default. No public API changes
(configuration only, like `RepositoryRoot`), so `PublicAPI.*.txt` is untouched.

A new internal `CheckoutsLocation` is computed **once per builder** and cached beside `ServiceSourcesConfigCache`
(a `ConditionalWeakTable` keyed on the builder, so call sites cannot disagree). Computing it is **pure string work**: no
filesystem access, no exceptions for filesystem state, so `ManagedRepoRoot` keeps the purity its documentation promises
and deferral (DCP freezes the path before the clone) still works. It carries `OwnedDirectory`, `CheckoutsRoot`
(`OwnedDirectory\checkouts`) and `IsExternal`, and a `Problem` (a configuration error computed lexically, 3.2) that is
thrown only by the callers that are about to act.

| Setting | `OwnedDirectory` | `CheckoutsRoot` |
|---|---|---|
| unset | `<AppHost>\.servicesources` | `<AppHost>\.servicesources\checkouts` |
| `C:\ss` | `C:\ss\<hash>` | `C:\ss\<hash>\checkouts` |

(The property is `OwnedDirectory`, not `ToolDirectory`, which is already the static class for the AppHost's own
`.servicesources`.)

**Call sites.** `ManagedRepoRoot(string appHostDirectory, string name)` becomes `ManagedRepoRoot(CheckoutsLocation, string
name)`; the containment check stays inside it. Every site is changed to take the location instead of (or beside) the
AppHost string: `IsColdManagedCheckout` (callers `DeferredCheckout` and `LocalCheckoutPrefetch`), `PrepareRepoRoot` (the
`EnsureToolDirectory` and `ManagedRepoRoot` calls, and its two prefetch callers), `DeferredCheckout.BlockedChangedCommand`
and its caller, the other `DeferredCheckout` sites, and `ReferenceCheckout`, which also needs the home's AppHost directory
to compute the home's hash (3.7). Everything that is not a checkout stays keyed on the AppHost directory:
`ToolDirectory.PathIn(appHost)` for `selection.json`, `worktree-seed.json` and `path`-checkout prepare markers, and their
existing `ToolDirectory.Ensure(appHost)` calls (`SourceSelectionStore`, `PrepareMarker`) still create that directory's
`.gitignore` even when checkouts are external.

**Errors.** `Problem` is raised where the work is real: the actual `AddService` and deferred `Register` paths report it as
a configuration error. The speculative background prefetch (`LocalCheckoutPrefetch`, whose contract is that speculation
never fails `AddService`) treats a location with a `Problem` as "nothing to prefetch" and lets the real path report it.

The namespaced layout keeps the `checkouts` segment (`<root>\<hash>\checkouts\<name>`) so the owned directory is shaped like
`.servicesources`: the `.gitignore` and the build barrier files (written by `CheckoutBuildBarrier`) sit in the owned
directory, and the checkouts, their `.incoming-*` staging directories and `.incoming-*.lock` files sit in `checkouts\`, as
today. That keeps the tool's own files out of the namespace of checkout names, so a checkout cannot collide with a barrier
file. `.incoming-*` names inside `checkouts\` are as before.

### 3.2 Resolving the value

Lexical rules, applied when the location is computed, producing `Problem` on a violation (messages name the key, the
value and the resolved path):

- **Absolute only.** A relative value is refused. Resolving it against the AppHost would give every linked worktree its own
  sibling directory and place checkouts beside, or inside, the tree this setting exists to escape, and `PathOverrideCheck`
  does not cover this key. (This removes the seeded-relative-value question; it also keeps the setting from meaning
  something different per worktree.)
- **Local paths only.** UNC (`\server\share`), `\?\` and `\.\` forms are refused (the rule `SeparateGitDirectories.IsLocalRootedPath`
  already applies), because a committed configuration value must not be able to make the tool open an SMB connection.
- **Not at or around the AppHost.** Refused when the resolved root is the AppHost directory or one of its ancestors (the
  owned directory would land among source), or lies inside the AppHost's `.servicesources` (a collision with the default
  layout). A root elsewhere inside the AppHost's repository is allowed (3.4). A filesystem root such as `C:\` is allowed
  (`C:\<hash>`).
- No `~` expansion, and no `%VAR%` expansion by this tool: the shell expands the environment variable form
  (`ServiceSources__CheckoutsDirectory=C:\ss`), and in a file the path is written out.

Filesystem rules, checked only at the point a clone is about to happen (the same place `EnsureToolDirectory` runs
today), never while a path is merely computed or by the prefetch's speculative check:

- The root, if it exists, is a directory, and can be created if it does not (`CreateDirectory`, failing as a
  configuration error naming the key).
- The owned directory passes the ownership check in 3.3.

### 3.3 Hash namespace and ownership

`<hash>` = the first 10 hex characters of SHA-256 over the AppHost directory, built the way `ManagedGitDirectory.HashOf`
builds its input (`GetFullPath`, trailing separator trimmed, upper-cased, UTF-8). Ten hex is 40 bits: no realistic
collision among one developer's AppHosts, and shorter than #519's 16 because this segment is paid on the working-tree path.
The AppHost directory is the key because it owns `selection.json`, prepare state and configuration, and because linked
worktrees have distinct AppHost directories (3.7).

Known limits, documented rather than solved: the hash is of the spelling, so the same directory reached through a
junction, a `subst` drive or an 8.3 name gets a second namespace and a second set of clones; and upper-casing merges two
Linux directories that differ only in case (rare; it only shares checkouts). Neither is a safety problem.

**The name is predictable, so ownership is checked.** Anyone who can write to a shared root could pre-create
`<root>\<hash>\checkouts\<name>` holding a repository with a matching origin, and the existing adoption of a checkout on an
origin match would then build and run it. So at clone time, before anything is written into it (including the `.gitignore`
and barrier files), the owned directory is handled like this:

- Absent: created by this run, and an owner record (`owner.json`, naming the AppHost directory) is written into it.
- Present, a directory, not a reparse point, with an owner record naming this AppHost: used.
- Anything else (a file, a reparse point, a directory with no record or with another AppHost's record): refused with an
  error naming the directory, so the developer deletes it. The tool never adopts or deletes a directory it did not create.
  There is a check-then-use gap against a hostile local user racing the creation; a root another user can write to is
  outside what the tool defends against, and the docs say so (section 4).

The recursive delete of "debris" with no `.git` (`CloneIntoPlace`) and the staging sweeps therefore only run inside a
directory this tool created; the comment there saying everything under `.servicesources` is tool-managed is updated to say
"under an owned directory". `ScratchDirectory.SweepAbandoned`/`SweepOne` currently has no reparse-point check and gains one
(skip, as `SeparateGitDirectories.SweepOne` does), because a planted link is now reachable from a root the developer chose.

Two AppHosts sharing one `CheckoutsDirectory` never see each other's checkouts, staging directories, locks or barrier
files. Two runs of the same AppHost share a namespace and are protected as today (`CheckoutNameLock`, `.incoming-*.lock`
ownership, rename into place).

### 3.4 `.gitignore` and the build barrier

Both are written into `CheckoutsLocation.OwnedDirectory`, never into the configured root. The root can be a directory the
developer uses for other things (`C:\src`), and a `*` `.gitignore` or a `Directory.Build.props` written there would change the
status and the builds of everything else under it. The owned directory is tool-created (3.3), so it gets the same treatment
as `.servicesources`: `.gitignore` (`*`, `!.gitignore`) and `CheckoutBuildBarrier.Ensure`, after the ownership check. A new
`ToolDirectory.EnsureAt(string directory)` holds the shared body and `Ensure(appHostDirectory)` calls it (a distinct name,
since both take a string).

The barrier is still wanted externally: a root under a directory that has its own `Directory.Build.props`, `nuget.config`,
`.editorconfig` or `.mvn` would otherwise leak into every checkout, which is #224's concern. The `.gitignore` matters only
when the root lies inside some git repository, and costs nothing when it does not.

### 3.5 Path-length model and the refusal wording

`CloneLayout.Decide` already works from the real checkout and staging paths, so a short external root simply decides "in
place" with no further change. What changes:

- `GitPathLimit.StagingSuffixLength`, `MaxInPlaceAppHostLength` and `MaxSeparateAppHostLength` (pinned today at 170 and 190
  by `GitPathLimitTests`) stay as the default-location budgets. For an external root the budget is stated for the owned
  directory: the longest `CheckoutsRoot` is the working-tree (or in-place) root limit minus `\.incoming-<id>` (19), and the
  messages quote that figure instead of an AppHost length.
- The three refusals (`CloneLayout.Decide`, `GitPathLimit.GitRootRefusal`, `NoteExistingCheckout`) take the location. For
  the default location each gains "or set `ServiceSources:CheckoutsDirectory` (environment variable
  `ServiceSources__CheckoutsDirectory`) to a short directory such as `C:\ss`". For an external location they say to shorten
  `CheckoutsDirectory` and no longer say to move the AppHost. `NoteExistingCheckout`'s "move it out of the AppHost's
  `.servicesources` directory" is reworded to name the actual checkouts directory.
- The 145-character case is not refused by git; it fails in MSBuild inside the checkout, so no refusal fires and the tool
  does not try to predict MSBuild's limit. The fix is documentation (a troubleshooting entry naming the MSBuild error) and the
  setting.

### 3.6 Sweep, locks and existing checkouts

- `ScratchDirectory.SweepAbandoned(checkoutsRoot, ...)` and `Create(checkoutsRoot)` take the root as a parameter and receive
  `<hash>\checkouts`, so they only see this AppHost's staging directories and locks. Their logic is unchanged apart from the
  reparse-point skip in 3.3.
- `SeparateGitDirectories.SweepOrphans` and `ManagedGitDirectory` work from an owner record naming the checkout's full path
  and already treat an unmounted drive as "proves nothing" (`IsLocalRootedPath`). They need no change; a test pins that an
  external-root checkout is seen as live.
- Changing the setting does not move or delete anything. A checkout already under `.servicesources\checkouts` stays there and
  is not used; the docs say to delete or move it. (No runtime notice; see Open Questions.)

### 3.7 Worktree seeding and borrowing objects

- `WorktreeSeed` copies `servicesources.local.json` and `selection.json`, never checkouts, so it needs no change. Because the
  setting must be absolute (3.2), a seeded file means the same thing in the linked worktree.
- Each worktree has its own AppHost directory, so its own `<hash>` namespace and checkouts. That is the point: a deep
  `.claude/worktrees/...` AppHost gets short, independent checkouts.
- Borrowing: `ReferenceCheckout` gains this run's `CheckoutsLocation` and computes the home's candidate as
  `<root>\<hash(home AppHost)>\checkouts\<name>` under **this run's root**. The home path comes from git (`WorktreeHome`),
  this run's from `builder.AppHostDirectory`; a spelling difference (junction, 8.3) makes the candidate miss and the clone
  proceeds without borrowing. The existing checks (a `.git` directory or a separate git directory this tool made, and a
  matching origin URL) still apply, and any failure still degrades to a plain clone. A home that used a different root or the
  default location is not found; reading another worktree's configuration is out of scope.

### 3.8 `path`-source confinement

No rule changes, and none is needed. What each confinement is anchored to:

- A catalog `path:` is confined to the AppHost's repository root (`PathSource.ConfinementRootOf`: nearest `.git` ancestor of
  the AppHost, or `ServiceSources:RepositoryRoot`). It never involved the checkouts directory.
- A path inside a managed checkout (`ConfineProject`, `CheckoutRelativePath`) is confined lexically to that checkout's root,
  wherever it is.
- A developer `repository.path` or `path.path` override is an arbitrary developer directory by design.

The one interaction: an external `CheckoutsDirectory` outside the AppHost's repository means a managed checkout is no longer
inside that repository's tree. Nothing relied on that: the build barrier (3.4) isolates checkout builds, and the checkout is
its own repository. The docs say plainly that a `path:` does not reach the checkouts.

### 3.9 Prepare markers

A managed checkout's prepare marker lives inside the clone's git directory (`<checkout>\.git\servicesources-prepare.json` or
the separate git directory), not under `.servicesources\prepare`. It moves with the checkout, which is correct (a re-cloned
checkout must re-prepare) and needs no work. Decision 3 is therefore about the `path`-checkout markers that do live under
`.servicesources\prepare`; those are unchanged.

## 4. Attack surface

- **What the setting trusts.** The value comes from configuration, including a committed `appsettings.json` or a shared
  `servicesources.local.json`, so a hostile value is possible. It must be absolute and local (UNC and device paths are
  refused, so it cannot trigger an SMB connection), and not the AppHost or an ancestor. The tool then creates `<value>\<hash>`
  only if absent, writes an owner record, `.gitignore` and barrier files inside it only, and deletes only under
  `<hash>\checkouts` (debris with no `.git`, its own `.incoming-*`). It never adopts an existing `<hash>` it did not create
  (3.3), and never writes into or deletes from the configured directory itself. A hostile value can at worst make the tool
  create a directory and clone into it somewhere local and writable, a smaller effect than the clone destination a `path`
  override already allows.
- **Predictable names and planted content.** The hash is not secret. The owner record and the refusal of an unowned
  `<hash>` (3.3) stop the tool adopting a checkout somebody pre-planted there. What is left is a local race against creation
  by a user who can already write to the developer's chosen root; the docs tell developers not to point the setting at a
  directory other users can write to, since a checkout is code that is built and run.
- **Links.** `<hash>` as a reparse point is refused, and the staging sweep skips reparse points (3.3).
- **Prepare commands** still run with the checkout as working directory; relocation changes where, not what, runs, and the
  existing approval and confinement rules are untouched.
- **Information.** The refusals quote the resolved path, as the existing path refusals do.

## 5. Scope

In: the setting and its resolution (`CheckoutsLocation`), threading it through `ManagedRepoRoot` and its callers (1, 3.1),
the `ToolDirectory.EnsureAt` refactor, hash namespacing and ownership, the reparse skip in the staging sweep, the refusal
wording, `ReferenceCheckout`, tests, docs (`docs/sources/repository.md`, `docs/guides/configuration.md`,
`docs/troubleshooting.md`), the XML docs that hard-code `.servicesources/checkouts/<serviceName>` beneath the AppHost
(`ServiceSourcesBuilderExtensions`, `CheckoutBuildBarrier`, `LocalGitCheckout`), and a `CHANGELOG.md` entry under Added.

Out: moving existing checkouts automatically; a per-user default; a builder API for the setting; #479's separate git
directory (shipped; this composes with it); reading another worktree's configuration; changing where `selection.json` or
`.servicesources\prepare` live.

Overlap: open PR #529 touches `CHANGELOG.md`, `docs/sources/repository.md` and `DeferredCheckout.cs`, all of which this
touches. Merge order is the human's; whichever lands second rebases.

## 6. Testing

Traits per the repo: pure tests carry none; tests that touch the filesystem, including reparse-point tests and any test that
runs `dotnet`, carry `[Trait("IO", "true")]`; tests that race two AppHosts add `Concurrency`, and any that wait on a timeout
add `Timing`.

- `CheckoutsLocation` (pure): unset gives the default layout; an absolute value gives `<root>\<hash>\checkouts`; relative,
  UNC, `\\?\`, the AppHost directory, an ancestor, and a path inside `.servicesources` give a `Problem`; computing creates
  nothing; one hash for case and trailing-separator variants of one path, two hashes for two AppHosts.
- `ManagedRepoRoot` with an external location, and the existing containment tests for `..` names still passing.
- Prefetch with a `Problem` location: no throw from the speculative path; the real `AddService` path reports it.
- Ownership (IO): an absent `<hash>` is created with a record; one with this AppHost's record is used; a file, a reparse
  point (skipped where links cannot be created), a directory with no record and one with another AppHost's record are
  refused, and none is written into or deleted.
- Two AppHosts, one root (IO, Concurrency): neither sees the other's `.incoming-*` or locks; sweeping from one leaves the
  other's staging and lock alone; the owned directory holds `.gitignore` and the barrier files and the root does not.
- `ScratchDirectory.SweepAbandoned` skips a reparse point named `.incoming-*` (IO).
- Borrowing (IO, as the existing `ReferenceCheckout` tests): a home under the same root is found; origin mismatch still
  refused; a home at another root or the default location falls back to a plain clone without error.
- Messages: the three refusals name the setting for the default location and say to shorten it for an external one;
  `GitPathLimitTests` figures for the default location are unchanged.
- #519 interplay: a long external root still decides separate or refused by its real length.
- **The ticket's own case** (cannot run on ubuntu CI), verified on the Windows dev box: an AppHost directory of about 145
  characters, `ServiceSources__CheckoutsDirectory=C:\ss`, a managed service, and `dotnet run` of that service gets past the
  point where the same AppHost without the setting fails with MSBuild "Path ... too long". Result recorded in the PR. A
  Windows-only automated version (IO) builds a project at a checkout path over 260 characters only if it proves reliable.
- `scripts/smoketest-config-layers.sh` gains a case for the new key.

## 7. Open Questions

1. **Hash length and the `checkouts` segment.** 10 hex and `<hash>\checkouts\<name>` cost 22 characters of working-tree
   path. Dropping `checkouts` would save 10 but put barrier files and checkout names in one namespace. Confirm the trade.
2. **Refusing an ancestor of the AppHost** (3.2) also refuses a root like `C:\src` when the AppHost is under it. The
   alternative is to allow it, since only `<hash>` is written; the cost is hash directories among the developer's
   repositories. Keep the refusal?
3. **Relative values refused** (3.2). The alternative (resolve against the AppHost) gives every worktree a different place
   and defeats the purpose in a deep AppHost. Confirm.
4. **Extras left out as beyond the ticket:** borrowing from a home that used the default location (the migration case), and
   a runtime notice when old checkouts remain under `.servicesources\checkouts`. Both are small and could follow if wanted.
