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
`LocalCheckoutPrefetch`, `LocalGitCheckout.PrepareRepoRoot` and `ReferenceCheckout`. #519 lets git keep its git
directory in a short per-user path, but the working tree is still under the AppHost, so an AppHost directory
past roughly 190 characters is refused, and the ticket's smoke test (comment on #475) shows a 145-character
AppHost already fails later: the service's `dotnet run` hits MSBuild "Path ... too long" inside the checkout.
Deep AppHost paths are routine with worktree tools (`.claude/worktrees/<long-name>/...`).

There is no way to put checkouts elsewhere. This spec adds one.

## 2. Decisions already made by the human (not reopened)

1. A full relocation setting, `ServiceSources:CheckoutsDirectory`: a configuration key, so it is also the
   environment variable `ServiceSources__CheckoutsDirectory`, the command line, user secrets, appsettings, and
   (consistent with `repositoryRoot`) `"checkoutsDirectory"` in `servicesources.local.json`.
2. The default stays `<AppHost>\.servicesources\checkouts`. A short Windows path (for example `C:\ss`) is only
   documented, and suggested in the path-too-long errors. Nothing switches to a per-user location by default.
3. `selection.json` and the `path`-source prepare markers (`.servicesources/prepare/<service>.json`) stay with the
   AppHost.
4. Under an external directory, checkouts are namespaced by a hash of the AppHost path, and the leftover sweep and
   lock files are scoped to that subfolder.

## 3. Design

### 3.1 The setting

`DeveloperConfiguration.CheckoutsDirectoryKey = "ServiceSources:CheckoutsDirectory"`, added to
`DeveloperConfigFileSource.ReRootedValues` as `("checkoutsDirectory", CheckoutsDirectoryKey)`. Blank or unset means the
default. No public API changes (configuration only, like `RepositoryRoot`), so `PublicAPI.*.txt` is untouched.

A new internal `CheckoutsLocation` is resolved **once per builder** and cached beside `ServiceSourcesConfigCache`
(a `ConditionalWeakTable` keyed on the builder, so the many call sites cannot disagree). It carries:

- `ToolDirectory`: where the checkouts' sibling files (`.gitignore`, build barrier) live.
- `CheckoutsRoot`: `ToolDirectory\checkouts`.
- `IsExternal`.

| Setting | `ToolDirectory` | `CheckoutsRoot` |
|---|---|---|
| unset | `<AppHost>\.servicesources` | `<AppHost>\.servicesources\checkouts` |
| `C:\ss` | `C:\ss\<hash>` | `C:\ss\<hash>\checkouts` |

`ManagedRepoRoot(appHostDirectory, checkoutName)` becomes `ManagedRepoRoot(CheckoutsLocation, checkoutName)`; the
containment check (`IsContainedCheckoutDirectoryName`) is unchanged and still runs inside it. Every caller already has
the builder, so each obtains the location from it. Everything that is not a checkout stays keyed on the AppHost
directory: `ToolDirectory.PathIn(appHost)` for `selection.json`, `worktree-seed.json` and the `path`-checkout prepare
markers.

The namespaced layout keeps the `checkouts` segment (`<root>\<hash>\checkouts\<name>`) rather than putting names
directly under `<hash>`. It costs 10 characters but means the hash directory is shaped exactly like `.servicesources`,
so the `.gitignore`, the build barrier files and the `.incoming-*` staging and lock files sit beside the checkouts
exactly as today, and none can collide with a service's checkout name.

### 3.2 Resolving the value

- Relative values resolve against the AppHost directory, like every other path a developer writes
  (`ResolveConfiguredRepositoryRoot` does the same). Absolute is recommended and documented, because a relative value
  in a seeded worktree resolves differently per worktree (see 3.7).
- No `~` or `%VAR%` expansion (the configuration providers already substitute environment variables for the env var
  form; a literal `%LOCALAPPDATA%` in a file is not expanded and the docs say to write the path out).
- The value is resolved lazily, at the first managed-checkout path computation, not at `AddServiceSources` time, so an
  AppHost with only `path` services never reads or creates anything. The directory is created at first clone
  (`Directory.CreateDirectory`), not at resolution.
