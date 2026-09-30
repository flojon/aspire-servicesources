# A repository-level `path` override for a `repositoryRef` group (#416)

**Date:** 2026-09-30
**Status:** Draft (one open question, at the end; every other decision is settled below)
**Resolves:** #416 (`servicesources.local.json` has no way to point a whole `repositoryRef` group at an
already-checked-out working tree in one setting).
**Relates to:** #397 (merged; serializes builds of `"path"` services that share a repository) and #291
(the grouping this extends). Neither is changed.

---

## 1. Facts this design rests on

Checked against the code at `ac4d802`.

- `Config/RepositoryDeveloperConfig.cs` already has `Path`, `Ref` and `Prepare`. `Path` is **reserved**:
  `LocalGitCheckout.PrepareRepoRoot` throws `ServiceSourcesConfigurationException` when a grouped
  service's `repositoryConfig.Path` is non-null ("is reserved and does not redirect the group's
  checkout"). `DeveloperConfigShape.Repository` and `DeveloperConfigValidator` already accept the
  field, so **no new key and no change to the config shape** is needed; this ticket removes a
  rejection and gives the field its meaning.
- The file key is `repositories.<name>.path` (`DeveloperConfigFileSource.FileRepositoriesKey`), which is
  re-keyed to `ServiceSources:Repositories:<name>:path` (`DeveloperConfiguration.RepositoriesKey`); the
  environment spelling is `ServiceSources__Repositories__<name>__Path`.
- `<name>` is matched against `RepositoryDefinition.CheckoutName` case-insensitively
  (`DeveloperConfiguration.CanonicalizeToCatalog`). The undeclared-names half of that call is
  **discarded** for repositories (`DeveloperConfiguration.ReadFrom`), so an entry naming no declared
  repository is silently inert today.
- A service's own escape is `repository.path` (deprecated alias `local.path`;
  `ServiceDeveloperConfig.ReconcileRepositoryAlias` folds `local` into `Repository`). Its behaviour is
  spread across `LocalGitCheckout`:
  - `IsManagedCheckout(config)` is `config.Repository.Path is null`; it gates the managed-vs-path
    split in `LocalProjectSource.Resolve` (and `RequireRepositoryToCheckOut`), and, through
    `IsColdManagedCheckout`, `DeferredCheckout.ShouldDefer` and `LocalCheckoutPrefetch.Run`.
  - `PrepareRepoRoot` resolves it with `ResolveDeveloperDirectory(..., appHostDirectory)`, i.e.
    `Path.GetFullPath(path, appHostDirectory)`: absolute used as-is, relative resolved against the
    **AppHost directory** (not the json file's directory), and the directory must exist. No clone, no
    fetch, no ref, no reconciliation.
  - `repository.path` together with `repository.ref` on one service is an error.
  - A grouped service's own `repository.ref` is an error (its group's ref is shared).
- The resolved directory becomes `repoRoot`: `LocalProjectSource.ResolveProjectFile`/`ConfineProject`
  resolve the catalog's `project:` (and every non-dotnet kind's paths) **against `repoRoot`** and confine
  them to it. That is exactly what the ticket wants for `src/Basket.API/Basket.API.csproj`.
- `PreparePlan.For(..., managedCheckout, ...)` ignores the catalog's `prepare` (with a notice) on an
  unmanaged checkout, and `CheckoutPreparation`/`PrepareMarker` take the same flag.
- `LocalProjectSource.LocalPathDeprecationNotice` fires whenever `!managedCheckout` and says
  "`repository.path` is deprecated, use the `path` source".
- The "declare the same repository ... but none of them are grouped" notice
  (`ServiceSourcesConfigCache`) only considers entries with `Repository.CheckoutName == entry.Key`
  (ungrouped), so **a grouped member never contributes to it**. The ticket's warning comes purely from
  the workaround of declaring the services ungrouped.
