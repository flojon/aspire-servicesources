# #291 Stage 2 implementation plan — the authoring API, yaml, developer config, and the checkout re-keying

**Status:** Reviewed 2026-09-09 — one round (fresh reader, code-grounded). Must-fix findings folded in
below; the three open questions are settled (see "Open questions" at the end, now answered rather than
open).
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
9 depends on 1–5 (composition needs both producers). Samples/README/CHANGELOG (Task 7) come last, once
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
- `WithRepository_NoLongerTakesProject_CompileTimeOnly` (the real `project:` call sites move to
  `.WithRepository(url).WithProject(...)` — verified by grep against this commit, there are three, not
  the design doc's "four": `test/.../Catalog/ServiceDefinitionBuilderTests.cs:13`,
  `samples/DemoAppHostCodeCatalog/Program.cs:16-19`,
  `samples/DemoAppHostTypeScriptCodeCatalog/apphost.mts:9-12`. `CatalogErrorMessageTests.cs:147` —
  the design's other named file — calls `WithRepository` with no `project:` argument, so it is
  unaffected by the narrowing)
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
  but `ServiceDefinitionBuilder.Build()` and the composition-time checks in Task 6 both rely on
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

**Test-file mechanical fallout**: `ServiceDefinitionBuilderTests.cs:13`'s
`WithRepository(url, project: "...", defaultRef: "...")` moves to
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
  as `KnownTopLevelProperties`) — no kind-block exemption, since a repository entry has no kind.
  **Also validate unknown keys nested inside a repository entry's own blocks** (e.g. a typo inside
  `repositories: monorepo: prepare: { comand: [...] }`) — `KnownNestedProperties`
  (`ServiceCatalogLoader.cs:29-35`) is derived only from `ServiceMetadata`'s properties today, so a
  bare port of the existing per-service loop would silently accept that typo. Add a second
  `KnownNestedProperties`-equivalent derived from `RepositoryMetadata` (or widen the existing one to
  be keyed by declaring type, since `prepare:`'s nested keys are identical either way — it is the same
  `PrepareMetadata` type in both places) and run the same nested-key walk over
  `raw.Repositories[name]`. Then,
  per service: if `RepositoryRef` is set, resolve against the repositories map (error naming the
  service, the ref, and every declared repository name if absent), refuse `Repository`/`DefaultRef`
  set alongside it (two separate checks, two separate messages per the design), refuse `Prepare` set
  alongside it. Repository name uniqueness against the `checkouts/` namespace (colliding with an
  ungrouped service's own name) is **not** checked here — that is Task 6's composition-time check,
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
  `ServiceCatalogBuilder.Freeze()`'s widened return — `LoadedConfig.Load` (Task 6) needs both.

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
- **`Prepare/PrepareMode.cs`'s `PrepareModes.Parse(string serviceName, string? written, string
  writtenAt)` is a second, independent message site the first draft of this plan missed** — it
  hardcodes `$"Service '{serviceName}': {writtenAt} is '{written}'..."` at `PrepareMode.cs:100-103`,
  and is `PreparePlan.For`'s `ParseOptional` helper's only caller
  (`PreparePlan.cs:253`). Its first parameter renames to `label` exactly like `PreparePlan.For`'s does,
  and its one other caller — `CheckoutPreparationTests.cs:108`, which calls it directly — updates to
  pass a formatted label. (`PrepareModes.Written` is unaffected: it takes no name/label at all.)
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
- **Concurrency — settled, see "Open questions" below.** `LocalGitCheckout.ReconcileRepoRoot` (fetch,
  checkout) and `CheckoutPreparation.Run` (an arbitrary bootstrap command) can now both be invoked for
  the *same* `CheckoutName` from two different deferred services' background tasks
  (`DeferredCheckout.StartDeferredAsync`), which is new exposure this stage introduces. Add
  `CheckoutNameLock` (a small keyed async lock beside `LocalCheckoutPrefetch`) and acquire it in
  `DeferredCheckout.StartDeferredAsync`, held across the span from `deferred.Prefetch.GetRepoRoot(...)`
  through the `CheckoutPreparation.Run` call (`DeferredCheckout.cs:722-786`), keyed on
  `deferred.Definition.Repository.CheckoutName`. Take the same lock in `LocalProjectSource.Resolve`
  around the equivalent span too, even though the eager path cannot race today (`AddService()` calls
  are sequential on the composition thread) — cheap, and keeps the invariant true independent of call
  order rather than true only by the current shape of the two callers.

---

### Task 4 — The third developer-config shape

**Tests first** (`test/.../Config/DeveloperConfigValidatorTests.cs`, `DeveloperConfigurationTests.cs`):

- `Repositories_UnknownKey_ThrowsNamingRepositoryShape`
- `Repositories_NearMissSuggestion_SameMachineryAsServices`
- `LocalRef_OnGroupedService_ThrowsNamingServiceRepositoryAndWhereToSetItInstead` (criterion 4)
- `LocalRef_OnUngroupedService_Unaffected` (regression guard — the existing, still-legal case)
- `RepositoriesRef_ResolvesAheadOfDefaultRef`
- `RepositoriesPath_NonNull_ThrowsReservedError` (see the settled decision below — this replaces
  `RepositoriesPath_RedirectsTheWholeGroupsCheckout` from this plan's first draft)

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
  repository names. **This needs the composed repository-name set**, which (per Task 2/Task 6) is
  known only once code and yaml repositories are merged — so `ReadFrom` widens to take a second
  `IEnumerable<string> repositoryNames` parameter, supplied by `LoadedConfig.Load` (Task 6) once it
  has composed both catalogs' repositories, mirroring exactly how `catalogNames` is supplied today.
  `CanonicalizeToCatalog`'s logic is reused verbatim (extract it to a shape-agnostic helper taking the
  bound dictionary and the name set, called once for services and once for repositories) rather than
  duplicated.
- **`RepositoryDeveloperConfig.Path` — settled, see "Open questions" below: reserved, not a
  whole-group redirect.** The field exists on the type (the design names the triple literally), but a
  non-null value is rejected as a configuration error — naming the repository and stating that a
  group's checkout is not yet redirectable at the repository level, with per-service `local.path` on
  each member named as the way to split them out individually today. Design finding 4 is explicit that
  the per-service escape is the *only* one ("No new field, no repository-level list of exceptions"),
  which is why this plan no longer implements a redirect. Resolution logic below therefore only reads
  `Ref` and `Prepare` from a repository's developer-config entry.
- `LocalGitCheckout.ConfiguredReference`, `PrepareRepoRoot`, `ReconcileRepoRoot`, `ResolveRepoRoot`
  each widen with a new `RepositoryDeveloperConfig? repositoryConfig` parameter (nullable, `null`
  meaning "no repository-level entry" — the overwhelming common case for an ungrouped service, which
  never has one). `ConfiguredReference` becomes: grouped and `repositoryConfig?.Ref` set →
  that; else `config.Local.Ref ?? definition.Repository.DefaultRef` (ungrouped, unchanged) — with the
  grouped-`local.ref`-is-an-error check (criterion 4) raised in `PrepareRepoRoot` *before*
  `ConfiguredReference` is consulted, naming the service, `definition.Repository.CheckoutName`, and
  `ServiceSources:Repositories:{CheckoutName}:ref` as where to set it instead. `PrepareRepoRoot` also
  raises the reserved-field error for a grouped repository whose developer-config entry sets
  `repositoryConfig?.Path is not null` (see the settled decision above), checked alongside the
  `local.ref`-on-grouped-service check, before either the managed-checkout branch or
  `ConfiguredReference` runs.
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
    redundant clone. **Also fix the existing `.Where(candidate => LocalGitCheckout.IsColdManagedCheckout(
    appHostDirectory, candidate.Name, candidate.Config))` filter** (`LocalCheckoutPrefetch.cs:548-549`)
    — it is keyed on `candidate.Name` (the service name) today, and must key on
    `candidate.Definition.Repository.CheckoutName` instead, or a warm grouped checkout (second and
    later grouped candidate, directory already exists from the first) is misjudged "cold" and an
    unnecessary speculative clone task is launched for it. The outcome is harmless — `PrepareRepoRoot`
    no-ops against an existing `.git` — but it is wasted background work and worth fixing alongside
    the rest of this method's re-keying rather than leaving as a latent inefficiency.
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
- The **ungrouped-collision warning** (question 5) — **settled, see "Open questions" below**: after
  composition, group every *ungrouped* service (`definition.Repository.CheckoutName == serviceName`,
  i.e. not sharing any handle) by `definition.Repository.Url`; for every URL named by two or more such
  services, buffer a notice via the existing `ServiceSourcesWarnings` mechanism
  (`LocalProjectSource.cs:69`'s pattern — `ServiceSourcesWarnings.For(builder).AddNotice(...)`),
  called directly inside `LoadedConfig.Load` — which already receives `builder` as its own parameter
  (`ServiceSourcesConfigCache.cs:250`), so no new plumbing is needed to reach it.

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

## Open questions — settled after one review round

All three were put to a fresh reader with the actual code in front of them. Recorded here as
decisions, not as open items; the corresponding task sections above are amended to match.

1. **The concurrency question (Task 3): settled — add one lock, one acquisition.** The race is real
   (verified against `LocalCheckoutPrefetch.ResolveRequestedRepoRoot`, `UseExistingCheckout`, and
   `CheckoutPreparation.Run`): the deferred path's `Task.Run`-per-service means two grouped, cold,
   deferred services can call `ReconcileRepoRoot` (a real `git fetch`/`checkout`) and then
   `CheckoutPreparation.Run` (an arbitrary bootstrap command) concurrently against one directory,
   before either marker exists — this is new exposure Stage 2 introduces, not a pre-existing one.
   Given this codebase's existing rigor about concurrent-checkout races (`CloneIntoPlace`'s atomic
   rename dance is exactly this kind of care applied to the clone half), a lock is warranted. **Simpler
   than this plan's first draft**: one acquisition, not two — in `DeferredCheckout.StartDeferredAsync`,
   wrapping the span from `deferred.Prefetch.GetRepoRoot(...)` through the `CheckoutPreparation.Run`
   call (`DeferredCheckout.cs:722-786`) in a single keyed lock held for that whole span, keyed on
   `deferred.Definition.Repository.CheckoutName`. The eager path (`LocalProjectSource.Resolve`) never
   needs the lock — `AddService()` calls are sequential on the composition thread, so two grouped eager
   services never interleave there — but taking the same lock there too, briefly, costs nothing and
   keeps the invariant ("reconcile+prepare for one checkout never runs twice at once") true regardless
   of which path a future change might route through. A small keyed-lock utility
   (`internal sealed class CheckoutNameLock`, a `ConcurrentDictionary<string, SemaphoreSlim>` behind an
   async `LockAsync(string checkoutName)` returning an `IAsyncDisposable`) lives beside
   `LocalCheckoutPrefetch`, which already owns the per-checkout-name state this pairs with.
2. **`RepositoryDeveloperConfig.Path`'s semantics (Task 4): settled — reserved, not a whole-group
   redirect.** The design's own finding 4 is explicit and points the other way from this plan's first
   draft: *"`local.path` is the documented escape from a group. **No new field**, no repository-level
   list of exceptions."* A whole-group `Path` override would be exactly such a new field. The type still
   declares `Path` — the design's developer-config section names the triple `{ Path, Ref, Prepare }`
   literally, so the shape has to exist — but its resolution logic does **not** implement a redirect:
   a non-null `RepositoryDeveloperConfig.Path` is rejected as a configuration error naming the
   repository and stating that a group's checkout is not yet redirectable at the repository level —
   use a per-service `local.path` on each member to split them out individually instead. This is a
   forward-compatible reservation (the field parses and is schema-valid, so a later stage can implement
   it without a wire-format change) rather than a silent no-op, which would leave a developer writing
   `"repositories": {"monorepo": {"path": "..."}}}` with no signal that nothing happened.
3. **Where the ungrouped-collision warning (Task 6) gets buffered: settled, was never actually
   open.** `ServiceSourcesConfigCache.LoadedConfig.Load` (`ServiceSourcesConfigCache.cs:250`) already
   receives `IDistributedApplicationBuilder builder` as its parameter — the same builder
   `ServiceSourcesWarnings.For(builder).AddNotice(...)` needs, and the same pattern
   `LocalProjectSource.cs:69` already uses for the `path`-checkout ignored-prepare notice. The notice is
   buffered directly inside `Load`, after composing the merged services and their repositories, with no
   new plumbing required.

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
