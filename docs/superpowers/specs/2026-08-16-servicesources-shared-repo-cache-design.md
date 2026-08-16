# Aspire.Hosting.ServiceSources — Per-Service Checkouts Inside the AppHost Directory

**Date:** 2026-08-16
**Status:** Design — ready for implementation planning.
**Resolves:** cache-directory collision and stale-ref bugs found while reviewing milestone 1a. Companion to the [milestone 1a design](2026-08-09-servicesources-design.md) (defines the `"local"`-source cache layout this design replaces) and the [phase 2 deferred/parallel resolution design](2026-08-15-servicesources-phase2-deferred-resolution-design.md) (defines the concurrency model this design must be safe under).

## Motivation

Milestone 1a's cache layout is `<cacheDirectory>/<repoName>` where `repoName` is just the trailing URL segment (`LocalProjectSource.GetRepositoryName`). Two problems, both found during review rather than in production:

1. **Name collision.** Two different repositories with the same trailing segment (`github.com/team-a/orders` and `github.com/team-b/orders`) collide on `<cacheDirectory>/orders`. Milestone 1a detects this (`RepositoryUrlsMatch` compares the existing clone's `origin` URL against the configured one) and throws — but throwing is the whole remedy; there's no way to use both repos from the same cache directory.
2. **Stale ref on reuse.** `config.Ref` / `metadata.DefaultRef` is applied only inside the `if (!Directory.Exists(repoRoot))` branch — i.e. only on the very first clone. On every subsequent resolve, the existing checkout is reused exactly as-is; ref is never re-read or re-applied. If two consuming projects (or two runs of the same project after editing `servicesources.local.json`) want different refs of the same cached repo, whichever ref was checked out first silently wins for both — no error, no re-checkout.

Both problems trace back to the same root cause: one cache directory is being asked to represent two independent things at once — "the object history of a repository" and "a specific ref, checked out, for one consumer." An earlier draft of this design split those into a shared bare-object-store plus per-service worktrees/reference-clones. That was dropped: these checkouts are meant to be places a developer actively edits and commits code (the main reason to reach for `"local"` source over `"cluster"` at all), and object-store sharing across services optimizes for a cost — repeated full clones of the same repo — that nothing in this project has actually observed yet, while adding real complexity (a shared bare-repo lifecycle, cross-process locking, and either a worktree branch-naming conflict or a fragile `alternates` dependency on the bare repo never moving). The fix below solves both named bugs without any of that: each service simply gets its own full, independent clone, keyed by `serviceName` instead of `repoName`.

## Architecture

### Per-service checkout, keyed by service name, inside the AppHost directory

```
<AppHostDirectory>/.servicesources/checkouts/<serviceName>/
<AppHostDirectory>/.servicesources/.gitignore   (auto-written: "*\n!.gitignore\n")
```

`serviceName` is already guaranteed unique within one `servicesources.yaml`, so this path can never collide with another service — even one pointed at the same underlying repository. The collision bug is fixed as a side effect of keying by service instead of by repo name; no URL-hashing or identity computation is needed.

This also means the `cacheDirectory` key in `servicesources.local.json` no longer applies to managed clones — checkout location is fixed under the AppHost's own directory, not developer-configurable. (`path`, which points at an entirely developer-managed checkout outside this flow, is untouched.) `ServiceSourcesConfigCache.GetCacheDirectory` and the `cacheDirectory` config field should be removed as part of implementing this design.

The `.gitignore` is written by the package itself (idempotent, write-if-missing), same as before — it ignores everything under `.servicesources/` except itself, so a service's checkout (and any commits a developer makes inside it) never leaks into the AppHost repo's own git status.

`GetOriginUrl` and the `RepositoryUrlsMatch` collision check are removed entirely: the cache path itself now encodes uniqueness, so there is nothing left to reconcile.

### Ref resolution — reconciled on every resolve, guarded against local edits

Because the checkout is a normal (non-bare) clone owned by one service, `IGitClient.Checkout(repoRoot, reference)` can run unconditionally whenever a `reference` is configured — fixing the stale-ref bug — with no branch-naming conflicts to worry about (each service's clone has its own independent `refs/heads` namespace; `LibGit2SharpGitClient.Checkout`'s existing branch/remote-branch/tag/commit resolution logic is reused completely unchanged).

But since a developer may have uncommitted edits in that checkout (the whole reason `"local"` source exists), reconciling the ref on every resolve must not silently discard work:

1. Before attempting `Checkout`, check whether the working tree is dirty (new `IGitClient.HasUncommittedChanges(repoRoot)`, backed by `repo.RetrieveStatus().IsDirty`).
2. If dirty **and** the requested ref differs from what's currently checked out, fail loudly: throw `ServiceSourcesConfigurationException` naming the service, the configured ref, and telling the developer to commit or stash before the checkout can be reconciled. No silent skip, no forced checkout over local changes — consistent with this project's existing fail-fast stance (same discipline as the ref-not-found and path+ref-conflict errors already in place).
3. If dirty but the requested ref already matches what's checked out, do nothing — this is the common case (developer editing on the expected branch) and must not be treated as an error on every subsequent AppHost start.
4. If clean, `Checkout` runs as normal, whether the checkout is newly created or being reused.

No unconditional fetch on every resolve — that would reintroduce a silent background network call on every AppHost start, which milestone 1a deliberately avoided:

1. `Clone` (only when the checkout doesn't exist yet) clones at the remote's default branch — no fetch needed beyond the clone itself.
2. If a `reference` is configured, `Checkout(repoRoot, reference)` is attempted (subject to the dirty-tree guard above).
3. If that checkout fails because the ref can't be resolved locally, `Fetch(repoRoot)` runs once and the checkout is retried. A ref that's genuinely missing (typo, deleted branch) still fails after the retry and is wrapped in the existing `ServiceSourcesConfigurationException` shape, naming the service and ref — unchanged error contract from milestone 1a.

### Concurrency

Because every service now touches only its own checkout directory (`<AppHostDirectory>/.servicesources/checkouts/<serviceName>/`), this design needs no new locking at all — it's already compatible, unmodified, with the [phase 2 deferred/parallel resolution design](2026-08-15-servicesources-phase2-deferred-resolution-design.md)'s stated assumption that "each service only ever touches its own cache directory." Two services sharing the same underlying repo just do two independent clones; there's no shared mutable state between them to serialize.

Two separate `dotnet run` processes (or two AppHost projects) racing to create the *same* service's checkout at the same time is a pre-existing risk carried over unchanged from milestone 1a's synchronous single clone — not something this design introduces or needs to newly solve.

## Testing

- `LibGit2SharpGitClient`: `Fetch` (new) pulls a ref created on the origin after the initial clone; `HasUncommittedChanges` (new) reports false on a clean checkout and true after an uncommitted edit.
- `LocalProjectSource.ResolveProjectPath`: cache-miss clones + checks out the configured ref; cache-hit with a clean tree re-applies a changed ref without re-cloning; cache-hit with a dirty tree and a ref that already matches does nothing; cache-hit with a dirty tree and a *different* ref throws, naming the service and ref, without touching the working tree; a ref that requires a fetch triggers exactly one `Fetch` call before the retry; the `.gitignore` is written under `.servicesources/`.
- End-to-end (`AddServiceIntegrationTests.cs`): two services configured against the *same* fixture repository with *different* refs both resolve correctly in one `AddService` sequence — the concrete scenario this design fixes, exercised for real against `LibGit2SharpGitClient` and a real bare-repo fixture, not fakes. A third service sharing that repo and ref with one of the other two is included as well, confirming two independent clones of the same repo+ref simply both succeed (no shared-namespace conflict, unlike the worktree approach this design replaced).
- `AddServiceIntegrationTests.cs`'s existing single-service test asserts the project path directly under the old `<cacheDirectory>/<repoName>/<project>` layout — that assertion must be rewritten to the new `<AppHostDirectory>/.servicesources/checkouts/<serviceName>/<project>` shape, not left in place alongside the new multi-service cases.

## Explicitly Out of Scope

Cleanup of orphaned checkouts when a service is removed from `servicesources.yaml` is not addressed here. Auto-pull/freshness policy beyond "fetch only when the configured ref can't be resolved locally" is unchanged from milestone 1a's stance and remains out of scope (tracked separately as phase 2 "Repo update / freshness command", issue #3). Sharing a single clone's object store across services or across AppHost projects is deliberately not pursued by this design (see Motivation) — if repeated full clones of the same large repo become an observed cost, that's a separate future design, not a reason to hold this one.
