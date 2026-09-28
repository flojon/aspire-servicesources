# Aspire.Hosting.ServiceSources — Serializing the build of `path` services that share a repository

**Date:** 2026-09-28
**Status:** Draft
**Resolves:** GitHub issue #397 (two `"path"` services pointing at one repository race on shared
`bin/`/`obj/` when Aspire starts them at once).
**Upstream:** microsoft/aspire#15190 (the same collision, reported against `AddProject`).

## Motivation

A `"path"` service resolves to `builder.AddProject(name, projectFile)` (`PathSource.Resolve`, dotnet
kind). Nothing in this package compiles it: Aspire launches each project resource with
`dotnet run`, and the build is that command's own implicit one (see `docs/sources/repository.md`,
"Aspire builds a checkout, on every start"). Aspire starts independent resources concurrently, so
two path services whose projects share a `ProjectReference` run two MSBuild invocations at the same
time over the same referenced project's `obj/` and `bin/`. The loser fails with `MSB4018` or
`CS2012` (file in use), on a cold tree or after a source change, non-deterministically.

The `"repository"` source already serializes *its own* mutations per shared checkout
(`CheckoutNameLock`: reconcile, prepare) but not the build, and this ticket is scoped to `"path"`
only. Managed-checkout behaviour must not change.

## Findings that constrain the design

1. **The build is not ours.** There is no call in this package to wrap in a lock. Any serialization
   has to be inserted between "Aspire decides to start the resource" and "`dotnet run` builds". The
   package subscribes to no lifecycle event for that today (its only resource-event reader is
   `ServiceStartupFailureNotices`, which reads state snapshots after the fact).
2. **Composition-time work cannot serialize a start-time build.** `AddService` calls run one at a
   time on the composition thread, so a lock taken there protects nothing; the race is between the
   starts of two resources, well after composition.
3. **Only the build step is serialized** (human decision). After the builds, the processes run
   concurrently. A gate held across the *run* would deadlock two services that `WaitFor` each other.
4. **What "sharing" means is not knowable cheaply.** The collision is caused by a shared
   `ProjectReference` closure; computing it needs MSBuild evaluation of every project at
   composition. A path service's repository, by contrast, is cheap: the nearest ancestor holding
   `.git` (the walk `PathSource.ConfinementRootOf` already does).
5. **A gate that mis-fires must be cheap.** Serializing two builds that did not need it costs
   startup time only; failing to serialize two that did costs a flaky start. The design errs on
   over-grouping.
6. **`dotnet run` still builds afterwards.** After a gated `dotnet build` of the same project, the
   run's own build is an incremental no-op. Whether concurrent no-op builds can still collide is
   the residual risk the spike (see Testing) must measure; it decides whether a `--no-build`
   argument is also needed.

## Alternatives weighed

| | Auto-detect only | Auto-detect + explicit group override (recommended) | Explicit group only |
|---|---|---|---|
| Fixes the ticket's case (same repo) with no config | yes | yes | no: every affected developer must know to opt in |
| Two repos sharing a project through a sibling path (`../shared/Lib.csproj`) | no: different git roots | yes, by naming one group | yes |
| New config surface | none | one optional catalog field, `buildGroup` | one field, mandatory to use |
| Failure mode | silent miss for cross-repo sharing | override is the escape hatch | silent miss until someone hits the flake |

**Recommendation: auto-detect plus an explicit `buildGroup` override.** Reasons: (a) the ticket's
scenario is a fixed shape (one repository), so it should just work; requiring opt-in for a race is
how the bug ships again. (b) Auto-detection by repository root is deliberately coarse (finding 4);
a coarse rule needs an escape hatch for what it cannot see, cross-repo `ProjectReference`s, and an
explicit name is the only tool for that. (c) The override is one nullable field with one precedence
rule (explicit wins), no new mode, no opt-out switch. Auto-detect alone is a strict subset, so if
the reviewer judges the field not worth its documentation and export-surface cost, it can ship
first and the field can follow without breaking anything.

## Design

### Group key

For a `"path"` service of the built-in `dotnet` kind, the **build group key** is:

1. `buildGroup`, if the catalog entry declares one (ordinal, case-sensitive); else
2. the full, separator-trimmed path of the nearest ancestor of the resolved service directory
   (`repoRoot` in `PathSource.Resolve`) that holds a `.git` entry (directory or file, as
   `ConfinementRootOf` treats it); else
3. the resolved service directory itself (a `.git`-less copy: two services in one directory share;
   unrelated directories do not).

Keys are namespaced so an explicit name can never equal a path: explicit `g:<name>`, derived
`p:<full path>`, compared ordinal-ignore-case on Windows and ordinal elsewhere.

A group of one member is not gated.

### The gate

`PathSource` registers each `dotnet` path service's key in a per-builder registry
(`ConditionalWeakTable<IDistributedApplicationBuilder, BuildGroups>`, same pattern as
`CheckoutNameLock.For`) and subscribes the returned project resource to
`BeforeResourceStartedEvent`. Membership is only complete once every service has been added, so the
subscription is unconditional and the handler asks the registry, at start time, whether its key has
more than one member; if not it returns at once. Otherwise it:

1. acquires the key's `SemaphoreSlim(1,1)` asynchronously, honouring the event's cancellation token;
2. runs `dotnet build "<projectFile>"` (argument list, no shell) in the project's directory,
   streaming output to that resource's own log via `ResourceLoggerService`, so a compile error
   lands in the dashboard console where #150 already points the developer;
3. releases the semaphore in `finally`, whatever the exit code.

The handler **never fails the start**. A non-zero exit is logged against the resource and the
resource proceeds; its own `dotnet run` rebuilds, fails identically, and reports through the
existing `ServiceStartupFailureNotices` path. One source of truth for "does not compile".

The semaphore is released before the process runs, so runtimes stay concurrent (finding 3). The
build uses the configuration `dotnet run` uses by default so the follow-up is a no-op. The gate is
registered only when `builder.ExecutionContext.IsRunMode`.

### Scope

- **In:** `"path"` source, `dotnet` kind (the only kind that becomes an `AddProject` resource and
  the only one with the shared-`ProjectReference` `bin/obj` race).
- **Out:** `"repository"`, `"url"`, `"container"`, `"kubernetes"`, `"disabled"`; non-dotnet kinds
  (javascript, java) build outside MSBuild. A managed checkout is documented as not gated; it may
  show the same race and is a follow-up, not this ticket.

### Config surface (override)

- Catalog: optional `buildGroup: <string>` on a service entry (yaml), `WithBuildGroup(string)` on
  `ServiceDefinitionBuilder`. Non-empty and without surrounding whitespace, else
  `ServiceSourcesConfigurationException`. Read only when the resolved source is `"path"`; ignored,
  like a leftover `repository.ref`, when another source is selected.
- Developer config: **not** exposed. The group describes the code's structure, which the catalog
  owns (see Open Questions).
- New public API surface: `WithBuildGroup` and `ServiceMetadata.BuildGroup`, reachable from the
  `AspireExport` surface (`ServiceDefinitionBuilder` is `ExposeMethods = true`), so the TypeScript
  export CI job must be checked for a new export.

## Attack surface

- The gate executes `dotnet build` on the project file `AddProject` was given, which
  `LocalProjectSource.ResolveProjectFile`/`ConfineProject` already confine. `buildGroup` is only a
  dictionary key and reaches no command line.
- A developer's unconfined `path.path` override can point anywhere; building it is what `dotnet run`
  would do a moment later regardless, so the gate adds no new execution.
- A group name shared unintentionally by unrelated services costs serialized builds, never a
  deadlock: the semaphore is held around the build only.

## Testing

Per repo convention (`PathSourceTests`; `dotnet test -f net10.0` per round, full matrix once):

- Group key: same repo root shares a key; different repos differ; `.git`-less directories;
  `buildGroup` beats derivation; explicit `g:x` never equals a path.
- Gate: two grouped services' builds never overlap (fake build runner records enter/exit); a failed
  build releases the semaphore and does not throw; cancellation while waiting releases nothing it
  does not hold; a lone service takes no lock; different keys run concurrently.
- Isolation: `"repository"` services subscribe nothing (guard for "managed behaviour unchanged");
  publish mode subscribes nothing.
- Config: `buildGroup` validation, yaml round-trip, ignored under another source.
- **Spike (first task of the plan):** measure against Aspire 13.5.2 that (a)
  `BeforeResourceStartedEvent` holds that resource's launch until the handler completes, and is
  raised per resource in parallel; (b) two real projects sharing a reference, started through a
  gated pair, no longer collide, and whether the follow-up `dotnet run` no-op build ever collides.
  If (b) fails, add `--no-build` to the grouped projects' arguments and re-measure.

## Documentation and release notes

- `docs/sources/path.md`: a short "Several path services from one repository" section: the auto
  rule, the `buildGroup` override, and that only the build is serialized.
- `docs/sources/repository.md`: "Aspire builds a checkout, on every start" and "Several services
  from one repository" gain one sentence each: the gate is `path`-only, a managed checkout is not
  gated. No existing claim becomes false.
- `CHANGELOG.md` `[Unreleased]`: a **Fixed** entry only if the path source's collision shipped in a
  released version; 0.7.0 introduced `path:`, so confirm against the tag; otherwise **Added**.

## Sibling overlap

Open PR #404 edits `ServiceSourcesBuilderExtensions.cs`, `ILocalResourceKind.cs`,
`DeferredCheckout.cs`, `LocalCheckoutPrefetch.cs`, `CheckoutTiming.cs`,
`ServiceSourcesConfigCache.cs`, `docs/sources/path.md`, `docs/sources/repository.md`,
`CHANGELOG.md` and `PathSourceTests.cs`. This design touches `PathSource.cs`, the catalog field,
the docs, the changelog and the tests; the shared files are docs, changelog and tests. Whoever
lands second rebases.

## Open Questions

1. Should developer config be able to join or override a group (a `path.path` override making two
   services share a directory the catalog does not know are related)? Design says no; the auto
   rule already covers same-repository overrides.
2. Changelog category (Added vs Fixed), pending the 0.7.0 tag check above.
3. Should managed checkouts get the same gate in a follow-up issue? (Not filed by this run.)
4. Does the spike show `--no-build` is needed? If so it is a plan task, not a design change.