- Refused with a `ServiceSourcesConfigurationException` naming the key, the value and the resolved path, when the
  resolved path exists and is a file, is a filesystem root with no usable child, cannot be created, or **is the AppHost
  directory or contains it** (an ancestor). The last rule is what keeps a misconfigured value from placing a hash
  directory inside the repository's own source tree or scattering them across a directory the developer uses for other
  things. An external directory *inside* the AppHost's repository (not an ancestor of the AppHost) is allowed; see 3.4.

### 3.3 Hash namespace

`<hash>` = the first 10 hex characters of SHA-256 over the AppHost directory, built the way
`ManagedGitDirectory.HashOf` builds its input (`GetFullPath`, trailing separator trimmed, upper-cased, UTF-8), so two
spellings of one Windows path agree. Ten hex is 40 bits: no realistic collision among one developer's AppHosts, and
shorter than #519's 16 because this segment is paid on the working tree path, which is the scarce one. The AppHost
directory is the key because the AppHost is the unit that owns `selection.json`, prepare state and configuration, and
because linked worktrees have distinct AppHost directories (3.7).

Two AppHosts sharing one `CheckoutsDirectory` therefore never see each other's checkouts, staging directories, locks or
barrier files. Two *runs of the same AppHost* still share a namespace, and are protected exactly as today
(`CheckoutNameLock`, the `.incoming-*.lock` ownership, rename into place).

A pre-existing `<hash>` that is a file or a reparse point is refused rather than followed: the recursive delete of
debris and the sweeps act under it, so a link planted at that name must not redirect them. (Same posture as
`SeparateGitDirectories.SweepOne`, which skips reparse points.)

### 3.4 `.gitignore` and the build barrier

Both are written into `CheckoutsLocation.ToolDirectory`, never into the configured root itself. The root can be a
directory the developer uses for other things (`C:\src`), and a `*` `.gitignore` or a `Directory.Build.props` written
there would change the status and the builds of everything else under it. The tool-created `<hash>` directory is
tool-owned by construction, so it gets `ToolDirectory.Ensure`-style treatment: `.gitignore` (`*`, `!.gitignore`) and
`CheckoutBuildBarrier.Ensure`. `ToolDirectory.Ensure` is refactored from `Ensure(appHostDirectory)` into a path-based
`Ensure(directory)` plus the existing AppHost overload.

The barrier is still wanted externally: a root under a directory that has its own `Directory.Build.props`,
`nuget.config`, `.editorconfig` or `.mvn` would otherwise leak into every checkout, which is exactly #224's concern. The
`.gitignore` matters only when the root lies inside some git repository, and costs nothing when it does not.

### 3.5 Path-length model and the refusal wording

`CloneLayout.Decide` already works from the real checkout and staging paths, so a short external root simply decides
"in place" with no further change. What changes:

- `GitPathLimit.StagingSuffixLength`, `MaxInPlaceAppHostLength` and `MaxSeparateAppHostLength` are expressed against the
  AppHost directory plus the fixed `.servicesources\checkouts` tail. They are replaced by a computation from the
  actual `CheckoutsRoot` (budget in characters for the root, not for the AppHost). The messages that quote them
  (`CloneLayout.Decide`, `GitPathLimit.GitRootRefusal`, `NoteExistingCheckout`) take the location.
- The three "move the AppHost to a shorter path" refusals gain the alternative, and name the setting: for the default
  location, "or set `ServiceSources:CheckoutsDirectory` (environment variable `ServiceSources__CheckoutsDirectory`) to
  a short directory such as `C:\ss`"; when the location is already external, they say to shorten
  `CheckoutsDirectory` and no longer tell the developer to move the AppHost.
- The 145-character case (ticket comment) is not refused by git, it fails in MSBuild inside the checkout, so no
  refusal fires. The fix there is documentation (troubleshooting entry naming the MSBuild error) plus the setting.
  The tool does not try to predict MSBuild's limit.

### 3.6 Sweep, locks and existing checkouts

- `ScratchDirectory.SweepAbandoned(checkoutsRoot, ...)` and `ScratchDirectory.Create(checkoutsRoot)` already take the
  root as a parameter. They receive `CheckoutsRoot`, which is the `<hash>\checkouts` directory, so they only ever see
  this AppHost's staging directories and locks. No change to their logic.
- `SeparateGitDirectories.SweepOrphans` and `ManagedGitDirectory` work from an owner record naming the checkout's full
  path and already treat an unmounted drive as "proves nothing" (`IsLocalRootedPath`). They are location-independent and
  need no change; a test pins that an external-root checkout is seen as live.
