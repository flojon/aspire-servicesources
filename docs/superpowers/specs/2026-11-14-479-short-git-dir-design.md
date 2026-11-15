# Keeping a managed clone's git directory short on Windows (#479)

**Date:** 2026-11-14
**Status:** Approved
**Resolves:** #479 (a managed `repository` clone is refused on Windows once the AppHost directory passes
about 129 characters, because git cannot write a pack file past MAX_PATH).
**Relates to:** #474 / #483 (the pack-path check), #477 (its refusal message), #488 (the repack temp file
sizing), #466 (`core.longpaths`), #475 (relocating all checkouts; not done here).
**Measured against:** git `2.52.0.windows.1`, Windows 11, `LongPathsEnabled=0`, `core.longpaths=true`,
a throwaway one-commit repository cloned with `--dissociate --reference-if-able` and
`--separate-git-dir`. The probe was deleted afterwards; section 2 is the record.

## 1. Problem and what the code does today

`LocalGitCheckout.CloneIntoPlace` clones into `<AppHost>\.servicesources\checkouts\.incoming-<8 hex>`
(`ScratchDirectory`), then `Directory.Move`s it to `checkouts\<name>`. Before cloning,
`PackPathLimit.EnsureFits` throws when `max(checkout, staging).Length + 85 > 259`, where 85 is
`\.git\objects\pack\.tmp-<10 digit pid>-pack-<40 hex>.pack`. That refuses every AppHost directory over
about 129 characters. The pack file lives under `<checkout>\.git`, so the only thing that has to be short
is the git directory.

The ticket proposes `git clone --separate-git-dir=<short>`. This spec settles the design questions the
ticket lists and corrects three claims it makes about the code:

- **There is no `core.longpaths` backfill to adapt.** The ticket lists "the `core.longpaths` backfill
  (`git config --file`)" among the code that assumes `.git` is a directory. `main` has no such code:
  `GitCliClient.PlatformOptions()` passes `-c core.longpaths=true` after `clone`, which persists it into
  the clone's own config, and nothing rewrites an existing checkout's config. `PersistLongPaths`
  appears in #483's description but not in the tree. A separate git dir's config is written by the same
  clone, so nothing needs adapting.
- **Reconcile does not read `.git` by path.** `UseExistingCheckout` only calls `git` with `-C <checkout>`
  (`GetOriginUrl`, `HasUncommittedChanges`, `IsRefCheckedOut`, `Checkout`, `Fetch`), which follows a
  `.git` file. What assumes a directory is listed in section 5.
- **Pack paths stay short, but the working tree does not stay unlimited.** See section 2.

## 2. What a separate git directory does and does not buy (measured)

| Staging root length | `clone --dissociate --separate-git-dir` | then `status`, `repack -a -d`, `fetch` |
|---|---|---|
| 195, 235 | ok | ok, no warnings |
| 250, 253 | ok | ok; `repack`/`status` warn `unable to access '.gitattributes': Filename too long` |
| 265, 295 | fails: `fatal: this operation must be run in a work tree`, `Clone succeeded, but checkout failed` | n/a |

- The pack and repack temp file were never the problem once the git directory is short: `repack -a -d`
  and `fetch` succeed at 250 characters where an in-place clone is refused at 175 (`259 - 85`).
- The **working tree root** has its own ceiling, near 250 characters here: git changes into the
  directory, and a directory path past about 248 characters cannot be a current directory on Windows
  without the long-path opt-in. It is not "no matter how deep" as the ticket says. The ticket's note of
  files up to 312 characters verified under `core.longpaths` is about file depth below a shallower root,
  not about the root.
- `.git` file written by `--separate-git-dir` is `gitdir: <absolute path, forward slashes>`.
  `core.worktree` is **not** set in the git directory's config, and a `Directory.Move` of the working
  tree afterwards leaves `git -C <new path> status` working with no config change. Both were checked
  with git 2.52; the implementation must re-check them in its Windows test, because they are git
  behaviour this package does not control.

