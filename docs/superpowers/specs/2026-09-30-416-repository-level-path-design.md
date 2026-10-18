# A group-level `path` source for a `repositoryRef` group (#416)

**Date:** 2026-09-30 (reshaped the same day; supersedes the first draft, which built on `repository.path`)
**Status:** Draft (three open questions at the end)
**Resolves:** #416 (`servicesources.local.json` has no way to point a whole `repositoryRef` group at an
already-checked-out working tree in one setting).
**Relates to:** #397 (serializes builds of `"path"` services that share a repository; now applies to the
group, see 2.7) and #291 (the grouping this extends).

---

## 0. Why the first draft was reshaped

The first draft gave `repositories.<name>.path` the meaning of `repository.path`. That mechanism is
**deprecated** (CHANGELOG "Deprecated": use `"source": "path"` with `"path": { "path": "..." }`) and the
maintainer wants it, and its `local.path` alias, to go away. A new spelling on top of it would add a
second thing to migrate. So the group-level override is expressed the way the `path` source is: a
`source` and a `path` block, at `repositories.<name>`.

## 1. Facts this design rests on

Checked against the code on the branch (`e460404`) and `main` (`ac4d802`).

- `RepositoryDeveloperConfig` has `Path` (a `string`), `Ref` and `Prepare`. On `main` `Path` is reserved
  and rejected by `LocalGitCheckout.PrepareRepoRoot`, so **no released behaviour depends on its type**;
  it can be reshaped without a compatibility cost.