- Changing the setting does not move or delete anything. A checkout already under `.servicesources\checkouts` stays
  there and is not used. When the setting is external and the default location still holds checkout directories, a
  notice says so once per run and names the directory, so the developer can delete or move them (same style as the
  #519 "delete and re-clone" notice; notice only).

### 3.7 Worktree seeding and borrowing objects

- `WorktreeSeed` copies `servicesources.local.json` and `selection.json`, never checkouts, so it needs no change. A
  seeded file carrying a relative `checkoutsDirectory` resolves differently in the linked worktree; `PathOverrideCheck`
  does not cover the repository root today either. Decision: leave it, and the docs recommend an absolute value. (If
  the review wants a notice, it is a small addition to `PathOverrideCheck`; listed in Open Questions.)
- Each worktree has its own AppHost directory, so each has its own `<hash>` namespace and its own checkouts. This is the
  point: a deep `.claude/worktrees/...` AppHost gets short, independent checkouts.
- Borrowing (`ReferenceCheckout`) computes the home worktree's candidate. It becomes: the home's checkout under **this
  run's resolved root** (`<root>\<hash(home AppHost)>\checkouts\<name>`), then, if that is not a usable reference, the
  home's checkout at its default location. The second candidate covers the common migration where the home worktree's
  clones were made before the setting existed. Both candidates go through the existing checks (a `.git` directory or a
  separate git directory this tool made, and a matching origin URL), and any failure still degrades to a clone without a
  reference. If the home worktree used a *different* external root, nothing is found and the clone is made without
  borrowing; there is no way to know the other worktree's setting without reading its configuration, which this does
  not do.

### 3.8 `path`-source confinement

No rule changes, and none is needed. What each confinement is anchored to:

- A catalog `path:` is confined to the AppHost's repository root (`PathSource.ConfinementRootOf`: nearest `.git`
  ancestor of the AppHost, or `ServiceSources:RepositoryRoot`). It never involved the checkouts directory.
- A path inside a managed checkout (`ConfineProject`, `CheckoutRelativePath`) is confined lexically to that checkout's
  root, wherever it is.
- A developer `repository.path` or `path.path` override is an arbitrary developer directory by design.

The one interaction is that `ConfinementRootOf` finds the repository by walking up from the AppHost directory; an
external `CheckoutsDirectory` outside that repository means a managed checkout is no longer *inside* the AppHost's
repository tree. Nothing relied on that: the build barrier (3.4) is what isolates checkout builds, and the checkout is
its own repository. The docs say this plainly so nobody expects a `path:` to reach the checkouts.

### 3.9 Prepare markers

A managed checkout's prepare marker lives inside the clone's git directory (`<checkout>\.git\servicesources-prepare.json`
or the separate git directory), not under `.servicesources\prepare`. It therefore moves with the checkout, which is
correct (a re-cloned checkout must re-prepare) and needs no work. Decision 3 ("prepare markers stay with the AppHost")
is read as the `path`-checkout markers that actually live under `.servicesources\prepare`; those are unchanged. Flagged in
Open Questions in case the intent was different.

## 4. Attack surface

- **What the setting trusts.** The value comes from configuration, which includes a committed `appsettings.json` or a
  `servicesources.local.json` a teammate shares, so a hostile value is possible. The tool writes only to
  `<value>\<hash>\...` (a directory it creates) and deletes only under `<hash>\checkouts` (debris with no `.git`, its own
  `.incoming-*` directories). It never deletes, writes a `.gitignore` or a barrier file into the configured directory
  itself (3.4), and refuses an ancestor of the AppHost (3.2). A hostile value can therefore at worst make the tool create
  a `<hash>` directory and clone into it somewhere writable; it cannot make the tool delete or overwrite existing
  content, which is a smaller effect than the clone destination a `path` override already gives.
- **Pre-planted links.** `<hash>` as a reparse point or file is refused (3.3). `<hash>\checkouts` is created by the tool
  inside it. A link planted deeper by someone with write access to a directory the developer chose is outside what this
  tool defends against, same as for `.servicesources` today.
- **Shared root, several users or AppHosts.** Namespacing isolates AppHosts; it is not an access boundary. A root other
  local users can write to is the developer's choice, and the docs say so: a checkout is code that gets built and run.
