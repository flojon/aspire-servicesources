# Aspire.Hosting.ServiceSources — Seeding a linked worktree from the main worktree

**Date:** 2026-10-08
**Status:** Draft — not yet reviewed.
**Resolves:** nothing filed yet. Motivated by a direct report: "when switching to a worktree the local
clones are only available on the main branch."

## Motivation

Everything ServiceSources keeps per developer lives beside the AppHost and is git-ignored:

| What | Where |
| --- | --- |
| Managed checkouts | `.servicesources/checkouts/<checkoutName>/` (`LocalGitCheckout.ManagedRepoRoot`) |
| Cold-checkout picks (#271) | `.servicesources/selection.json` (`SourceSelectionStore`) |
| Prepare markers | `.servicesources/prepare/<service>.json` (`PrepareMarker`) |
| Developer config | `servicesources.local.json` (`DeveloperConfiguration.FileName`) |

`git worktree add` materializes only tracked files, so an AppHost run from a linked worktree of the
AppHost's repository starts from nothing: no source choices, a fresh source-pick prompt, and a full
network clone of every `repository` service. In practice developers either give up on worktrees for
AppHost work or hand-copy files between them.

## Goals

- A first run in a linked worktree behaves like the main worktree's last run: same developer config,
  same picks, checkouts already present (or cheaply obtained).
- The worktree stays isolated afterwards. A checkout in the worktree can sit on a different ref,
  carry different edits and build independently of the main worktree's checkout of the same service.
- No new configuration surface.

## Non-goals

- **Sharing one set of checkouts between worktrees.** Two AppHosts reconciling the same checkout
  onto different refs, building into the same `bin/obj`, and seeing each other's edits is the
  failure mode the per-service-checkout design
  (`2026-08-16-servicesources-shared-repo-cache-design.md`) already rejected. A developer who
  wants it anyway points the service at the main worktree's checkout with the existing `path` source
  in the worktree's `servicesources.local.json`; that is the escape hatch, and this design adds no
  other.
- **Carrying uncommitted work across.** Seeding copies git history, not working-tree state.
- **Keeping worktrees in sync.** Seeding happens once; afterwards the worktree's files are its own.
- **Bare-repository layouts** (`git clone --bare` + only linked worktrees). There is no main
  worktree to seed from, so nothing happens — the same as today.

## Findings that constrain the design

**F1 — `.servicesources/` is resolved from `AppHostDirectory` only.** `ToolDirectory.PathIn` is a
pure function of the AppHost directory, and every consumer goes through it. Nothing today asks git
about the AppHost's own repository.

**F2 — The managed checkout path must stay worktree-local and pure.** `ManagedRepoRoot` is frozen
into DCP's executable spec before the clone happens (the reason deferral works at all). Seeding must
not change *where* a checkout lives, only *how* it is filled.

**F3 — Linked-worktree checkouts are refused.** `CloneIntoPlace` throws for a `.git` *file*. So
"seed with `git worktree add` from the home checkout" is out, besides hitting git's rule that a
branch may be checked out in only one worktree.

**F4 — Alternates are fragile.** A plain `git clone --reference` leaves `objects/info/alternates`
pointing at the home checkout; a `git gc` or deletion there corrupts the worktree's clone. The
shared-repo-cache design rejected alternates for this reason. `--dissociate` copies the borrowed
objects in and removes the alternates file, which keeps the speed-up without the dependency.

**F5 — A local-path clone gets the wrong refs.** `git clone <home checkout>` makes the *home
checkout's local branches* into the new clone's `origin/*`. Re-pointing `origin` and fetching does
not prune them cleanly. `--reference … --dissociate` against the *real* URL takes refs from the real
remote and only borrows objects, so the result is indistinguishable from a normal clone.

**F6 — Developer config is read in exactly one place.** `DeveloperConfigFileSource.Register` reads
`<AppHostDirectory>/servicesources.local.json` once per builder, before anything resolves. Selection
is read in `SourceSelectionStore.Read` from the `BeforeStartEvent` subscriber. Both happen before
any clone, so seeding the two files up front is early enough.

**F7 — Prepare markers describe the working tree they sit beside.** A fresh clone has no
`node_modules`, `target/` or similar, so copying home's marker would wrongly skip `prepare`. Markers
are not seeded.

## Design

### Finding home

A new internal `WorktreeHome.TryResolve(builder)` returns the home AppHost directory, or `null`:

1. **Filesystem pre-check, no git.** Walk up from `appHostDirectory` to the nearest ancestor with a
   `.git` entry, the same walk `PathSource.ConfinementRootOf` does. No entry, or a `.git`
   *directory*: return `null`. Only a `.git` *file* (a linked worktree, a submodule, a
   `--separate-git-dir` clone) goes on to git. The main worktree, plain clones and non-repository
   AppHosts therefore never spawn a process.
2. `git rev-parse --path-format=absolute --git-dir --git-common-dir --show-toplevel` from
   `appHostDirectory`. If git fails for any reason (git missing, older than 2.31 and so rejecting
   `--path-format`, not a repository), return `null`. If git-dir equals common-dir, this is a
   submodule or a `--separate-git-dir` clone: return `null`.
3. `git worktree list --porcelain`. The first record is the main worktree. If it carries `bare`,
   return `null`.
4. `rel = Path.GetRelativePath(worktreeTop, appHostDirectory)`;
   `home = Path.Combine(mainTop, rel)`. If `home` does not exist, return `null`.

Every linked worktree shares one common dir, so home is always the main worktree, including for a
worktree created from another linked worktree. Paths are compared after `Path.GetFullPath`,
case-insensitively on Windows.

The result is cached per builder in a `ConditionalWeakTable`, like the rest of the per-builder
state, so config seeding and every clone share one resolution.

**Where git comes from.** `DeveloperConfigFileSource.Register` has no `IGitClient`; the clients live
inside the source registry. Resolution therefore does not go through `IGitClient`. It goes through a
small internal `IWorktreeProbe` with one method returning
`(GitDir, CommonDir, Top, MainTop, MainIsBare)?`. Its production implementation runs the two
commands above through the same process plumbing `GitCliClient` uses.
`WorktreeHome.UseProbe(builder, probe)` replaces it for one builder, and must be called before the
builder's first entry point. Tests that exercise seeding use that. Every other test is isolated by the
pre-check: test AppHost directories live under the temp directory, have no `.git` ancestor, and so
never consult the probe. A test that puts its AppHost directory inside this repository must
install a probe that returns `null`. Otherwise, run from a linked worktree, it would seed from the
developer's real main worktree.

### Seeding config files — once per worktree

`WorktreeSeed.EnsureSeeded(builder)` runs at the top of `DeveloperConfigFileSource.Register`
(F6), before the file is read:

1. If `.servicesources/worktree-seed.json` exists in the worktree, return. Seeding has happened, and
   a file the developer has since deleted stays deleted.
2. Resolve home. If `null`, return without writing a marker. For the main worktree and non-git
   AppHosts this is the filesystem pre-check alone. A linked worktree whose home lacks the AppHost is
   re-checked next run, at the cost of two git calls.
3. For each of `servicesources.local.json` and `.servicesources/selection.json`: if absent in the
   worktree and present (as a regular file, not a reparse point) in home, copy it with
   `FileMode.CreateNew`. An existing worktree file is never overwritten, and a concurrent AppHost's
   copy wins the race harmlessly.
4. `ToolDirectory.Ensure`, then write the marker recording the home path and which files were
   copied. The marker is written even when nothing was copied: seeding is a once-per-worktree
   event, not a watch on home.

`ToolDirectory` says an AppHost with nothing to keep should not acquire `.servicesources/`. Seeding
departs from that only in linked worktrees whose home was resolved. The marker is what keeps every
later run free of git calls, which matters more there than one ignored directory.

**Never fails the run.** `Register` deliberately throws on a malformed file so the next entry point
retries. Seeding must not join that. Everything in `EnsureSeeded` is wrapped. A copy or marker
write that fails with `IOException` or `UnauthorizedAccessException` (a locked file, a read-only
directory) is reported as a notice and skipped, and no marker is written, so the next run tries
again. A probe failure is already `null`.

**Reporting.** `Register` runs while the AppHost is being composed, before any `ILogger` exists.
Every message below goes through `ServiceSourcesWarnings.For(builder).AddNotice(…)`, which buffers
and flushes at `BeforeStartEvent`, like every other build-time notice. There is one notice listing
each copied file and the home path it came from, and one per suspect `path` entry (below).

Copying rather than reading through to home keeps the worktree independent: editing the worktree's
config does not touch main's, and the developer can see and change exactly what was inherited.

**Developer path overrides.** `path.path` names a directory resolved against the AppHost
directory. The deprecated `repository.path` still resolves one the same way, so it is checked as an
alias of `path.path` for as long as it is read; it gets no logic of its own and drops out when it is
removed. (`repositoryRoot` is read only where no `.git` is found, so it never applies in a
worktree.) After copying `servicesources.local.json`, each such value is resolved twice, from home
(`Rh`) and from the worktree (`Rw`):

| Case | Example | Outcome |
| --- | --- | --- |
| `Rh == Rw`, outside `mainTop` | absolute `D:\src\orders`; `../../orders` from sibling worktrees at equal depth | The developer's own external checkout, used by both, as configured. Silent. |
| `Rh` inside `mainTop` and `Rw` inside `worktreeTop`, at the same relative position | `../services/orders` | Each worktree uses its own copy: the monorepo case. Silent. |
| `Rw` inside `mainTop` but not inside `worktreeTop` | absolute path into the main worktree, including its `.servicesources/checkouts/x` | The worktree would build and edit main's files: the sharing the non-goals reject. Warn. |
| any other `Rh != Rw` | `../../other-repo` from a worktree nested in main, or at a different depth | Points at a missing directory or, worse, a different real one. Warn. |

The warning cases produce one notice per entry. It names the service, the key, the value as
written, and both resolved paths, and it says to edit the worktree's `servicesources.local.json`.
Seeding never rewrites or suppresses the entry. Rewriting would lose comments and formatting, and
suppressing would make the worktree do something its own file does not say. The worktree's file is
the truth, and the notice is how the developer learns it needs attention.

"Inside `worktreeTop`" is what keeps a worktree nested in main (`<main>/.worktrees/x`) out of the
third row: its own files are inside `mainTop` too, but they are its own.

The check runs once, on the entries seeding just copied, and never again. So the deliberate escape
hatch from the non-goals stays quiet: a `path` into the main worktree's checkout that the developer
writes into the worktree's own file afterwards is not inspected.

### Seeding checkouts — at clone time

`IGitClient` gains a default interface method
`CloneWithReference(url, destination, referenceRepository, progress) => Clone(url, destination, progress)`.
`GitCliClient` overrides it to add `--reference-if-able <path> --dissociate`. A default method rather
than a new parameter on `Clone` keeps the eleven private test fakes, across the core, Java and
JavaScript test projects, compiling unchanged.

`--reference-if-able` rather than `--reference`: if home's checkout disappears between the probe and
the clone, git warns and clones normally instead of failing. That removes the most likely reference
failure without needing a fallback.

`--dissociate` repacks every borrowed object into the new clone before removing `alternates`. So
the saving is network transfer, not disk or local time. A large repository still costs a full local
object copy, at disk speed rather than hardlink speed.

`CloneIntoPlace` asks for a reference only when all of these hold:

- home resolved, and `<home>/.servicesources/checkouts/<checkoutName>` has a `.git` *directory* (F3
  in reverse: a home checkout that is itself a worktree or separate-git-dir clone is not borrowed
  from);
- that checkout's origin satisfies `RepositoryUrlsMatch` against the catalog URL, so an unrelated
  repository squatting at the same name is never borrowed from.

The clone still goes to the real URL into the usual scratch directory and is moved into place by the
existing logic, so concurrency, debris sweeping and the post-clone decision are unchanged. Reconcile
then checks out the configured ref as usual.

Seeding must never be what fails a run, but a blanket retry would make every network failure in a
worktree cost two full attempts. So the fallback is narrow. `GitCliClient.CloneWithReference`
classifies its own failure. If git's stderr names the reference path, or reports an alternates or
object-borrowing error, it throws `GitReferenceFailedException`; anything else surfaces exactly as
`Clone` would surface it. `CloneIntoPlace` retries as a plain clone only on
`GitReferenceFailedException`. Authentication and network failures propagate unchanged after one
attempt.

The fallback is reported where the clone's progress already goes, not through
`ServiceSourcesWarnings`. That buffer is written only at `BeforeStartEvent`, and a deferred
checkout clones after it, so a notice added there would never appear. The line goes to the same
`IGitProgressSink` the clone reports to, which for a deferred checkout is the service's resource log.
With no sink (`progress` is `null`) it goes nowhere: the clone still succeeded.

The retry clones into a **fresh** scratch directory (a new `.incoming-<checkout>-<guid>`) inside the
same `try`/`finally`, and that `finally` deletes both scratch directories. Any it cannot delete are
left to the next run's debris sweep, like any other abandoned scratch.

Because only objects are borrowed, the network round-trip remains: the clone negotiates with the
remote and downloads only what home lacks. Offline seeding is out of scope.

### Interaction with existing behaviour

- **Cold-checkout gate (#271).** `selection.json` is seeded before the gate reads it, so the
  worktree's first run honours main's picks instead of prompting again. Undecided services still
  prompt.
- **Shared repositories (#291).** Seeding is keyed by `CheckoutName`, the same key home's directory
  uses, so grouped services borrow from the one shared checkout.
- **Build barrier, prepare.** Untouched. The worktree's checkout is a normal managed checkout with
  no prepare marker, so `prepare` runs on first use (F7).
- **Main worktree and non-git AppHosts.** `TryResolve` returns `null` and nothing changes.

## Testing

Real git, against the existing `sample-service.git` fixture:

- **Home resolution:** a linked worktree resolves to the main worktree's AppHost path; the main
  worktree, a non-repository directory and a bare-repository layout resolve to `null`; a worktree of
  a worktree resolves to the main worktree; a worktree nested inside the main worktree
  (`<main>/.worktrees/x`) resolves to the main worktree.
- **Pre-check:** the main worktree and a non-repository directory resolve without the probe being
  called (a recording probe asserts zero calls).
- **Config seeding:** files copied when absent; never overwritten when present; marker stops a
  second seed after the developer deletes a copied file; marker written when home resolved but had
  nothing to copy; no marker written when home is absent.
- **Seeding failure:** a home file that cannot be read (held open exclusively) produces a notice,
  no marker and no exception out of `Register`; the next builder seeds successfully.
- **Notices:** copied files and warnings arrive through `ServiceSourcesWarnings`, not a logger.
- **Path-override notices,** one test per row of the table, against `path.path`: silent for an
  absolute external path and for an in-repo relative path; warns for an absolute path into the main
  worktree; warns for an escaping relative path from a worktree nested in main, where it resolves to
  a real but different directory; silent for an in-repo relative path from a worktree nested in
  main. One more test shows that a deprecated `repository.path` into the main
  worktree warns the same way.
- **Checkout seeding:** a cold checkout in a worktree with a matching home checkout clones with a
  reference, ends with no `objects/info/alternates`, `origin` is the catalog URL, and the
  remote-tracking refs match the fixture rather than home's local branches.
- **Not borrowed:** home origin mismatch; home checkout with a `.git` file.
- **Fallback:** a fake throwing `GitReferenceFailedException` is retried as a plain clone into a
  different scratch directory, no `.incoming-*` debris remains, and the fallback line reaches the
  progress sink; a fake throwing a plain clone
  failure is attempted once and propagates; `GitAuthenticationFailedException` likewise.
- **Reference vanished:** home's checkout deleted after resolution; `--reference-if-able` still
  produces a normal clone (real git).
- A fake that overrides `CloneWithReference` records the reference, so unit tests can assert when it is or is not passed.

## Documentation

`docs/sources/repository.md` gets a short "Working in git worktrees" section: what is seeded, that it
happens once, that checkouts are independent afterwards, and the `path` source as the way to share
the main worktree's checkout deliberately.

## Open questions

1. **Seed `backingServices` config?** It lives in the same `servicesources.local.json`, so it comes
   along. Nothing else to do unless backing services grow their own state under `.servicesources/`.
2. **Should seeding be opt-out?** Proposed: no switch. Deleting the marker re-seeds, and deleting a
   seeded file keeps it deleted. Revisit if someone needs to suppress it.
3. **Home gains config after the worktree's first run.** The marker is written even when nothing
   was copied, so a `servicesources.local.json` created in home later is never picked up. Proposed:
   accept it; deleting the marker re-seeds. The alternative, re-seeding until something was copied,
   reintroduces git calls on every run of a worktree whose home has no config.