- `RepositoryDeveloperConfig.Prepare` is bound but read nowhere in `src`; not in scope.
- `docs/sources/repository.md` ("The per-service escape from a group", "There is no repository-level
  equivalent") and `docs/guides/configuration.md` (the paragraph ending "`path` exists on the shape but
  is reserved") state the old behaviour. The issue quotes a README sentence; `README.md` has no such
  text now, so the docs above are the ones to update.

## 2. Decisions

1. **Meaning.** `repositories.<name>.path` is the directory the whole group uses as its checkout: the
   same thing `repository.path` is for one service. Members' `project:` paths resolve against it and are
   confined to it, unchanged. Nothing is cloned, fetched, reconciled or deferred for a member that
   resolves to it.
2. **Resolution of the value.** Identical to `repository.path`: absolute is used as-is; relative is
   resolved against the AppHost directory; it must be an existing directory. Not relative to the json
   file, because the same key can arrive from environment variables or user secrets, which have no file.
   A missing directory is a `ServiceSourcesConfigurationException` that names the **repository** and the
   key (`ServiceSources:Repositories:<name>:path`), not a member service. `ResolveDeveloperDirectory`
   gains a caller-supplied subject so one implementation serves both messages.
3. **Precedence** (first that is set wins): the member's own `repository.path` (alias `local.path`) >
   the group's `path` > the managed checkout (group `ref` > catalog `defaultRef`, cloned under
   `.servicesources/checkouts/<name>/`). A member with its own path is resolved exactly as today and
   never sees the group's; its siblings still get the group's. One seam carries this: an
   `EffectivePath(config, repositoryConfig)` helper on `LocalGitCheckout`, and `IsManagedCheckout` /
   `IsColdManagedCheckout` / `PrepareRepoRoot` / `PreparePlan`'s `managedCheckout` argument /
   `RequireRepositoryToCheckOut` all read through it, so prefetch, deferral, prepare and the
   "no repository to clone" exemption cannot disagree about whether a member is managed.
4. **Group `path` with group `ref`.** An error when a member resolves, mirroring the per-service rule
   ("`ref` only applies when this tool manages the clone"), naming the repository and both keys. It is
   raised whether or not that particular member also has its own path, because the contradiction is in
   the group's own entry. Catalog `defaultRef` is not an error: it is the team's and is ignored, as it is
   for a per-service path. A member's own `repository.ref` stays refused as today.
5. **Group path applies only to a grouped service.** The lookup is by `CheckoutName`, and an ungrouped
   service's `CheckoutName` is its own name, so `repositories.orders.path` would otherwise redirect an
   ungrouped service called `orders`. `EffectivePath` therefore consults `repositoryConfig.Path` only
   when `IsGrouped`. Such an entry is reported by decision 6.
6. **Unknown repository names.** Keep the tolerance (a warning, not an error, matching
   `ServiceConfigAudit` for services) but stop being silent: a typo'd name now means members quietly
   clone into the managed checkout instead of using the developer's tree, which is worse than a typo'd
   `ref`. `DeveloperConfiguration` keeps the undeclared repository names it currently discards
   (`UndeclaredRepositoryNames`); `ServiceConfigAudit.Report` adds one reason listing them with a
   did-you-mean against the declared repository names, and listing those names, worded for
   `repositories`. It applies to any key under the entry, not just `path`.
7. **No new warning, and no old one.** The ungrouped-URL notice needs no change (fact above); a test
   pins that a grouped set with a group path produces none. The per-service deprecation notice
   (`LocalPathDeprecationNotice`) is **not** emitted for a path that came from the group: its advice
   (the `path` source, one entry per service) is exactly the repetition the ticket removes, and the
   `path` source has no group form. It still fires for a member's own `repository.path`.
8. **Prepare.** Unchanged: on an unmanaged checkout the catalog `prepare` is ignored with the existing
   notice and the developer's per-service `repository.prepare` applies. Implementation must check that
   notice's wording does not tell a group-path developer to set a `repository.path` they did not write.
   `RepositoryDeveloperConfig.Prepare` stays unread (separate, pre-existing gap).