- **Prepare commands** still run with the checkout as working directory; relocation changes where, not what, runs, and the
  existing approval and confinement rules are untouched.
- **Information.** The refusals quote the resolved path, as the existing path refusals do.

## 5. Scope

In: the setting and its resolution (`CheckoutsLocation`), threading it through `ManagedRepoRoot` and its callers, the
`ToolDirectory.Ensure` refactor, hash namespacing, the refusal and notice wording, `ReferenceCheckout`, tests, docs
(`docs/sources/repository.md`, `docs/guides/configuration.md`, `docs/troubleshooting.md`) and a `CHANGELOG.md` entry
under Added.

Out: moving existing checkouts automatically; a per-user default; a builder API for the setting; #479's separate git
directory (already shipped; this composes with it); reading another worktree's configuration; changing where
`selection.json` or `.servicesources\prepare` live.

Overlap: open PR #529 touches `CHANGELOG.md`, `docs/sources/repository.md` and `DeferredCheckout.cs`, all of which this
touches. Merge order is the human's; whichever lands second rebases.

## 6. Testing

Traits per the repo: pure tests need none; tests that create directories get `[Trait("IO", "true")]`.

- `CheckoutsLocation` (pure plus temp dir): unset gives the default layout; absolute and relative values resolve; the same
  AppHost spelled with different case or a trailing separator gives one hash on Windows; two AppHosts give two hashes;
  refusals for a file, the AppHost directory and an ancestor; lazy (resolving the location creates nothing).
- `ManagedRepoRoot` with an external location, plus the existing containment tests still passing for `..` names.
- Two AppHosts, one root (IO): clones do not see each other's `.incoming-*` or locks; sweeping from one leaves the
  other's staging and lock alone; the `<hash>` directory holds `.gitignore` and the barrier files and the root does not.
- `<hash>` as a reparse point or file is refused (reparse test skipped where links cannot be created).
- Borrowing (IO, fake or real git as the existing `ReferenceCheckout` tests do): home under the same external root is
  found; home at its default location is found as the fallback; origin mismatch still refused; a different external root
  falls back to a plain clone.
- Messages: the three refusals name the setting for the default location and say to shorten it for an external one.
- Notice when the setting is external and `.servicesources\checkouts` is non-empty; no notice when empty or absent.
- Separate-git-directory interplay (#519 seam `CloneLayoutOptions`): a long external root still decides separate or
  refused by the real path length.
- **Verify leg for the ticket's own case** (cannot run on the ubuntu CI): on the Windows dev box, an AppHost whose
  directory is about 145 characters, `ServiceSources__CheckoutsDirectory=C:\ss`, a managed service with a deep project,
  and `dotnet run` of that service reaches a running state where the same AppHost without the setting fails with the
  MSBuild "Path too long" error. Result recorded in the PR; a `[SkippableFact]`-style Windows-only test builds a project
  at a checkout path over 260 characters under the external root only if it proves reliable.
- CHANGELOG, docs and the `scripts/smoketest-*.sh` family are unaffected except that the config-layers smoke test should
  gain a case for the new key.

## 7. Open Questions

1. **Prepare markers.** Decision 3 says markers stay with the AppHost, but a managed checkout's marker is inside its own
   git directory and moves with it (3.9). Is that acceptable, or should managed markers also move to
   `.servicesources\prepare`? (Changing it would break "re-clone means re-prepare" unless keyed on checkout identity.)
2. **Seeded relative values.** Should `PathOverrideCheck` warn when a seeded `servicesources.local.json` carries a relative
   `checkoutsDirectory`, or is the docs recommendation of an absolute value enough (3.7)?
3. **Hash length and keeping the `checkouts` segment.** 10 hex and `<hash>\checkouts\<name>` cost 22 characters of the
   path; dropping the `checkouts` segment saves 10 at the price of name collisions with barrier files and staging
   directories (3.1). Confirm the trade, or accept a different one.
4. **Refusing an ancestor of the AppHost** (3.2) also refuses a root such as `C:\src` when the AppHost is under it,
   which a developer might reasonably choose. The alternative is to allow it, since only `<hash>` is written; the cost is
   hash directories appearing among their repositories. Keep the refusal?
5. **Second borrowing candidate** (3.7) adds a lookup at the home's default location. Worth it, or only the same-root
   candidate?
