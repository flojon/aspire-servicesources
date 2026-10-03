# Group-level `path` source for a `repositoryRef` group Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A `repositories.<name>` entry in `servicesources.local.json` (or its env spelling) can be `{ "source": "path", "path": { "path": "..." } }`, which resolves every grouped member whose source is `repository` or `path` through `PathSource` against that directory (#416).

**Architecture:** `RepositoryDeveloperConfig` gains `Source` and `Path` becomes a `PathDeveloperConfig`. One static seam, `GroupPathSource.Redirects(serviceName, definition, config, repositoryConfig)`, answers "is this member resolved by `PathSource` against the group's directory". `AddService` consults it before the source lookup, `PathSource.Resolve` uses it to pick the group directory as `repoRoot`, and the speculative prefetch skips redirected members. Everything else (build serialization, prepare rules, confinement) is `PathSource`'s existing behaviour. No managed-checkout plumbing changes.

**Tech Stack:** C# / .NET (net8.0;net9.0;net10.0), xUnit, Aspire.Hosting.

**Spec:** `docs/superpowers/specs/2026-09-30-416-repository-level-path-design.md`. Read it first; "decision N" below refers to its section 2.

## Global Constraints

- Source in `src/Aspire.Hosting.ServiceSources`, tests in `test/Aspire.Hosting.ServiceSources.Tests` (one module).
- File key `repositories.<name>.source` / `.path.path`; config keys `ServiceSources:Repositories:<name>:Source` / `:Path:Path`; env `ServiceSources__Repositories__<name>__Source` / `__Path__Path`. No flat `path` string form.
- Precedence per member, first that applies: own `path.path` or `repository.path`/`local.path`, then the group directory (member source `repository` or `path`, grouped), then the unchanged source. `url`, `container`, `kubernetes` members and ungrouped same-name services are untouched.
- `source` accepts `path` or `repository`; anything else, and any group-level `prepare`, is a validation error. A group `ref` beside `source: path` is ignored.
- Relative values resolve against the AppHost directory; the directory must exist; a missing one throws naming the repository and `ServiceSources:Repositories:<name>:path:path`.
- No new deprecation notice for the group form; the `repository.path` notice gains one sentence for a grouped service. No "none of them are grouped" warning for a path-sourced group.
- Keep the unknown-repository-name audit (`ServiceConfigAudit`).
- Cheap leg per task: `dotnet test -f net10.0`; build check: `dotnet build -c Release -warnaserror`.

## Task 1: Shape, binding and validation

- [ ] Test first (`DeveloperConfigurationTests`): json and `Repositories__X__Source`/`__Path__Path` bind (case-insensitive name); a higher layer's `source: repository` cancels a lower `path`; `url`/`container`/`kubernetes` source refused; `path.prepare` and group `prepare` refused; a flat string `path` refused; an invalid entry for an undeclared repository does not fail startup.
- [ ] `RepositoryDeveloperConfig`: add `Source`, make `Path` a `PathDeveloperConfig?`.
- [ ] `DeveloperConfigShape.Repository` takes the `repository`/`path` source names; `DeveloperConfiguration.ValidateRepositoryEntries` enforces the rules above.

## Task 2: The seam and `PathSource`

- [ ] Test first (`GroupPathSourceTests`): every repository-sourced member lands on the group directory; a `path` member without its own directory gets it; relative anchors to the AppHost directory; a missing directory names repository and key; `source: repository` leaves members alone; member `path.path` and `repository.path`/`local.path` win while siblings still get the group's; container/url members and ungrouped same-name service untouched; group `ref` ignored; `Redirects` truth table.
- [ ] Add `GroupPathSource` (`Redirects`, `ResolveDirectory`); route `AddService` and `PathSource.Resolve` through it; `RunPrepareIfDue` treats a group directory as the developer's own tree.

## Task 3: Remove the first draft's managed-checkout plumbing

- [ ] Restore `LocalGitCheckout`, `PreparePlan`, `DeferredCheckout`, `LocalProjectSource` to the `main` signatures (no `EffectivePath`, `PathComesFromGroup`, `GroupPathKey`, group `path`+`ref` error, `repositoryConfig` threading), and drop their group-path tests.
- [ ] Test first (`LocalCheckoutPrefetchTests`): a redirected member produces no speculative clone; `LocalCheckoutPrefetch` filters with `GroupPathSource.Redirects`.

## Task 4: Notices, serialization and audit

- [ ] Tests: the `repository.path` notice for a grouped member names the group form; a path-sourced group emits no deprecation notice and no "none of them are grouped" warning; ungrouped same-repository entries still warn; two members share one `BuildGroupKey` and build serially.
- [ ] `LocalProjectSource.LocalPathDeprecationNotice` adds the sentence for a grouped service.
- [ ] Keep `UndeclaredRepositoryNames` and `ServiceConfigAudit.OrphanedRepositoryEntriesReason` with their tests.

## Task 5: Docs and changelog

- [ ] `docs/sources/repository.md`, `docs/sources/path.md`, `docs/guides/configuration.md`: the group form, precedence, which members it touches, ref ignored, build gate, no stale "reserved" or "no repository-level equivalent" text.
- [ ] `CHANGELOG.md` `[Unreleased]` **Added** entry for #416.

## Task 6: Verify

- [ ] `dotnet build -c Release -warnaserror` and `dotnet test -f net10.0` green; full matrix once before landing.
