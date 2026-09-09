# #291 Stage 2 implementation plan — the authoring API, yaml, developer config, and the checkout re-keying

**Status:** Draft
**Implements:** the Stage 2 row of `docs/superpowers/specs/2026-09-08-repository-handle-design.md`'s
Staging table — "everything with actual new behaviour." Stage 1 (`RepositoryDefinition` +
`CheckoutName`, all records anonymous) shipped as PR #306, merged to main at `b2ccca4`'s successor
`4a74f63` (main HEAD as of this plan).
**Base commit:** `4a74f63` (origin/main).

Every claim below about current code is grounded in a read of that file at this commit; no line
numbers are repeated from the design doc's own findings documents, which are cited by name where
relevant.

## What "done" means

The six acceptance criteria from the design doc, restated as this plan's exit conditions:

1. Two services naming the same repository through `WithSharedRepository`/`repositoryRef` produce
   one clone and one working tree.
2. Two services naming the same URL **without** a shared handle stay independent (unchanged).
3. `LocalCheckoutPrefetchTests.FirstAddService_TwoServicesInOneRepository_DownloadsItTwiceConcurrently`
   is replaced by its inverse.
4. Conflicting refs across a shared repository are unrepresentable in code (one handle, one
   `DefaultRef`) and rejected as a named error in the one remaining route to a conflict: a grouped
   service's own `local.ref`.
5. The existing per-service reconciliation rules are restated in terms of the shared checkout: a
   failed shared checkout names every service on it.