- A service's source is one string (`ServiceDeveloperConfig.Source`); `"path"` is resolved by
  `PathSource`, which treats the resolved directory as `repoRoot` and resolves the catalog's `project:`
  (and every non-dotnet kind's paths) against it, confined to it. A service's own override is
  `path.path`: `PathSource.ResolveRepoRoot` reads `config.Path.Path`, else the catalog `path:`.
- `PathSource.Resolve` already receives `repositoryConfig` (unused today).
- `PathSource.SerializeBuild` keys the #397 gate by `BuildGroupKey.For(repoRoot, buildGroup, ...)` =
  the git repository containing the directory. Members resolving to one directory share a key, so
  they are serialized **with no change to the gate**.
- `PathSource.RunPrepareIfDue` already treats a developer `path.path` as "your own tree": the catalog
  step is not run there, only a declared `path.prepare`; a grouped service inherits no catalog
  `repository.prepare` (it is written to run once at the shared root).
- `DeveloperConfiguration.ReadFrom` discards the undeclared repository names on `main`, so a typo'd
  `repositories` entry is silently inert. The branch already keeps them (`UndeclaredRepositoryNames`).
- A `path` service ignores a leftover `repository.ref` (layering rule in `docs/sources/path.md`).

## 2. Decisions

1. **Shape.** A `repositories.<name>` entry gains `source`, and `path` becomes the same block a
   service's `path` is (`PathDeveloperConfig`; only `path` is accepted in it):

   ```json
   { "repositories": { "eshop": { "source": "path", "path": { "path": "../eshop" } } } }
   ```

   Environment spelling: `ServiceSources__Repositories__eshop__Source=path` and
   `ServiceSources__Repositories__eshop__Path__Path=../eshop`. `source` accepts `path` or
   `repository`; `repository` is the explicit "managed clone" (lets a higher layer cancel a lower
   layer's `path`, the same gesture services have). Anything else (`url`, `container`, ...) is a
   validation error: a repository group has no such form. A `path` block without `source: path` is
   not applied, the way a service's block for an unselected source is not read. The flat string form
   `repositories.<name>.path: "..."` is **not** offered (one spelling; it never worked, so nothing
   needs compat).
2. **Meaning: group `source: path` means "wherever a member would use the group's checkout, use this
   directory."** The directory is the group's `repoRoot`: members' `project:` paths (and non-dotnet
   kind paths) resolve against it and are confined to it, unchanged. Value rules are `path.path`'s:
   absolute as-is, relative to the **AppHost directory**, unconfined, must exist. A missing directory
   throws naming the **repository** and key `ServiceSources:Repositories:<name>:path:path`
   (`ResolveDeveloperDirectory` already takes a caller-supplied subject on the branch).
3. **Which members it applies to** (the source-selection seam). Per member, first that applies:
   a. its own `path.path` (source `path`) or deprecated `repository.path`/`local.path`: wins, resolved
      as today; siblings still get the group's;
   b. effective source `repository` or `path` **and grouped**: the member is resolved by `PathSource`
      with `repoRoot` = group directory (a member whose effective source is `repository` is promoted to
      `path`; the catalog `path:` of a `path` member is displaced, developer overrides catalog);
   c. effective source `url`, `container`, `kubernetes` (or not configured): untouched. The group entry
      neither errors nor supplies a missing source. Rationale: developers list every service with
      `source: repository` (there is no other way to be configured, barring `defaultSource`), so a rule
      "member's explicit source beats the group" would make the group override useless.
4. **Ungrouped services** never pick up a group entry: the lookup is by `CheckoutName`, which for an
   ungrouped service is its own name. The group form applies only when `IsGrouped`.
5. **`ref`.** Not an error. Like a `path` service ignoring `repository.ref`, a group `ref` beside
   `source: path` is unread, so a lower layer's `ref` cannot break a higher layer that switches the
   group to `path`. The catalog `defaultRef` is ignored. A member's own `repository.ref` is still
   refused for a grouped service as today.
6. **Unknown names.** Kept from the branch: a `repositories` key naming no declared repository is
   reported by `ServiceConfigAudit` (warning, with a did-you-mean), because a typo'd name silently
   falls back to managed clones. It covers any key under the entry.
7. **Build serialization (#397).** Applies: all members resolve through `PathSource` to one directory,
   hence one `BuildGroupKey`, so their `dotnet` builds run one after another and then concurrently,
   which fixes the `MSB4018`/`CS2012` race for the monorepo case. No gate change; a test pins it.
   This reverses the first draft's "builds are not serialized". `buildGroup` (catalog) still refines
   it. The `prepare` lock already serializes by resolved directory.
8. **Prepare.** Members get `PathSource`'s existing rules: the catalog step is not run in a developer
   directory (a startup notice shows the command to paste), and a member runs only a `path.prepare`
   it declares. A `prepare` under the group's `path` block (and the unread
   `RepositoryDeveloperConfig.Prepare`) is **rejected by the validator** in this ticket, with a message
   pointing to the per-service `path.prepare`: running a group step once per group needs a group-keyed
   marker and is a separate feature (open question 2).
9. **No deprecation notice for a group-sourced directory.** `repository.path` is still deprecated and
   still warns for a member's own; the group form is the recommended replacement, so it warns nothing.
   The `repository.path` notice gains one sentence for a grouped service: "to redirect the whole
   repository at once, set `repositories.<name>` to `{ "source": "path", "path": { "path": ... } }`".
10. **No new warning.** The "declared the same repository but none are grouped" notice only counts
    ungrouped entries; a test pins that a group with a path source produces none.

## 3. Alternatives considered

| Shape | Verdict |
|---|---|
| `repositories.<name>.path` as a flat string, `repository.path` semantics (first draft) | Rejected: builds a second spelling on the mechanism being removed. |
| **`repositories.<name>: { source: path, path: { path } }`** | **Recommended**: same vocabulary as a service; `source` lets layers cancel it; room for more group-level `path.*` fields. |
| `repositories.<name>.path` as a block, no `source` | Rejected: a block that silently changes behaviour breaks "a block is read only for the selected source", and a higher layer cannot cancel it. |
| A member's explicit `source` beats the group | Rejected: every member normally lists `source: repository`, so the override would never fire. |
| Group form overrides all member sources incl. `url`/`container` | Rejected: clobbers a developer's choice to run a dependency from an image. |
| Per-service `path.path` only (status quo) | Rejected: the repetition #416 asks to remove. |

## 4. What changes in the existing code (branch `416-repo-level-path-3510`)

**Keep:** `UndeclaredRepositoryNames` / `RepositoryNames` and `ServiceConfigAudit.OrphanedRepositoryEntriesReason`
with their tests; `ResolveDeveloperDirectory` taking a `Raw subject`; the `ServiceSourcesConfigCache`
no-notice test; the docs/CHANGELOG/plan skeleton (reworded; the plan is rewritten after this spec is
approved).

**Replace / revert:**
- `RepositoryDeveloperConfig`: `Path` string becomes `PathDeveloperConfig?`; add `Source`. Update
  `DeveloperConfigShape`, `DeveloperConfigValidator` (allowed `source` values; reject `path.prepare`)
  and `DeveloperConfigFileSource` re-keying for the nested block.
- Remove the managed-checkout plumbing added on the branch: `LocalGitCheckout.EffectivePath`,
  `PathComesFromGroup`, `GroupPathKey`, the group `path`+`ref` error, and the `repositoryConfig`
  parameters threaded through `IsManagedCheckout`, `IsColdManagedCheckout`, `DeferredCheckout.ShouldDefer`,
  `LocalCheckoutPrefetch`, `PreparePlan.groupPathKey` and `LocalProjectSource` (restore those files to
  `main` for that concern). Promoted members never reach the managed-checkout code, and prefetch and
  deferral see no cold managed checkout for them (test).
- Add the source-selection seam (decision 3): where a grouped member's effective source is settled,
  promote `repository`/`path` to `path` when the group entry is `source: path`; `PathSource.ResolveRepoRoot`
  gains the group step between `config.Path.Path` and the catalog `path:` (group dir not confined).
- `LocalProjectSource.LocalPathDeprecationNotice`: restore the `main` condition and add the sentence
  from 2.9.
- Tests: drop the `LocalProjectSource`/`LocalGitCheckout`/`DeferredCheckout`/`Prefetch` group-path tests;
  add `PathSource` tests (group dir as root; relative to AppHost; missing dir names repository and key;
  member `path.path` and `repository.path`/`local.path` beat the group; `url`/`container` member
  untouched; ungrouped same-name service untouched; `source: repository` cancels; group `ref` ignored;
  `project:` resolves and is confined to the group dir; two members share one build-gate key and build
  serially; no cold-clone prefetch), config tests for binding from json and
  `ServiceSources__Repositories__<name>__Source` / `__Path__Path`, and validator tests.

## 5. Migration and deprecation story

`repository.path` and `local.path` are unchanged and stay deprecated. The path off them is now complete:
per service `{ "source": "path", "path": { "path": ... } }`; for a whole group, the same under
`repositories.<name>`. The one thing that does not carry over by itself is a `repository.prepare`
block (per service: move to `path.prepare`). The group form never existed before, and no
`repositories.<name>.path` string ever worked, so nothing is migrated for it. CHANGELOG `[Unreleased]`
**Added** gets one entry (#416). The **Deprecated** entry lives in a released section, so the pointer
at the group form lives in the deprecation notice text and the Added entry instead.

## 6. Documentation

`docs/sources/repository.md` (replace "no repository-level equivalent": the group form, precedence,
which members it touches, ref ignored, eShop example); `docs/sources/path.md` (replace "No
`repositories:` grouping either": a path service still has nothing to clone, but a group can be pointed
at one tree; add the #397 note); `docs/guides/configuration.md` (shape and env spelling); CHANGELOG.

## Open Questions

1. Decision 3b: should the group form also promote members whose effective source is `repository`
   (recommended) or only members already on `path`? The latter is narrower but leaves the eShop case
   (everyone on `repository`) needing per-service edits.
2. Decision 8: is a group-level `path.prepare` wanted now? Recommended no (validator rejects it); it
   needs a group-keyed prepare marker.
3. Decision 6 (audit for unknown repository names) is slightly beyond the ticket: keep (default) or drop.