So the feature raises the longest usable staging root from 174 to 235 characters. That is an AppHost
directory of about 190 characters for a checkout name of 18 characters or fewer (`235 - 27 - 19`, where 27 is
`\.servicesources\checkouts` and 19 is `\.incoming-<8 hex>`); a longer checkout name makes the final path the
longer one and lowers it by the difference. A refusal remains beyond that (decision D1). The first
warnings were seen at 250 and the first failure at 265, so 235 leaves about 15 characters of margin on purpose.

## 3. Decisions

### D1. When: only when the in-place pack path would not fit

**Decision:** a clone is in place (today's layout, a `.git` directory) whenever `PackPathLimit` says the
pack path fits. Only when it does not fit, on Windows, the clone uses `--separate-git-dir`.

**Why not always on Windows:**

- Every working Windows checkout stays exactly what it is. The ticket's migration requirement ("working
  checkouts that don't need to move") is then true by construction, with no code to move anything.
- Most AppHosts are shallow. Always-on would give each of them a git directory in
  `%LOCALAPPDATA%` that outlives a deleted checkout, plus a sweep problem (section 6), for no gain.
- `ReferenceCheckout`, `PrepareMarker` and `GetHeadCommitSha` keep their common case unchanged.
- The cost is two layouts, so the separate one needs a test path that runs on Linux CI. Section 8 covers it.

**How the #477 refusal changes.** `EnsureFits` stops being a refusal and becomes the *decision*:

| Condition on Windows (`max(final, staging)` = `L`) | Result |
|---|---|
| `L + 85 <= 259` | in place, as today |
| `259 - 85 < L <= 235` | separate git dir |
| `L > 235`, or the git root cannot be used (see D2) | refusal, reworded |

The refusal stays a `ServiceSourcesConfigurationException`, still before anything is downloaded, still
naming the length and how many characters to remove. Its content changes: it no longer cites the pack
file, it cites the working-tree ceiling (`L > 235`), and the "move the AppHost" advice is the same. The
235 is the largest staging root that was clean in section 2 with margin below the first warnings at 250.
It is a constant (`MaxWorkingTreeRootLength`) with that measurement as its comment. `PackPathLimit` gains a
second suffix, `LongestPackFileInGitDir` (`\objects\pack\.tmp-<10>-pack-<40>.pack`, 80 characters), built from the
same pieces as `LongestPackFile` rather than hard-coding `\.git`; `PackPathLimitTests`, which assert 85 and
174, are rewritten around the three-way decision, not merely extended. When the git root is
unusable (D2) the message says so and gives today's pack-path numbers. Non-Windows behaviour is
unchanged: no check, no separate git dir.

### D2. Location and naming

**Root:** `Path.Combine(Environment.GetFolderPath(SpecialFolder.LocalApplicationData), "ServiceSources", "git")`.
Not configurable: no environment variable, no config key, no public API. Tests inject the root through an
internal parameter. A setting would be a second place for a path-length mistake and a way to point the
tool at a directory it then deletes from (section 4).

**Budget:** a git directory is `<root>\<16 hex>-<8 hex>.git` (29 characters), and its deepest pack file is
`\objects\pack\.tmp-<10>-pack-<40>.pack` (80 characters) below it, so the root must be at most 149
characters. A longer root, or an empty `LocalApplicationData` (some service accounts), counts as "git
root cannot be used" in the D1 table.

**Name:** `<hash>-<id>.git`.

- `<hash>` is the first 16 hex characters of SHA-256 over the **final** checkout path, full path, trailing
  separators trimmed, upper-cased with the invariant culture (Windows paths compare ignoring case). It
  groups the git directories one path has had, which is what the sweep and a person diagnosing need.
  It is not an identity: two spellings of one path (a junction, `subst`) hash differently and still work,
  because the `.git` file, not the name, is how a checkout finds its git directory.
- `<id>` is the 8-hex staging id `ScratchDirectory.Create` already picked for this attempt. It makes the
  name unique per clone attempt, so two AppHosts cloning the same final path at once never share a git
  directory (a shared name would make the second `git clone` fail on a non-empty directory, or worse
  succeed into the first's files). The loser's attempt is discarded whole, like today.

The git directory is cloned to its final name straight away, so nothing renames it. Only the working tree
is renamed (`Directory.Move(scratch, repoRoot)`), which section 2 showed survives.

### D3. Staging and the rename

- `ScratchDirectory` owns the `.incoming-<id>` directory and its `.incoming-<id>.lock`. It gains an
  optional paired git directory `<git root>\<hash>-<id>.git` and a second lock file `<hash>-<id>.git.lock` in
  the **git root**, held open with no sharing for the same lifetime, so a sweep run from any other AppHost
  can tell a clone in progress from an abandoned one. `Dispose` removes the pair and both locks. If the
  reference-checkout retry in `CloneIntoPlace` disposes and recreates the scratch, the new attempt has a new
  id and so a new git directory name, computed from that id.
- On success the working tree moves and the git directory stays. Disposal must therefore be told the git
  directory was adopted (so it removes it only when the clone was abandoned: concurrent loser, failed
  clone, failed move).
- `gitClient.Clone` / `CloneWithReference` take an optional `separateGitDir` and add
  `--separate-git-dir=<path>` after `clone`. It is passed as one argument (`--separate-git-dir=<path>`),
  never split, so a path cannot become an option. It is a path this package computed, never user input
  (the checkout name is already contained by `IsContainedCheckoutDirectoryName`).
- The ownership record (D4) is written into the git directory **before** the working tree is moved, naming
  the final path. A crash between the two leaves an unreferenced git directory the sweep collects, never a
  checkout whose `.git` file points at a git directory with no record (which D6 would refuse for good).

### D4. Ownership record and cleanup

Each separate git directory carries `servicesources-owner.json`:
`{ "checkout": "<final path>", "createdUtc": "...", "lastUsedUtc": "..." }`.

A git directory is **live** when the `.git` file at `checkout` resolves (D6 rules) to that directory.
Cleanup:

1. **Abandoned staging:** `ScratchDirectory.SweepAbandoned` already removes `.incoming-*` directories whose
   lock is free. It takes the git root and, in the branch where it removes an existing `.incoming-<id>`
   directory, also removes every `*-<id>.git` child of the git root and its `.lock`. It never removes a git
   directory in the orphan-lock branch (`SweepOrphanLock`: a lock with no staging directory is the state
   after a successful move followed by a crash, where the git directory is live).
2. **Orphans** (retention decided, Open Question 1): a new `SeparateGitDirectories.SweepOrphans`
   runs at the same point (`CloneIntoPlace`, only for a clone that will use a separate git directory, so a
   shallow AppHost pays nothing). It enumerates direct children of the git root that match the strict
   pattern `^[0-9a-f]{16}-[0-9a-f]{8}\.git$` and are not reparse points, and for each:
   - skip it when its git-root `.lock` exists and cannot be opened exclusively (a clone in progress, in any
     AppHost); a lock that can be opened is stale and is removed with that child;
   - skip it when it is live (D6 resolves from the recorded checkout back to it);
   - skip it when the recorded checkout's drive root does not exist, and when the recorded path is not a
     rooted local path (a UNC or relative value is never probed);
   - with a parsable record: skip it while `lastUsedUtc` is within the retention window;
   - with no record or an unparsable one: skip it while the directory's own last-write time is within the
     retention window. Afterwards it is deletable, which is safe because D6 can never adopt such a
     directory;
   - otherwise delete it recursively (never following a reparse point, never with a path taken from the
     record) and log one line to the clone's progress sink, like the staging sweep does.
3. **Refresh:** a live git directory's `lastUsedUtc` is rewritten when more than a day old, at reconcile
   (`UseExistingCheckout`). The record is always written to a temp file and moved over the old one, so a
   concurrent reader sees a whole record or the old one. This keeps a checkout in use through this tool
   from being swept. A checkout whose AppHost directory is **moved or renamed** is not re-adopted (D6 rule
   4); see there.
4. **Re-clone of a path:** nothing special. The old git directory no longer resolves from its checkout, so
   rule 2 collects it after the retention window.
5. **Checkout deleted by the tool:** the tool deletes a checkout directory in exactly one place
   (`CloneIntoPlace` removing debris with no `.git` entry). A directory with no `.git` has no git
   directory, so there is nothing to clean there.
6. **Best effort:** every sweep step swallows `IOException` and `UnauthorizedAccessException`, like
   `ScratchDirectory.SweepAbandoned`. A sweep must never fail resolution.

Disk cost, stated plainly: each separate git directory is a full clone, and a worktree tool that makes a new
deep path per task makes a new one per path, which stays until the retention window passes.

**Retention is 14 days** without a `lastUsedUtc` refresh, and the orphan sweep deletes (Open Question 1, resolved
below). Everything in D4 is decided.

### D5. Existing checkouts and migration

- A working in-place checkout is never touched. `PrepareRepoRoot` already returns an existing `.git`
  directory untouched, and D1 only decides for a new clone.
- Decided (Open Question 2, resolved below): a deep checkout that already exists as an
  in-place clone and now fails in `fetch`/`gc` is **not migrated automatically**. Moving a git directory under a developer's open IDE is a mutation of
  their working checkout, and `git init --separate-git-dir` on a repository whose own paths already
  exceed the limit is not something this package can promise to complete. Instead reconcile logs a notice
  when it finds an in-place checkout whose pack path would not fit (`PackPathLimit` on its path),
  saying to delete `checkouts\<name>` and re-run so it is cloned again with a separate git directory.
  `checkouts` is tool-owned (docs/sources/repository.md), so deleting it is the documented remedy for
  other states too.
- A deep AppHost that was refused by #477 has no checkout at all, so it simply clones under the new rule.

### D6. Recognising a checkout that has a `.git` *file*

Today a `.git` file is refused as "not a checkout this tool cloned" (`CloneIntoPlace`, the
`File.Exists(.git)` branch) and `PrepareRepoRoot` ignores it (`Directory.Exists(.git)` is false, so it
tries to clone and hits that refusal). A new helper `ManagedGitDirectory.Resolve(repoRoot)` returns the
git directory for either shape:

- `.git` is a directory: that directory.
- `.git` is a file: parse its single `gitdir:` line; accept it **only** when all of these hold:
  1. the file is at most 4 KiB and has exactly that one line;
  2. the target, made absolute (git writes forward slashes) and normalised, is a **direct child** of the
     git root (no `..`, compared case-insensitively on Windows) and its name matches the strict pattern
     from D4;
  3. the target is a real directory and none of it, the git root and the git root's ancestors up to
     `LocalApplicationData` is a reparse point (above that is the user's own profile, inside the trust boundary);
  4. its `servicesources-owner.json` parses and its `checkout` equals `repoRoot` (same comparison).
  Anything else returns `null`, and the caller keeps today's refusal for a `.git` file, with a message naming
  the specific reason: a record naming another checkout path (the AppHost directory or worktree was moved or
  renamed, so the checkout is refused rather than re-adopted: delete it and re-run), an unreadable record, a
  target outside this machine's git root (another Windows account, an elevated process, a different
  `LOCALAPPDATA`, or WSL: the message names both roots), or a git directory that is gone.

The resolver is the single place that answers "is this `.git` entry one of ours", and every site in
section 5 calls it instead of testing `Directory.Exists(.git)`.

When the git root is unusable (D2), a checkout that already exists as a separate git directory still resolves
if rules 1-4 hold; the root is only a requirement for creating one.

## 4. Attack surface

This design makes the package create, read and delete directories **outside** the AppHost tree, and read
a file inside a checkout to decide where git runs. Each trust edge:

| Edge | What is trusted | Control |
|---|---|---|
| `.git` file in `checkouts\<name>` names a git dir | The file is developer-owned, but the folder can arrive by copy, zip or a shared drive. Git runs hooks, `core.fsmonitor` and `core.sshCommand` from whatever git directory it is pointed at, so a hostile `gitdir:` is code execution. | D6: only a direct child of our git root, strict name, non-reparse, owner record naming this checkout. Anything else is refused before any `git` runs in the checkout. |
| Git root `%LOCALAPPDATA%\ServiceSources\git` | Per-user directory, default ACL is the user only. Another process as the same user is already inside the existing trust boundary (it could edit the checkout and the AppHost). | No new network or elevated surface. The root is created with default ACLs, never world-writable. |
| `servicesources-owner.json` content | Untrusted data inside a directory we may delete. | Never used to build a deletion target. Sweep deletes only the child it enumerated, found by the strict name pattern, never a path read from the file. The file's `checkout` is used only for `Exists`/equality checks. A malformed or missing record never names a target; the directory is judged by its own last-write time under the retention window (D4.2), so a corrupted record cannot trigger an early removal. (An unfinished clone is also covered by the lock-based staging sweep.) |
| Sweep deleting recursively | A reparse point inside the git root could redirect a recursive delete. | Children that are reparse points are skipped; delete never follows one. |
| Shared git root across AppHosts and users of the machine | One AppHost's sweep can delete another AppHost's git directory. | Live test (resolves from the checkout back), lock test, drive-present test and retention; nothing is deleted on a read error. |
| Credentials | The clone's config may carry the origin URL. | Unchanged in kind: it was in `<checkout>\.git\config`; it now sits in a per-user directory. The token is injected through the credential helper environment, not written. |
| `.git` entry no longer inside `.servicesources` | `.servicesources\.gitignore` and the `CheckoutBuildBarrier` files protect the AppHost repository from checkouts. | Not affected: the working tree stays under `.servicesources`. A git directory outside it cannot show in the AppHost's status. |
| Borrowing objects (`--reference`) from the home worktree's checkout | Objects come from a directory this tool does not control. | `ReferenceCheckout` resolves through D6 (or an in-place `.git` directory) and still requires an origin match. A `.git` file that D6 rejects means "do not borrow", not an error. |

Delete helpers that clear read-only attributes are only ever handed a path confirmed, by the strict name
pattern, to be a direct child of the git root.

Nothing here executes content from the new directories beyond what git itself already executes from a
clone's `.git`, and the owner record is parsed with `System.Text.Json` into a record, like
`PrepareMarker`.

## 5. Code that assumes `.git` is a directory

All sites found by searching `.git` in `src/Aspire.Hosting.ServiceSources`:

| Site | Today | Change |
|---|---|---|
| `LocalGitCheckout.PrepareRepoRoot` (existing checkout test) | `Directory.Exists(.git)` | `Resolve(repoRoot) is not null`, so a managed separate checkout is adopted like any existing one |
| `CloneIntoPlace` `.git` file refusal | any `.git` file refused | refuse only when `Resolve` is null; message for the git-dir-missing case |
| `CloneIntoPlace` post-clone check (it also guards the debris `Directory.Delete`) and the post-`Move` check | `Directory.Exists(.git)` at two places | same helper; a `.git` file that is not ours is never deleted (kept refusal) |
| `LocalGitCheckout.ReferenceCheckout` | `.git` directory only, because git refuses a linked-worktree reference | accept an in-place `.git` directory, or a `Resolve` result; pass the resolved git directory to `--reference-if-able`. The ticket suggests `git rev-parse --git-dir`; `Resolve` is used instead because it avoids running git on a `.git` file nobody has vetted. A separate git directory is a full repository, not a linked worktree |
| `PrepareMarker.LocationFor` (managed) | `repoRoot\.git\servicesources-prepare.json` | the resolved git directory, or `repoRoot\.git` when that is a directory. When `.git` is a file `Resolve` rejects there is no marker location: `Read` returns null and `Write` is skipped (already best effort), so the step just runs. `LocationFor` gains one small file read. The marker moves with the git directory, so a re-cloned checkout starts without one (new id). The `PrepareMarker` doc comment and the comment above the `.git`-file refusal in `CloneIntoPlace` call a `.git` file a shape this tool refuses and are updated |
| `GitCliClient.GetHeadCommitSha` | accepts a `.git` directory or file | none: it is only reached after `PrepareRepoRoot` has run `Resolve` (`CheckoutPreparation` decides after reconcile). No git command runs in a `.git`-file checkout before `Resolve` accepts it; that is enforced in `PrepareRepoRoot`, not in the clients |
| `UseExistingCheckout` / `Checkout` / `Fetch` / `GetOriginUrl` | `git -C <checkout>` | none; git follows the `.git` file |
| `PathSource` / `WorktreeHome` | accept a `.git` file | none; they find the AppHost's repository, not a managed checkout |
| `PackPathLimit` | the only check | split into "fits in place" and "ceiling", D1 |
| `core.longpaths` backfill | does not exist | nothing to change |

## 6. Repack and gc

`clone --dissociate` repacks into the staging git directory, and `gc --auto` repacks into the checkout's
git directory. Both write under the git directory, which is now short, so both are covered. This is
measured in section 2 (`repack -a -d`, `fetch`); the implementation repeats it with the real code path in
a Windows test, with a checkout whose in-place pack path would not fit.

## 7. Scope

In: separate git directory on Windows when needed, its location, naming, ownership record, sweep,
resolver, the sites in section 5, the reworded refusal, `docs/troubleshooting.md`, the `repository`
source doc's paragraph on checkouts, a `CHANGELOG.md` entry under Changed.

Out: relocating checkouts (#475); migrating an existing in-place checkout; a setting for the git root;
any non-Windows behaviour; any public API (nothing in `PublicAPI.Unshipped.txt`).

## 8. Testing

All new test classes carry `[Trait("IO", "true")]`; those where two clones, a sweep and `Resolve` race
(two AppHosts on one final path) also carry `Concurrency`.

- Cross-platform, runs on Linux CI: a decision seam `CloneLayout` (in place or separate, from lengths and
  a git root) with a table test of D1; the separate layout is forced by injection, so the clone, adoption,
  `Resolve` (every reject in D6), `ReferenceCheckout`, `PrepareMarker.LocationFor`, staging cleanup and
  the sweep (live, locked, drive missing, retention, malformed owner, reparse point) run without Windows.
  Real `git` against a local repository, like `LocalGitCheckoutTests`.
- Windows only (skipped elsewhere, run by the implementer, output pasted in the PR): an AppHost directory
  of about 200 characters: clone succeeds with a git directory under the git root, a second run reuses
  it, `fetch`, `gc --auto` and a borrowed-object clone work, a 240-character root is refused with the
  reworded message. This is the "real deep-path test" the ticket asks for; it also re-checks the two git
  behaviours section 2 depends on.
- `ScratchDirectoryTests`: the paired git directory is removed by `Dispose` and the abandoned sweep, and
  not removed when adopted.

## 9. Open Questions

Both resolved by the owner; none remain open.

1. **Retention of an unreferenced git directory (D4). Resolved:** the orphan sweep auto-deletes an unreferenced git
   directory after 14 days without a `lastUsedUtc` refresh. An in-use checkout keeps itself live through the
   refresh.
2. **Existing deep in-place checkouts (D5). Resolved:** notice only; the remedy is to delete the checkout and
   re-clone. There is no automatic migration.