6. **An existing AppHost, yaml or code, keeps working unchanged** — proved by the full suite passing
   with an unchanged pass count for every test that does not name this feature, plus the two
   criterion-6 guard tests staying green (one already exists from Stage 1:
   `RepositoryDefinitionTests`'s anonymous-CheckoutName proof; this stage adds the
   `LocalGitCheckoutTests` guard the design's Testing section names).

## Task ordering

Each task names its test(s) first, per TDD. Tasks 1–3 are pure domain/API and have no dependency on
4–7; 8 depends on 1 and 3 (needs `CheckoutName` resolution and the developer-config shape to exist);
9 depends on 1–5 (composition needs both producers). Samples/README/CHANGELOG (10) come last, once
the surface is stable. This matches the TaskCreate list already tracking this session's work
(tasks #4–#10 there map onto tasks 1–7 here).

---

### Task 1 — `RepositoryBuilder` and the narrowed/widened authoring API

**Tests first** (`test/.../Catalog/ServiceDefinitionBuilderTests.cs`, `RepositoryBuilderTests.cs` new):

- `RepositoryBuilder_DerivesNameFromUrl_StrippingDotGit`
- `RepositoryBuilder_ExplicitNameWins`
- `WithSharedRepository_TwoServices_ProduceTheSameRepositoryDefinitionInstance` (identity check —
  `ReferenceEquals`, the design's actual identity mechanism)
- `WithSharedRepository_ThenWithRepository_ThrowsAdditiveError` and the reverse order
- `WithRepository_NoLongerTakesProject_CompileTimeOnly` (the four existing `WithRepository(...,
  project: ...)` call sites in tests move to `.WithRepository(url).WithProject(...)`)
- `WithProject_CalledTwice_ThrowsAdditiveError`
- `WithPrepare_OnServiceWithSharedRepository_ThrowsNamingTheRepository` (the one runtime check the
  single-builder decision costs, per design "Errors on the chain")
- `RepositoryBuilder_WithPrepare_SetsPrepareOnTheSharedDefinition`

**Implementation** (`src/.../Catalog/RepositoryBuilder.cs` new; `ServiceCatalogBuilder.cs`;
`ServiceDefinitionBuilder.cs`):

- `RepositoryBuilder`: `[AspireExport(ExposeMethods = true)]`, sealed, internal constructor taking
  `(string url, string name, string? defaultRef)` — `name` already resolved (derived or explicit) by
  the time the constructor runs, so the type itself does no derivation. Carries `WithPrepare(...)`
  (same signature and validation as `ServiceDefinitionBuilder.WithPrepare`, factored into a shared
  private static helper — `PrepareMetadataFactory.Create(command, windowsCommand, mode, label)` in
  `Config/PrepareMetadata.cs` or a new internal type — to avoid duplicating the mode-validation
  block). Exposes an internal `Build()` that lazily constructs and **caches** the `RepositoryDefinition`
  instance (`??=`), so every service sharing this handle gets the same reference — this is the
  identity mechanism the design's "Architecture" section describes; nothing compares two identities,
  but `ServiceDefinitionBuilder.Build()` and the composition-time checks in Task 9 both rely on
  reference equality falling out of calling `Build()` once and caching it.
- `ServiceCatalogBuilder.AddRepository(string url, string? name = null, string? defaultRef = null)`:
  validates `url` non-blank; derives `name` from the URL's last path segment with a trailing `.git`
  stripped when `name` is null (reuse `GitUrl.Parse` if it already exposes the segment — check before
  writing a second URL parser); validates the resolved name via
  `LocalGitCheckout.IsContainedCheckoutDirectoryName` (#224 guard — a name is validated here even
  though a service's own name isn't validated until `ManagedRepoRoot` runs, because a repository name
  has no other gate before it becomes a directory); tracks repositories in a second
  `Dictionary<string, RepositoryBuilder>` (`_repositories`), throwing on a name collision naming both
  URLs and suggesting `name:`. Returns the `RepositoryBuilder`. `Freeze()` widens to also return the
  accumulated repositories (`(IReadOnlyDictionary<string, ServiceDefinition> Services,
  IReadOnlyDictionary<string, RepositoryDefinition> Repositories) Freeze()`) — every caller of
  `Freeze()` (just `CodeCatalogAccumulator.Freeze` today) widens with it.
- `ServiceDefinitionBuilder`: replace the two repository-related private fields with a single
  `_repositorySource` marker (`object?` — holds either the URL string or the `RepositoryBuilder`) used
  only for the additive `RequireUnset` guard shared between `WithRepository` and
  `WithSharedRepository`; keep separate `_repository`/`_defaultRef`/`_sharedRepository` fields for the
  actual values. `WithRepository(string url, string? defaultRef = null)` drops `project`.
  `WithProject(string project)` is new, additive-guarded on `_project`. `WithSharedRepository(
  RepositoryBuilder repository)` additive-guarded on `_repositorySource`. `Build()`: if
  `_sharedRepository is not null`, throw if `_prepare is not null` (the repository-handle
  configuration error, naming the service and the repository's name) and set `Repository =
  _sharedRepository.Build()` (the shared instance); else build the anonymous record exactly as Stage 1
  does.

**Test-file mechanical fallout**: the four `WithRepository(url, project: "...", defaultRef: "...")`
call sites in `ServiceDefinitionBuilderTests.cs` and `CatalogErrorMessageTests.cs` move to
`.WithRepository(url, defaultRef: "...").WithProject("...")`.

---

### Task 2 — Yaml `repositories:` / `repositoryRef:`

**Tests first** (`test/.../Config/ServiceCatalogLoaderTests.cs`):

- `Load_RepositoriesKeyWithNothingUnder_BindsToEmpty`
- `Load_UnknownKeyInsideRepositoriesEntry_Throws`
- `Load_RepositoryRefNamingNoRepositoriesEntry_ThrowsListingDeclaredNames`
- `Load_RepositoryAndRepositoryRefBothSet_ThrowsNamingBothKeys`
- `Load_DefaultRefBesideRepositoryRef_ThrowsSameAsAbove` (`defaultRef` now belongs to the repository)
- `Load_PrepareOnServiceCarryingRepositoryRef_ThrowsPointingAtRepositoriesEntry`
- `Load_TwoServicesShareRepositoryRef_ToDefinitionReturnsTheSameRepositoryDefinitionInstance`

**Implementation**:

- `RepositoryMetadata` (new, `Config/RepositoryMetadata.cs`): `Repository` (string, the URL — named to
  match the per-service `repository:` field, not `Url`, so the loader's reflection-derived property
  names line up with the yaml key `repository:`), `DefaultRef`, `Prepare`. Declared in the `Config`
  namespace like `PrepareMetadata`, so it participates in `ServiceCatalogLoader`'s existing nested-block
  unknown-key machinery automatically once wired below.
- `ServiceCatalog.Repositories`: `Dictionary<string, RepositoryMetadata>` with the same null-coercing
  setter `Services` has.
- `RawServiceCatalog.Repositories`: `Dictionary<string, Dictionary<string, object>>`, mirroring
  `Services`, for the same unknown-key-inside-a-repository-entry check the service loop already does.
- `ServiceCatalogLoader.Load`: after the existing per-service loop, a parallel loop over
  `catalog.Repositories` validating unknown top-level keys against
  `YamlPropertyNames(typeof(RepositoryMetadata))` (a new `KnownRepositoryProperties` set, same pattern
  as `KnownTopLevelProperties`) — no kind-block exemption, since a repository entry has no kind. Then,
  per service: if `RepositoryRef` is set, resolve against the repositories map (error naming the
  service, the ref, and every declared repository name if absent), refuse `Repository`/`DefaultRef`
  set alongside it (two separate checks, two separate messages per the design), refuse `Prepare` set
  alongside it. Repository name uniqueness against the `checkouts/` namespace (colliding with an
  ungrouped service's own name) is **not** checked here — that is Task 9's composition-time check,
  because it spans both catalogs and code-declared repositories too.
- `ServiceMetadata.RepositoryRef`: new `string?` property. `IsReservedKindName` picks this up for
  free (`KnownTopLevelProperties` is derived by reflection) — the CHANGELOG needs to say so (Task 7).
- `ServiceMetadata.ToDefinition` widens to
  `ToDefinition(string yamlPath, string serviceName, IReadOnlyDictionary<string,
  RepositoryDefinition> repositories)`. When `RepositoryRef` is set, `Repository =
  repositories[RepositoryRef]` (already validated to exist by the loader above — this method is not
  where that error is raised, keeping the "no filesystem/network access, composition-time only"
  property `PreparePlan.For` already documents for its own inputs). One `RepositoryDefinition` instance
  is minted per yaml `repositories:` entry, in `ServiceCatalogLoader.Load` or a caller just above it
  (repositories resolve per catalog, before the merge, per design "Composition, and the checkout") —
  **not** inside `ToDefinition`, so two services calling `ToDefinition` with the same `repositoryRef`
  get the identical instance rather than each minting their own.
- `ServiceCatalogLoader.Load`'s return type widens (or a new method alongside it does) to also hand
  back the resolved `IReadOnlyDictionary<string, RepositoryDefinition>` for this yaml file, mirroring
  `ServiceCatalogBuilder.Freeze()`'s widened return — `LoadedConfig.Load` (Task 9) needs both.

---

### Task 3 — Prepare moves to the repository

**Tests first** (`test/.../Prepare/PreparePlanTests.cs`, `CheckoutPreparationTests.cs`):

- `For_GroupedRepository_UsesRepositoryLabelInMessages` (the display-label rename — assert the
  rendered message names "repository 'monorepo'" rather than "service 'orders'" for a repository-level
  mode-parse failure)
- `CheckoutPreparationTests`: `TwoGroupedServices_RunTheStepOnceperTreePerCommit` (finding 3, the bug
  this closes) — first service's `CheckoutPreparation.Run` writes the marker, second service's
  `Decide` reads it and skips.
- `TwoGroupedServices_ConcurrentPrepare_SerializesRatherThanRacing` — see the concurrency note below;
  this is the one place this plan's implementation goes beyond what the design doc's prose states
  outright, so it is flagged for the plan-review round rather than asserted as settled.
- `PathMemberOfAGroup_RunsItsOwnBlockAndInheritsNothing` (already implied by existing behaviour once
  `definition.Repository.Prepare` is the repository's; confirm rather than newly implement)

**Implementation**:

- `PreparePlan.For`'s first parameter changes from `string serviceName` to `string label` (already
  formatted — `"service 'orders'"` / `"repository 'monorepo'"` — built by the caller via two small
  helpers, e.g. `PreparePlan.ServiceLabel(name)` / `PreparePlan.RepositoryLabel(name)`, so every
  interior message stays a substring of the design's own wording rather than each call site
  interpolating "Service" by hand). Every interior message in `PreparePlan.cs` that currently reads
  `$"Service '{serviceName}': ..."` becomes `$"{label}: ..."`. `IgnoredCatalogStepNotice`'s embedded
  JSON snippet (`"\"{serviceName}\": { ... }"`) is unaffected — that snippet is about the *developer's*
  file entry, always keyed by service name regardless of grouping, since a `path` service is never
  grouped (finding 4).
- `LocalProjectSource.Resolve`'s call site passes `PreparePlan.ServiceLabel(serviceName)` when
  `definition.Repository.CheckoutName == serviceName` (ungrouped — the common case, unchanged
  wording) and `PreparePlan.RepositoryLabel(definition.Repository.CheckoutName)` otherwise. This is
  the one place the "is this service grouped" test (`CheckoutName != serviceName`) is introduced; Task
  8's grouped-`local.ref` check reuses the identical test.
- `CheckoutPreparation.Run`/`WouldRun`/`Decide`/`Launch`/`Tag`/`FailedMessage`/`LaunchFailedMessage`:
  each currently takes `serviceName` for two purposes — the marker path (via
  `PrepareMarker.LocationFor(serviceName, ...)`, which **ignores** `serviceName` entirely for a managed
  checkout, using `repoRoot`/`.git` instead — see `PrepareMarker.cs:70`) and the message/tag text.
  Widen the same way `PreparePlan.For` does: replace `serviceName` with `label` throughout this file
  too, and pass `serviceName` separately **only** to `PrepareMarker.LocationFor`, which still needs a
  real service name for the `path`-checkout, per-service marker file case (`PrepareMarker.cs:73-83` —
  that branch is unreachable for a grouped repository, since `local.path` always de-groups a service,
  but the parameter itself stays a service name because the file name it produces must be one).
  Concretely: `CheckoutPreparation.Run(string serviceName, string label, PrepareStep step, ...)` —
  two string parameters where there was one, not a widened tuple, so every existing call site is a
  minimal two-argument diff rather than a signature restructure.
- **Concurrency**: `LocalGitCheckout.ReconcileRepoRoot` (a mutation of the shared working tree — fetch,
  checkout) and `CheckoutPreparation.Run` (a mutation via an arbitrary bootstrap command) can now both
  be invoked for the *same* `CheckoutName` from two different services' resolution paths. On the eager
  path this is not reachable — `AddService()` calls are sequential on the composition thread, so two
  grouped services' reconcile-then-prepare sequences never interleave. On the **deferred** path
  (`DeferredCheckout.StartDeferredAsync`, one `Task.Run` per deferred service, none awaited by the
  composition thread) it is reachable: two cold, deferred, grouped services can both reach
  `GetRepoRoot` → `ReconcileRepoRoot` and then `CheckoutPreparation.Run` concurrently against one
  directory. Before this stage that was impossible (every service had its own directory), so this is
  new exposure the design's prose does not call out by name (it treats the marker as sufficient,
  which handles *duplicate work* but not a *literal race* between two `git checkout`/two bootstrap
  commands against the same tree). **Proposed fix**: a small keyed async lock
  (`internal sealed class CheckoutNameLock` — a `ConcurrentDictionary<string, SemaphoreSlim>` behind a
  `LockAsync(string checkoutName)` returning an `IDisposable`) held by `LocalCheckoutPrefetch` across
  reconciliation, and separately acquired by the caller (`LocalProjectSource`/`DeferredCheckout`)
  around the `CheckoutPreparation.Run` call for the same `CheckoutName` — two acquisitions rather than
  one lock held end-to-end, because reconciliation happens inside `LocalCheckoutPrefetch` and prepare
  happens in its caller, and forcing one lock across that boundary would mean passing a held lock
  object through `LocalProjectSource`/`DeferredCheckout`, which is more invasive than two short
  critical sections. **This is a plan-review question, not a settled decision** — flag it explicitly
  in the review round rather than building it on the strength of this plan alone; the alternative is
  to accept the marker-file race as a known, narrow limitation (first-run-only, deferred-only, grouped
  services only, and the marker write is a rename so the failure mode is "prepare ran twice" rather
  than a corrupted marker) and document it instead of adding new locking machinery.

---

### Task 4 — The third developer-config shape

**Tests first** (`test/.../Config/DeveloperConfigValidatorTests.cs`, `DeveloperConfigurationTests.cs`):

- `Repositories_UnknownKey_ThrowsNamingRepositoryShape`
- `Repositories_NearMissSuggestion_SameMachineryAsServices`
- `LocalRef_OnGroupedService_ThrowsNamingServiceRepositoryAndWhereToSetItInstead` (criterion 4)
- `LocalRef_OnUngroupedService_Unaffected` (regression guard — the existing, still-legal case)
- `RepositoriesRef_ResolvesAheadOfDefaultRef`
- `RepositoriesPath_RedirectsTheWholeGroupsCheckout` (see the design note below — flagged for
  plan-review, same as Task 3's concurrency question)

**Implementation**:

- `RepositoryDeveloperConfig` (new, `Config/RepositoryDeveloperConfig.cs`): `Path`, `Ref`,
  `Prepare` (`PrepareDeveloperConfig?`), mirroring `LocalDeveloperConfig`'s three fields exactly —
  the design names this triple explicitly ("A third `DeveloperConfigShape`, over a new
  `RepositoryDeveloperConfig { Path, Ref, Prepare }`").
- `DeveloperConfigShape.Repository`: `Of<RepositoryDeveloperConfig>("Repository", "repository", [])`
  — empty `sourceNames`, since a repository entry has no `source` field to recognize a bare-value
  entry against.
- `DeveloperConfiguration.RepositoriesKey = "ServiceSources:Repositories"`; a new
  `Repositories: IReadOnlyDictionary<string, RepositoryDeveloperConfig>` property, read the same way
  `ReadFrom` reads `Services` — validated with `DeveloperConfigValidator.ValidateAll(section,
  DeveloperConfigShape.Repository)`, bound, blank-normalized, and canonicalized to the catalog's
  repository names. **This needs the composed repository-name set**, which (per Task 2/Task 9) is
  known only once code and yaml repositories are merged — so `ReadFrom` widens to take a second
  `IEnumerable<string> repositoryNames` parameter, supplied by `LoadedConfig.Load` (Task 9) once it
  has composed both catalogs' repositories, mirroring exactly how `catalogNames` is supplied today.
  `CanonicalizeToCatalog`'s logic is reused verbatim (extract it to a shape-agnostic helper taking the
  bound dictionary and the name set, called once for services and once for repositories) rather than
  duplicated.
- **Design question for review, same status as Task 3's**: does `RepositoryDeveloperConfig.Path`
  redirect the *whole group's* checkout to a local directory (symmetric with a service's own
  `local.path`, but at repository scope), with per-service `local.path` still available as the
  documented per-service escape (finding 4) that takes precedence over it? The design doc names the
  three fields but does not spell out `Path`'s semantics at repository scope in the same detail it
  gives `Ref` ("configured here, and only here") and `Prepare` (the whole "prepare moves to the
  repository" section). This plan assumes the symmetric reading — precedence, most to least specific:
  service's own `local.path` (full escape) → repository's `local.path` (whole-group redirect) →
  managed checkout at `checkouts/<CheckoutName>` with ref from repository `local.ref` ??
  `DefaultRef`. **Flag this explicitly to the plan reviewer**; if the intended scope is narrower
  (`Path` unused / reserved for a later stage), the field still needs to exist on the type for the
  shape to match the design's literal `{ Path, Ref, Prepare }`, but the resolution logic below would
  shrink to just `Ref` and `Prepare`.
- `LocalGitCheckout.ConfiguredReference`, `PrepareRepoRoot`, `ReconcileRepoRoot`, `ResolveRepoRoot`
  each widen with a new `RepositoryDeveloperConfig? repositoryConfig` parameter (nullable, `null`
  meaning "no repository-level entry" — the overwhelming common case for an ungrouped service, which
  never has one). `ConfiguredReference` becomes: grouped and `repositoryConfig?.Ref` set →
  that; else `config.Local.Ref ?? definition.Repository.DefaultRef` (ungrouped, unchanged) — with the
  grouped-`local.ref`-is-an-error check (criterion 4) raised in `PrepareRepoRoot` *before*
  `ConfiguredReference` is consulted, naming the service, `definition.Repository.CheckoutName`, and
  `ServiceSources:Repositories:{CheckoutName}:ref` as where to set it instead. If the `Path` semantics
  above are confirmed, `PrepareRepoRoot`'s `config.Local.Path is not null` branch grows an `else if
  (IsGrouped && repositoryConfig?.Path is not null)` branch reusing the same existing-directory
  validation, ahead of the managed-checkout branch.
- Callers (`LocalProjectSource.Resolve`, `LocalCheckoutPrefetch`) resolve
  `loaded.DeveloperConfig.Repositories.GetValueOrDefault(definition.Repository.CheckoutName)` once and
  thread it through — the same shape as `config` (`ServiceDeveloperConfig`) is already threaded.

---

### Task 5 — Re-key checkout/prefetch onto `CheckoutName`

**Tests first** (`test/.../Sources/LocalCheckoutPrefetchTests.cs`, `Git/LocalGitCheckoutTests.cs`):

- `Git/LocalGitCheckoutTests.UngroupedService_ManagedRepoRootIsExactlyCheckoutsService_BeforeAndAfter`
  — **the criterion-6 guard the design's Testing section names explicitly.** Written first, and kept
  green through every subsequent edit in this task — it is the one test whose failure means a
  developer's working tree gets silently orphaned.
- `LocalCheckoutPrefetchTests.FirstAddService_TwoGroupedServices_DownloadsItOnce` — replaces
  `FirstAddService_TwoServicesInOneRepository_DownloadsItTwiceConcurrently` (criterion 3; the
  `Barrier(2)` reasoning in the old test's comments is dropped with it, since there is now one task to
  wait on, not two racing ones).
- `LocalCheckoutPrefetchTests.TwoUngroupedServicesOneUrl_StillDownloadsTwice` (criterion 2 — the
  negative case that must keep failing to change).
- `LocalCheckoutPrefetchTests.FailedSharedCheckout_MessageNamesEveryServiceOnIt` (criterion 5).
- `DeferredCheckoutTests.ShouldDefer_GroupedService_ChecksTheSharedCheckoutDirectory`.

**Implementation**:

- `LocalGitCheckout.IsColdManagedCheckout(string appHostDirectory, string checkoutName,
  ServiceDeveloperConfig config)` — the `serviceName` parameter's *value* becomes `CheckoutName` at
  every call site; the parameter itself is renamed for clarity but the signature shape (three
  parameters) is unchanged. (Re-reading the design's "grows a parameter" line against the actual code:
  the growth is `DeferredCheckout.ShouldDefer`'s, not this method's — see below. This plan renames
  rather than widens `IsColdManagedCheckout`.)
- `DeferredCheckout.ShouldDefer(IDistributedApplicationBuilder builder, string serviceName,
  ServiceDefinition definition, ServiceDeveloperConfig config)` — genuinely widens (design: "both of
  ShouldDefer's callers already hold the definition, so it is a widening rather than plumbing").
  Passes `definition.Repository.CheckoutName` to `IsColdManagedCheckout`. Both call sites
  (`LocalProjectSource.Resolve:90`, `LocalCheckoutPrefetch.WouldBeDeferredIfAdded:606`) already hold
  `definition` in scope.
- `LocalGitCheckout.ManagedRepoRoot`: parameter renamed `serviceName` → `checkoutName` (a rename, not
  a signature change — still `(string appHostDirectory, string checkoutName)`); every call site passes
  `definition.Repository.CheckoutName`. Its error message ("Service '...' cannot be given a checkout")
  is reworded to drop "Service", since the same call can now name a repository
  (`"'{checkoutName}' cannot be given a managed checkout directory: ..."`) — this message is
  effectively unreachable in practice (both producers validate the name before it reaches here), so
  the reword is a correctness improvement with no behavioural stakes, not a compatibility question.
- `LocalGitCheckout.PrepareRepoRoot`, `ReconcileRepoRoot`, `ResolveRepoRoot`,
  `UseExistingCheckout`, `CloneIntoPlace`, `CheckoutWithFetchRetry`: each keeps `serviceName` (for
  every message — these are still reported per the *service* that asked, per criterion 5's spirit
  applied to the single-service case) **and** gains the `checkoutName` it resolves paths with — in
  practice `checkoutName` is read once, at the top of `PrepareRepoRoot`, as
  `definition.Repository.CheckoutName`, and threaded to `ManagedRepoRoot`. No other internal signature
  needs `checkoutName` separately, since `PreparedCheckout.RepoRoot` already carries the resolved path
  onward.
- `LocalCheckoutPrefetch._checkouts` and `_progress`: re-key from `Dictionary<string,
  Task<CheckoutResult>>`/`Dictionary<string, CheckoutProgress>` keyed by service name to keyed by
  `CheckoutName`. `_requested` and `_resolved` **stay keyed by service name** (design: "they answer
  'did the AppHost really ask for this', which is a question about a service"). Concretely:
  - `StartCheckout(serviceName, definition, ...)`: `_requested.Add(serviceName)` unchanged;
    `_checkouts` lookup/insert keyed on `definition.Repository.CheckoutName`.
  - `WatchCheckout(string serviceName, string checkoutName)` — widens to take both: `_progress` is
    keyed by `checkoutName` (one stream per checkout, shared by every service on it), but the "already
    resolved, hand back a completed stream" fast path in `WatchCheckoutLocked` needs to ask about
    `_resolved` which is per-service — so it must know both which stream to look up and which
    service's resolution state decides whether to short-circuit it. On reflection this is subtle
    enough to warrant its own decision at implementation time: the two call sites
    (`LocalCheckoutPrefetch.StartCheckoutTask` internally, and `DeferredCheckout.StartDeferredAsync:713`)
    both already hold both `serviceName` and `definition`, so widening costs nothing at the call sites.
  - `MarkResolved(serviceName)` unchanged (per-service).
  - `GetRepoRoot(serviceName, definition, config, repositoryConfig, appHostDirectory, gitClient)`:
    `_requested.Add(serviceName)` unchanged; `_checkouts` lookup keyed on `checkoutName`.
    `ReconcileRepoRoot` runs once **per service call**, even for a shared checkout — this is
    intentional and matches today's structure (reconciliation is cheap once the ref already matches,
    which it will for the second and later grouped services resolving after the first) rather than
    something to dedupe; only the *clone* is deduped, per design ("`_requested`/`_resolved` stay per
    service... A checkout is fully resolved once every requested service on it is").
  - `Run` (the speculative scan): `candidates` still enumerate by *service* (the developer config is
    per-service), but the dedup into `_checkouts` groups by `checkoutName` — two candidate services
    sharing a `checkoutName` must start **one** `StartCheckoutTask`, not two. Add a
    `HashSet<string> startedCheckouts` local to `Run`, checked before calling `StartCheckoutTask` for
    a candidate, so a second grouped candidate found later in the same enumeration does not start a
    redundant clone.
  - `FailedCheckoutMessage`/`ReportFailedCheckout`/`UnusedCheckoutsMessage`/`FailedUnusedCheckoutMessages`:
    these currently read `_checkouts`/`_requested` keyed by service name and produce one message per
    service. Once `_checkouts` is keyed by `checkoutName`, these need the **reverse index** (checkout
    name → every service that named it) to satisfy criterion 5 ("a failed shared checkout's notice
    names every service on it"). Build this from `_requested` filtered by
    `definition.Repository.CheckoutName == checkoutName` at report time (`ReportSpeculativeWork` /
    `UnusedCheckouts` already run once, at `BeforeStartEvent`, so this does not need to be
    incrementally maintained) — requires these methods to also retain each checkout's originating
    `ServiceDefinition`s, which they do not need today (only the service *name*). Simplest shape: keep
    a small `Dictionary<string, List<string>> _servicesOnCheckout` maintained alongside `_checkouts`
    (appended to in `StartCheckout` and the `Run` loop, both of which already know the service name
    and the checkout name at the point they'd write it).
- `LocalProjectSource.Resolve`: `repoRoot = prefetch.GetRepoRoot(serviceName, definition, config,
  repositoryConfig, builder.AppHostDirectory, gitClient)` — widened call, `repositoryConfig` resolved
  once near the top (Task 4).

---

### Task 6 — Composition checks and the ungrouped-collision warning

**Tests first** (`test/.../Catalog/CatalogCompositionTests.cs`):

- `RepositoryName_CollidesWithUngroupedServiceName_ThrowsNamingBoth`
- `RepositoryName_DeclaredInBothCatalogs_ThrowsDuplicateError`
- `CodeService_CannotReachYamlRepositoryRef` (repositories resolve per catalog, before the merge —
  already true by construction once Task 2/Task 1 keep code and yaml repository maps separate through
  `LoadedConfig.Load`; this test is a regression guard, not new production code)
- `TwoUngroupedServices_OneUpstream_WarnsSuggestingGrouping` (question 5's decided "warn" — a
  suppressible notice, buffered to `BeforeStartEvent` the same way `ServiceSourcesWarnings` already
  buffers the `path`-checkout ignored-prepare notice, per `LocalProjectSource.cs:69`)

**Implementation** (`Config/ServiceSourcesConfigCache.cs`'s `LoadedConfig.Load`):

- After freezing the code catalog (now returning `(Services, Repositories)`) and loading the yaml
  catalog (now also returning its resolved repositories map), before merging services: build the
  combined `CheckoutName` → owning declaration(s) map across **both** catalogs' repositories plus
  every ungrouped service's own name, and check for a collision — two different `RepositoryDefinition`
  instances (or a repository and an ungrouped service) resolving to the same `CheckoutName`. This is
  "One namespace, checked once" from the design. Raised as a `ServiceSourcesConfigurationException`
  naming both declarations and suggesting `name:`.
- Repository-name-declared-in-both-catalogs: a plain key collision between the code repositories dict
  and the yaml repositories dict, checked the same way the existing service duplicate check works
  (`LoadedConfig.Load:292-301`), same message shape.
- The **ungrouped-collision warning** (question 5): after composition, group every *ungrouped*
  service (`definition.Repository.CheckoutName == serviceName`, i.e. not sharing any handle) by
  `definition.Repository.Url`; for every URL named by two or more such services, buffer a notice via
  the existing `ServiceSourcesWarnings` mechanism (`LocalProjectSource.cs:69`'s pattern —
  `ServiceSourcesWarnings.For(builder).AddNotice(...)`) — but this is decided at **composition** time
  (`LoadedConfig.Load`), which runs before a builder reference is threaded that deep today. Check
  whether `ServiceSourcesWarnings.For` takes a builder or something composition already holds; if not,
  this notice may need to move to a point that does have the builder — most likely
  `ServiceSourcesConfigCache.LoadedFor`, which does. **Confirm the exact call site during
  implementation**; this is mechanical once the right seam is found, not a design question.

---

### Task 7 — Samples, README, CHANGELOG

- Both code-catalog samples (`samples/DemoAppHostCodeCatalog/Program.cs`,
  `samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts`) gain a grouped repository (two services
  sharing one `AddRepository`/`addRepository` handle) — these are also the only callers of the
  narrowed `WithRepository`'s dropped `project:` parameter outside the tests, so they **must** be
  updated for that alone; the `📘 typescript export surface` CI job builds the `.mts` file, so a
  missed update fails CI rather than shipping silently.
  - Read both files' current content before editing — not summarized above, so this task starts with
    a `Read` of each rather than working from this plan's description alone.
- `samples/DemoAppHostTypeScript/servicesources.yaml` gains a `repositories:`/`repositoryRef:` pair
  (the design names this file specifically, distinct from `samples/DemoAppHost/servicesources.yaml`,
  which is the plain-yaml, non-TypeScript sample and is not required to change).
- README: the monorepo shape presented as **the** way to express several services in one repository,
  in both the code and yaml catalog sections — not an advanced variant (design: "the opt-in is only
  real if the docs push toward it"). Plus `local.path` documented as the per-service escape from a
  group, and the `repositories:` block documented in the `servicesources.local.json` reference
  section.
- CHANGELOG: the feature, `#66` fixed (`Fixes #66` / `Closes #66` per this repo's PR-autoclose
  convention), the `repositoryRef` kind-name narrowing (a silent public-behaviour change, per design),
  and — per this repo's established convention — a plain statement that an existing catalog keeps its
  per-service checkouts until rewritten, so the fix is not read as automatic.

---

## Open questions for the plan-review round

1. **The concurrency question (Task 3)**: does the deferred path's newly-possible concurrent
   reconcile/prepare against one shared checkout need new locking, or is it an acceptable, narrow,
   documented limitation? This plan defaults to "add a keyed lock" but flags it because the design
   doc's own prose does not call for one.
2. **`RepositoryDeveloperConfig.Path`'s semantics (Task 4)**: whole-group redirect (this plan's
   assumption) versus reserved/no-op for this stage.
3. **Where the ungrouped-collision warning (Task 6) actually gets buffered**, since `LoadedConfig.Load`
   does not obviously hold a builder reference today — needs five minutes with the actual code to
   settle, not a design call, but worth flagging so the reviewer isn't surprised if the eventual PR's
   call site differs from what's described here.

## Verify legs (from `.github/workflows/ci.yml`)

- `dotnet build -c Release --no-restore -warnaserror` then `dotnet test -c Release --no-build` — the
  main leg, run every round.
- `📦 local source smoke test` (`scripts/smoketest-local-source.sh`) — exercises real clones against a
  local git remote; the one leg that would catch a real checkout/prefetch re-keying regression outside
  the unit suite. Expensive (up to 35 minutes); run once before the PR and on any round that touches
  `Git/`, `Sources/`, or `Prepare/`.
- `📘 typescript export surface` (`npx tsc --noEmit`) — must pass once the samples (Task 7) are
  updated; also the leg that would catch an ATS export mistake in `RepositoryBuilder`/
  `WithSharedRepository` structurally, though `CatalogExportsTests` (Task 1) is the faster local
  signal for that.
- `🤖 verify repo invariants` — unaffected by this stage (no new NuGet dependency, no dependabot
  change) but cheap; include anyway.
- `🐳 container source smoke test` / `🪜 config layers smoke test` — not touched by this stage's
  surface; run once before the PR for completeness, not every round.