9. **Builds are not serialized.** Members sharing the group path are `"repository"` services, which #397
   deliberately leaves ungated, as it does a managed group sharing one clone. Not changed here.
10. **Format.** No key is added, renamed or reshaped; one previously-rejected value becomes valid. The
    doc comments that call the field reserved are rewritten; the 2026-09-08 design's "no
    repository-level ..." (finding 4) is superseded by this spec and left as history.

## 3. Acceptance mapping

| Ticket criterion | Covered by |
|---|---|
| `repositories.<name>.path` sets the override for the whole group | decisions 1-2 |
| Works like `repositories.<name>.ref` | same key location, binding, canonicalization; decision 4 for the pair |
| Member's `local.path` still wins | decision 3 |
| Monorepo AppHost, `project:` relative to repo root, no per-service repetition, no warning | decisions 1, 7; fact on the notice |
| Shared path written once | decision 1 |
| Docs and CHANGELOG | section 5 |
| Tests | section 4 |

## 4. Tests (each written first)

`LocalGitCheckout` / `LocalProjectSource` level:
- group path on a grouped member: repoRoot is the group directory, `NeedsReconciliation` false, no git
  calls (fake git client records none), relative value resolved against the AppHost directory.
- absolute value used as-is; missing directory throws naming the repository and the key.
- member `repository.path` beats group path (distinct directories; a sibling still gets the group's).
- member `local.path` (alias) beats group path.
- group `path` + group `ref` throws naming repository and both keys; group `ref` alone still works.
- group `path` ignored for an ungrouped service of the same name.
- `IsColdManagedCheckout` / prefetch: no speculative clone and no deferral for a group with a path;
  a mixed group (one member with no effective path) behaves as before.
- `RequireRepositoryToCheckOut` exempts a grouped member whose only path is the group's.
- a `dotnet` member's `project:` relative to the group directory resolves and is confined to it.
- no deprecation notice for a group-sourced path; notice still present for a member's own path.

Config level:
- `DeveloperConfiguration.ReadFrom` binds `repositories.monorepo.path` from the json file and from
  `ServiceSources__Repositories__monorepo__Path`, case-insensitively against the declared name.
- an undeclared repository entry is exposed and the audit reports it (with a near-miss); a declared one
  is not.
- grouped members with a group path (code and yaml catalogs) emit none of the startup notices, including
  the "none of them are grouped" one.

Remove/replace the existing test asserting the "reserved" error in `LocalProjectSourceTests`.

## 5. Documentation

- `docs/sources/repository.md`: replace "There is no repository-level equivalent" with the group-path
  description, precedence, relative-to-AppHost rule, the `ref` conflict, and the eShop-shaped example.
- `docs/guides/configuration.md`: replace the "reserved" sentence; add the env spelling.
- `docs/sources/path.md`: one line pointing to the group path for grouped services.
- `CHANGELOG.md` `[Unreleased]` **Added**: one entry, #416.
- Code comments that say "reserved" (`RepositoryDeveloperConfig`, `LocalGitCheckout.PrepareRepoRoot`).

## 6. Review findings applied (single round)

- Attack surface: no new trust boundary. The path is developer-supplied config of the same kind as
  `repository.path`/`path.path`; project confinement to the directory is unchanged and still lexical.
  The one new risk is silent misconfiguration (typo'd name), handled by decision 6; a wrong directory
  that exists is the developer's to own, as today.
- Contradiction found and fixed: an earlier draft let an ungrouped service pick up a same-named
  `repositories` entry (decision 5).
- False-claim check: the ticket's warning does not come from the grouped path at all (fact above), so no
  change to that notice was designed.

## Open Questions

1. Decision 6 adds an audit notice for unknown repository names, which is slightly beyond the ticket. If
   the owner prefers to keep the ticket narrow, drop it and accept that a typo'd group name silently
   falls back to a managed clone. Default in this spec: include it.
