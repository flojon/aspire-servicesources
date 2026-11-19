# One source-owned requirements model (#504) Implementation Plan

> For agentic workers: implement task by task, test first (red, then green), one commit per task. The build must be
> green (`dotnet build -c Release -warnaserror` and the task's test filter) after every commit.

**Goal:** Each source declares its follow-up fields and checks them once. The picker, the save validator, the
error-message switch offer and `Resolve` all read that one `Check`, so they cannot disagree.

**Spec:** `docs/superpowers/specs/2026-11-19-504-source-owned-requirements-design.md` (Approved). D1-D9 and Resolved
Questions 1-6 are referenced below.

**Architecture:** new `Sources/SourceRequirements.cs` (model types) and `Sources/ServiceSource.cs` (template base
class). Six sources derive from the base class one at a time while the old `IsOfferedFor`/`CanResolve` stay on the
interface (as thin delegates to the new model where a source had its own). Then the interface gains
`Requirements`/`IsSwitchRemedy`, the consumers switch, and the old members and `SourceFollowUp` are deleted. No
public API change: `PublicAPI.*.txt` stay untouched (the build has no RS0016/RS0017).

**Legacy delegates (Tasks 5-9):** where a source already overrides `IsOfferedFor`/`CanResolve`, keep the member as a
one-line delegate: `Requirements(new SourceInput("", definition, config-or-new ServiceDeveloperConfig(), "")).Offered`
(or `.Resolves`). They exist only so the interface and its consumers compile until Task 10c.

**Test placement and traits:** new pure classes carry no trait. A test that creates a directory or a builder over one
(`TestHelpers.CreateBuilder(TempDirectories...)`, as `LocalProjectSourceTests.PlainBuilder`) is in a class carrying
`[Trait("IO", "true")]`. No new `Concurrency` or `Timing` is expected.

**Verify commands:**
- Build: `dotnet build -c Release -warnaserror`
- Task test run: `dotnet test test/Aspire.Hosting.ServiceSources.Tests -c Release --filter "<per task>"`
- After tasks 7, 9, 10c, 11, 12 and 13: the full suite `dotnet test -c Release --no-build`.
- Once before the PR: the `dotnet pack` leg from the notes file.

---

## Task 1: Tag an exception with the setting it is missing (D4)

**Test first** (new `ServiceSourcesConfigurationExceptionTests`, pure, `Config` folder or beside the existing
exception tests):
- `AsMissingSetting_TagsTheKey_AndReturnsTheSameException` (`MissingSetting == "kubernetes:context"`).
- `MissingSetting_OnAnUntaggedException_IsNull`.
- `AsMissingSetting_DoesNotChangeTheMessage`.
- `AsMissingSetting_TagHoldsOnlyTheKey` (the only `Data` entry added is the key; no message or value text).

**Implement** (`ServiceSourcesConfigurationException.cs`, beside `AsUnsettledResourceType`): `internal
ServiceSourcesConfigurationException AsMissingSetting(string key)` and `internal string? MissingSetting`, over a
`Data` key `"ServiceSources.MissingSetting"`.

**Verify:** filter `FullyQualifiedName~ServiceSourcesConfigurationExceptionTests`; build.

## Task 2: The requirements model types (D2, D7)

**Test first** (new `SourceRequirementsModelTests`, pure):
- `Resolves_IsTrueOnlyWithoutAProblem`.
- `Offered_WhenNoProblem_IsTrue`.
- `Offered_WhenProblemMissesAFieldTheDialogAsks_IsTrue` (problem tagged `url:url`, `Fields` has that key).
- `Offered_WhenProblemMissesASettingNoFieldCovers_IsFalse` (tagged `container:image`, no such field).
- `Offered_WhenProblemIsUntagged_IsFalse`.
- `ResolvesImpliesOffered_OverEveryCombinationOfFieldsAndProblem` (a small matrix: problem null/tagged/untagged times
  fields empty/matching): pins the `Problem is null` disjunct of `Offered`.
- `Capture_ReturnsNull_WhenTheCheckReturns`.
- `Capture_WrapsAConfigurationException_WithItsMessageAndTag`.
- `Capture_LetsAnyOtherExceptionPropagate`.
- `Unknown_NeitherResolvesNorIsOffered_AndHasNoFields`.

**Implement** (`Sources/SourceRequirements.cs`): `SourceInput`, `SourceRequirements` (with `Resolves`, `Offered`,
`Unknown`), `SourceProblem` (with `Message`, `MissingSetting`, `Capture(Action)`), `NoValues`. Move the
`FollowUpField` record here out of `SourceFollowUp.cs` (same namespace, nothing else changes).

**Verify:** filter `FullyQualifiedName~SourceRequirementsModelTests`; build.

## Task 3: The template base class (D3)

**Test first** (new `ServiceSourceTests`, class trait `IO` since it needs a builder; the test subclass
`RecordingSource : ServiceSource<int>` records the order of its calls):
- `Resolve_RunsCheckOnce_ThenBuildWithTheCheckedValue`.
- `Resolve_WhenCheckThrows_NeverCallsBuild`.
- `Requirements_RunsCheck_NeverBuild`.
- `Requirements_CarriesFieldsForThisDefinition_AndTheCheckProblem`.
- `Resolve_PassesTheRepositoryConfigAndAppHostDirectoryIntoTheInput`.
- `Resolve_IsSealed` (reflection: `typeof(ServiceSource<int>).GetMethod("Resolve")!.IsFinal`).

**Implement** (`Sources/ServiceSource.cs`): `ServiceSource<TChecked> : IServiceSource` with protected abstract
`FieldsFor`, `Check`, `Build`, `public abstract bool IsSwitchRemedy`, `public SourceRequirements
Requirements(SourceInput)`, and a `Resolve` that is sealed over `Build(builder, input, Check(input))`. Document on
`Check`: no side effect, no builder, no network/process/git/write, only configuration checks (the rule the spec
accepts in place of a type-level guarantee). `IServiceSource` itself is **not** touched yet: `Requirements` and
`IsSwitchRemedy` are the base class's own public members until Task 10, which is what keeps every commit between here
and there compiling.

**Verify:** filter `FullyQualifiedName~ServiceSourceTests`; build.

## Task 4: `DisabledSource` derives from the base (D6, D8)

**Test first** (new `SourceRequirementsTests`, the class that holds the per-source expectations; `IO`-free
because it never builds):
- `Disabled_RequirementsHaveNoFields_AndResolve`.
- `Disabled_IsNotASwitchRemedy`.

Existing tests that resolve a disabled service (`DisabledConsumerWaitTests`, `AddServiceTests`) are the regression
net for `Resolve`.

**Implement:** `DisabledSource : ServiceSource<NoValues>`; `FieldsFor` empty, `Check` returns `default`, `Build` is
today's `Resolve` body, `IsSwitchRemedy => false`.

**Verify:** filter `FullyQualifiedName~SourceRequirementsTests|FullyQualifiedName~DisabledConsumerWaitTests`; build.

## Task 5: `UrlSource` derives from the base

**Test first** (`SourceRequirementsTests`):
- `Url_BlankCatalogUrl_AsksUrlField_AndTheProblemIsTheMissingUrlSetting` (`Fields` = `url:url` required; `Problem.MissingSetting == "url:url"`; `Offered`, not `Resolves`).
- `Url_CatalogUrlPresent_NoFields_Resolves`.
- `Url_DeveloperUrlOnly_Resolves` (the config value counts, as it does at `Resolve`).
- `Url_FtpCatalogUrl_ProblemIsUntagged_NotOffered`.
- `Url_ProblemMessage_EqualsWhatResolveThrows_AndRedactsCredentials` (build both from the same definition; compare
  messages; uses a `redis://user:pw@host` value).
- `Url_IsASwitchRemedy`.

Existing `UrlSourceTests`, `UrlContainerReferenceTests` stay green unchanged (`Resolve` regression net).

**Implement:** `UrlSource : ServiceSource<Uri>`. `Check` is `ResolveUrl(...)`; the blank-URL branch of `ResolveUrl`
adds `.AsMissingSetting("url:url")`. `FieldsFor` takes the `url:url` rule from `SourceFollowUp`. `IsSwitchRemedy =>
true`. The old `CanResolve` becomes `Requirements(...).Resolves` (a delegate; deleted in Task 10).

**Verify:** filter `FullyQualifiedName~SourceRequirementsTests|FullyQualifiedName~UrlSourceTests|FullyQualifiedName~LocalProjectSourceTests`; build.

## Task 6: `ContainerSource` derives from the base, with one copy of the container rules (D5)

**Test first** (`SourceRequirementsTests`):
- `Container_NoImage_ProblemIsUntagged_NotOffered`; `..._NoPort_...`; `..._PortOutOfRange_...`: all three assert
  `Offered == false` and that `Problem.Message` equals what `ContainerSource.ResolveContainerConfig` throws for the
  same definition (this is what proves there is one copy).
- `Container_ImageAndPort_ResolvesWithNoFields`.
- `Container_UnsupportedCatalogScheme_NotOffered` (the new case spec section 5 item 7 names).
- `Container_IsASwitchRemedy`.

Existing `ContainerSourceTests` (`ResolveContainerConfig_*`) and `SourceChoicesTests.For_*Container*` stay green
unchanged.

**Implement:** `ContainerSource : ServiceSource<ContainerChecked>` (record of image, tag, port, scheme). `Check` is
`ResolveContainerConfig` then `EndpointScheme.Resolve`; `ResolveContainerConfig`'s missing-image and missing-port
branches add `.AsMissingSetting("container:image")` / `("container:port")`. **Delete** `TryGetImageAndPort`,
`ContainerConfigProblem`, the private `Validate`. `IsOfferedFor` and `CanResolve` become delegates to
`Requirements(...)` (`Offered`, `Resolves`), deleted in Task 10.

**Verify:** filter `FullyQualifiedName~ContainerSourceTests|FullyQualifiedName~SourceChoicesTests|FullyQualifiedName~SourceRequirementsTests|FullyQualifiedName~CatalogErrorMessageTests`; build.

## Task 7: `KubernetesSource` derives from the base

**Test first** (`SourceRequirementsTests`, and one in `KubernetesSourceTests` for the agreement):
- `Kubernetes_WithoutCatalogPort_AsksContextPortNamespaceScheme` and `Kubernetes_WithCatalogPort_DoesNotAskPort` and
  `Kubernetes_PortIsNumberInput_OthersText` (the three `SourceFollowUpTests` kubernetes cases, with the same
  expectations, over `Requirements`).
- `Kubernetes_EmptyConfig_ProblemIsMissingContext_Offered`.
- `Kubernetes_NoServiceName_ProblemIsUntaggedOrNonAskable_NotOffered` (tag `kubernetes:service`, no field).
- `Kubernetes_UnsupportedCatalogScheme_NotOffered` (reported before the context, so it is not hidden behind it: the
  ordering rule of D2).
- `Kubernetes_ContextAndPortGiven_Resolves`.
- `Kubernetes_ProblemMessage_EqualsWhatResolveThrows` and `..._ResolvesIffResolveReturns` over a sample matrix, with
  a fake port allocator (`FakePortAllocator`-style, see `KubernetesSourceTests`).
- `Kubernetes_Requirements_NeverAllocatesAPort` (counting allocator).
- `Kubernetes_IsASwitchRemedy`.

Existing `KubernetesSourceTests.BuildPortForwardArgs_*`, `EndpointMutationDetectorTests`, `MigratedSiteEscapingTests`
stay green: `BuildPortForwardArgs` keeps its signature.

**Implement:** `KubernetesSource(IPortAllocator) : ServiceSource<KubernetesChecked>` (scheme, context, remote port,
block). `Check` is today's `Validate` returning what it computed (block, scheme, forward target), with the three
missing branches tagged `kubernetes:service`, `kubernetes:context`, `kubernetes:port`. `Build` allocates the port and
builds the executable (the existing `Resolve` body after its two checks). `FieldsFor` takes the kubernetes rule from
`SourceFollowUp`. `IsOfferedFor`/`CanResolve` become delegates. `HasService` stays (used by `RequireKubernetesBlock`).

**Verify:** the filter of Task 6 plus `FullyQualifiedName~KubernetesSourceTests|FullyQualifiedName~EndpointMutationDetectorTests|FullyQualifiedName~MigratedSiteEscapingTests`; then the full suite.

## Task 8: `PathSource` derives from the base (D8, Resolved Questions 1 and 6)

**Test first** (`SourceRequirementsTests` for fields and problems; `PathSourceTests` for precedence and the single
resolution; the `PathSourceTests` class already carries `IO`/`Concurrency`):
- `Path_BlankCatalogPath_AsksPathField_ProblemIsTheMissingPathSetting` (`path:path`, offered, message equals
  `RequireCatalogPath`'s).
- `Path_CatalogPathPresent_NoFields_ResolvesForTheEmptyConfig` (no filesystem touched: assert with a catalog path to
  a directory that does not exist).
- `Path_OverrideToMissingDirectory_ProblemIsResolveOverridesMessage` (`IO`: temp AppHost directory).
- `Path_OverrideToExistingDirectory_Resolves`.
- `Path_GroupRedirect_BlankCatalogPath_DoesNotAskOrFail` (a `RepositoryConfig` whose source is `path`, a grouped
  member: `Check` passes, as `Resolve` does).
- `Path_Resolve_WithOverride_UsesTheCheckedDirectory` (`Resolve` over an override directory holding the project
  resolves from it; the existing override tests in `PathSourceTests` stay green and are the regression net for `Build`
  no longer calling `ResolveOverride` itself).
- `Path_BlankCatalogPathAndTypoKind_ReportsTheMissingPathFirst`: pins the precedence change of Resolved Question 6.
- `Path_IsNotASwitchRemedy`.

**Precedence:** `Check` now runs before the kind-handler lookup and project validation that `Resolve` did first. Run
`PathSourceTests` and `GroupPathSourceTests`: any test red for that reason alone (an entry with both a typo'd
kind/missing project and no directory expecting the kind or project error) is updated to give the entry a valid
directory so it still pins what it was written for; the commit message names each such test and says the reason
(the missing directory is now reported first, per the approved precedence change). A test red for any other reason is
a defect in this task: fix the code.

**Implement:** `PathSource(...) : ServiceSource<string?>` (the checked override directory, or `null`). `Check`:
`config.Path.Path` set -> `ResolveOverride`; else no group redirect and blank catalog path -> `RequireCatalogPath`
tagged `path:path`. `Build` is today's `Resolve` body after its first lines, passing the checked directory into
`ResolveRepoRoot` so the override is resolved once. `FieldsFor` takes the path rule from `SourceFollowUp`.
`IsSwitchRemedy => false`. The catalog path's confinement and existence stay in `Build` (Resolved Question 1).

**Verify:** filter `FullyQualifiedName~PathSourceTests|FullyQualifiedName~GroupPathSourceTests|FullyQualifiedName~PathBuildGate|FullyQualifiedName~SourceRequirementsTests`; build.

## Task 9: `LocalProjectSource` derives from the base (D8)

**Test first** (`SourceRequirementsTests`, plus `LocalProjectSourceTests` for the remedy):
- `Repository_NoRepositoryToClone_ProblemIsNonAskable_NotOffered` (tag `repository:url`; message equals
  `RequireRepositoryToCheckOut`'s).
- `Repository_WithRepository_ResolvesWithNoFields`.
- `Repository_RepositoryPathOverride_Resolves` (the unmanaged-checkout exemption).
- `Repository_IsNotASwitchRemedy`.
- The remedy-text tests in `LocalProjectSourceTests` (`'container', 'url' or 'kubernetes'`, the single-name and
  no-name cases) stay green unchanged: they are the regression net for the three-source list.

**Implement:** `LocalProjectSource(...) : ServiceSource<NoValues>`. `Check` is `RequireRepositoryToCheckOut` with the
missing-repository exception tagged; `Build` is the rest of today's `Resolve`. `FieldsFor` empty. `IsSwitchRemedy =>
false`. `SourceSwitchRemedy` still reads the old `CanResolve` this task (replaced in Task 10b). `IsOfferedFor` becomes
a delegate to `Requirements(...).Offered`.

**Verify:** filter `FullyQualifiedName~LocalProjectSourceTests|FullyQualifiedName~SourceRequirementsTests|FullyQualifiedName~SourceChoicesTests|FullyQualifiedName~DeferredCheckoutTests`; build; full suite (all six sources now derive from the base).

## Task 10: Move the contract onto the interface and switch the consumers

Three commits. Each is green on its own.

### 10a. Add `Requirements` and `IsSwitchRemedy` to `IServiceSource`; add `SourceProbe.Evaluate`

**Test first** (`SourceRequirementsModelTests` / new `SourceProbeTests`):
- `Evaluate_ReturnsTheSourcesRequirements`.
- `Evaluate_OnAnyOtherException_ReturnsUnknown` (a test source whose `Requirements` throws `UriFormatException`).
- `Evaluate_OnAConfigurationException_StillReturnsTheProblem` (a `ServiceSource<T>` whose `Check` throws is already
  captured, so this is `Requirements` not throwing).
- The existing `SourceProbe_TreatsAnyException_AsNotResolvable` is replaced by the two tests above (the old
  `Resolves(Action)` is removed in 10c).

**Implement:** `IServiceSource` gains abstract `SourceRequirements Requirements(SourceInput input)` and
`bool IsSwitchRemedy { get; }` (no default; every source already provides them through the base class, so it
compiles). `SourceProbe.Evaluate(IServiceSource, SourceInput)`: calls `Requirements`, returns
`SourceRequirements.Unknown` on any exception other than the captured configuration exception.

**Verify:** filter `FullyQualifiedName~SourceProbeTests|FullyQualifiedName~SourceRequirementsModelTests`; build.

### 10b. Switch the consumers to the model

**Test first:**
- `SourceChoicesTests`: add `For_UrlWithFtpCatalogScheme_OmitsUrl` and `For_KubernetesWithUnsupportedCatalogScheme_OmitsKubernetes`
  (red against the old `IsOfferedFor`, which says yes for both). Every existing case in the file keeps its
  expectation.
- `SourceChoicesTests.For_WhenASourceThrowsUnexpectedly_OmitsItAndStillOffersTheRest` (a registry-free check through
  `SourceChoices.For`'s evaluator seam: `For` takes the source lookup as an internal overload so a test can pass a
  throwing source).
- `LocalProjectSourceTests`: `SourceSwitchRemedy_SkipsSourcesNotFlaggedAsSwitchRemedy` (a flagged-false source that
  would resolve is not named).

**Implement:** `SourceChoices.For(definition)` filters `DisplayOrder` by `Evaluate(source, emptyInput).Offered`.
Add `SourceChoices.Requirements(string source, ServiceDefinition definition)` (the same evaluation, for the dialog).
`LocalProjectSource.SourceSwitchRemedy` loops `DisplayOrder`, skips `!source.IsSwitchRemedy`, keeps
`Evaluate(source, new SourceInput(serviceName, definition, config, appHostDirectory)).Resolves`; its
`"repository" or "path" or "disabled"` test is deleted (checklist item 6). `SourceSwitchRemedy` therefore needs the
service name and AppHost directory: `RequireRepositoryToCheckOut` takes them from `Check`'s `SourceInput`.

**Verify:** filter `FullyQualifiedName~SourceChoicesTests|FullyQualifiedName~LocalProjectSourceTests|FullyQualifiedName~UnconfiguredPromptTests`; build.

### 10c. Delete the old members and their tests

**Test first:** none new. First delete, then let the compiler list what is left:
- Delete `IServiceSource.IsOfferedFor` and `CanResolve`, every source's delegate, and the unused `SourceProbe.Resolves`.
- In `LocalProjectSourceTests` delete `EveryRegisteredSource_OverridesCanResolve_OrIsDeliberatelyNeverASwitchTarget`,
  `EverySourceThatCanResolve_IsAlsoOfferedFor`, `OverridesCanResolve`, `SourceProbe_TreatsAnyException_AsNotResolvable`
  and the `neverOffered` array. Their coverage is replaced in Task 11 (compile-time contract, `Resolves implies
  Offered` over all sources, the `IsSwitchRemedy` pin).
- `IServiceSource` doc comments that cross-reference the removed members are rewritten for `Requirements`.

**Verify:** build with `-warnaserror` (no dangling `cref`); full suite.

## Task 11: Contract tests over every registered source (checklist items 3 and 7; spec section 5 items 1-3, 5, 6)

**Test first** (new `SourceContractTests`, trait `IO` for the path samples; samples built the way
`EverySourceThatCanResolve_IsAlsoOfferedFor` built them: each source's catalog value blank/invalid/valid, crossed
with an empty config and configs that fill each source's block or hold invalid values):
- `EveryOfferedSource_AnsweredFromItsFields_Resolves` (closed loop): for every registered source and sample with
  `Offered`, set each `Fields` key from a sample-value table, bind it as a `ServiceDeveloperConfig`, and assert
  `Problem is null`. A key missing from the table fails with "add a sample value for `<key>`".
- `EveryFieldKey_IsARealConfigKey`: setting `<key>` in a JSON entry binds and passes `DeveloperConfigValidator`.
- `ResolvesImpliesOffered_ForEveryRegisteredSource_OverEverySample`.
- `IsSwitchRemedy_IsExactlyContainerKubernetesUrl`.
- `EverySource_CheckAndRequirementsAgreeWithResolve` for `url`, `container`, `kubernetes` (fake allocator),
  `disabled`: `Problem.Message` equals the message `Resolve` throws, and `Problem is null` iff `Resolve` returns.
- The existing `SourceChoicesTests.DisplayOrder_CoversEverySource` stays.

**Sample values for the closed loop:** `url:url` -> `http://localhost:5000`; `kubernetes:context` -> `dev`;
`kubernetes:port` -> `8080`; `kubernetes:namespace`, `kubernetes:scheme` -> `default`, `http`; `path:path` -> an
existing temp directory, which is also the `SourceInput.AppHostDirectory` for that sample.

**Red check (record the output in the notes file):** with each of the following reverted locally, the named test
fails, then restore: delete the `kubernetes:context` field from `KubernetesSource.FieldsFor` (closed loop);
remove `Problem is null ||` from `Offered` (`ResolvesImpliesOffered`); flip `UrlSource.IsSwitchRemedy` (pin). And the
compile-time contract: comment out `Check` in one source and observe `CS0534`; record in the PR.

**Verify:** filter `FullyQualifiedName~SourceContractTests`; full suite.

## Task 12: One validation path in `PickerAnswers` and the dialog (D9, checklist item 2; Resolved Questions 4 and 6)

**Test first:**
- `PickerAnswersTests` (re-expressed over an entry, `Entry(("kubernetes:context","dev"))` builds the nested
  `JsonObject` the writer would build): every existing case keeps its expectation, with these updated and the reason
  stated in the commit: `Validate_SourceInOtherCase_StillRequiresItsFields` now asserts the `Resolve` wording
  (`'kubernetes.context'`), not the dialog's `'kubernetes:context' (Kubernetes context)` (Resolved Question 4).
- New: `Validate_SourceNotOffered_RejectsTheMergedEntryToo` (what `ValidateEntry` used to wave through with
  `?? source`); `Validate_SettingAlreadyInTheFile_SatisfiesTheRequirement` (an entry with `url.url`);
  `Validate_OfferedContainerAndRepositoryEntries_PassThroughCheck` (both now run `Check` at save; an offered one
  always passes, so this pins that the path exists and does not throw, while a not-offered one is the rejection above); `Validate_ForgedSourceName_IsEscapedInTheMessage`.
- `UnconfiguredPromptTests` (class carries `IO`/`Concurrency`/`Timing`): a recording `IDeveloperConfigWriter` shows
  `SaveOneAsync` validates nothing before `SaveAsync` and passes exactly one `validateMerged` callback
  (`Save_ValidatesOnce_ThroughTheWritersCallback`); with the real writer a bad answer yields exactly one failure
  message (`Save_BadAnswer_ReportsOneFailure`).
- `SourceFollowUpTests` content moves into `SourceRequirementsTests` (the url/path/no-field cases; the kubernetes
  ones moved in Task 7), then the class is deleted.

**Implement:** `PickerAnswers.Validate(service, source, JsonObject entry, definition, appHostDirectory)` per D9:
match against `SourceChoices.For`, bind through the same `ConfigurationRoot` the file would load as, run
`DeveloperConfigValidator.ValidateAll`, return `Requirements(...).Problem?.Message`; the rejection message is built
through `Raw`/`Name`. Delete `ValidateEntry`, `ThrowIfUnresolvable`, the source switch, and the fields-dictionary
`Validate`. `UnconfiguredPrompt.SaveOneAsync` reads fields from `SourceChoices.Requirements(source, definition).Fields`,
drops the pre-save `Validate` call and passes `entry => PickerAnswers.Validate(...)` to the writer. Delete
`SourceFollowUp.cs`.

**Verify:** filter `FullyQualifiedName~PickerAnswers|FullyQualifiedName~UnconfiguredPromptTests|FullyQualifiedName~PickerRoundTripTests|FullyQualifiedName~SourceRequirementsTests`; build; full suite.

## Task 13: Docs, release notes, final sweep

- **`docs/guides/interactive-configuration.md`** (the "Offered when" table): `container` is offered for "an image and a
  valid port"; `kubernetes` for "a Kubernetes service name and a supported scheme"; `url` for "no catalog `url:`, or one
  that is an `http`/`https` URL"; `path` and `disabled` stay "always". The follow-up and "answers are checked" prose is
  re-read against the new behaviour and edited only where false (a container or repository answer is now resolve-checked).
  Test: none (prose); the sweep greps below are the check.
- **CHANGELOG.md decision: no new entry, no amendment.** Reason: the interactive picker ([#128]) and the `CanResolve`
  switch offer ([#378]) are in `[Unreleased]`; `git grep UseInteractiveServiceConfiguration v0.8.0 -- src` finds
  nothing, so every user-visible difference here (an `ftp` catalog `url` or an unsupported catalog `kubernetes.scheme`
  is no longer offered; a missing setting is reported in `Resolve`'s wording; an unoffered source is also refused on
  the merged path; container and repository answers are resolve-checked) is a change to a feature that has never shipped.
  The file's own rule (a bug introduced and fixed in the same `[Unreleased]` cycle gets no entry) applies by analogy,
  and a **Changed** entry is for released behaviour. The sentence in the [#128] Added entry ("offers each such service
  the sources its catalog entry can use and asks a follow-up for any that need more") stays true. The PR body states
  this and lists the differences so a reviewer can disagree. The #378 Changed entry is unchanged: its three-source list
  is unchanged. `CHANGELOG.md` is therefore not touched, which also avoids the overlap with PR #529 and the
  other worktrees named in the notes.
- Sweep: `git grep -nE "IsOfferedFor|CanResolve|SourceFollowUp|ValidateEntry|ThrowIfUnresolvable|TryGetImageAndPort|ContainerConfigProblem|neverOffered|\"repository\" or \"path\" or \"disabled\"" -- src test docs/guides docs/sources` returns nothing (historical `docs/superpowers` plans and specs are left as written). `ServiceSourcesBuilderExtensions.cs` summary comment on the six sources is edited only if it names a removed member.
- Verify legs from the notes file: restore, `build -warnaserror`, full `dotnet test`, then the pack leg once.

---

## Dependency and ordering notes

- Tasks 1-3 are additive and independent of any source. 4-9 each flip one source onto the base while the interface is
  untouched, so each is green alone; their order only matters in that 9 (repository) is last because its remedy reads
  the others' `CanResolve` until 10b.
- 10a cannot come earlier: it requires all six sources to provide the members. 10b needs 10a. 10c needs 10b. 11 needs
  10c (it asserts `Requirements` only through the interface). 12 needs 10b (`SourceChoices.Requirements`).
- Nothing before Task 12 changes what the dialog shows or what a save validates, apart from the catalog-scheme offer
  changes that arrive with the delegates in Tasks 6-7 (those are covered by the new `SourceChoicesTests` cases added
  in 10b and by the per-source tests above).

## Acceptance checklist coverage

| Checklist item | Task |
|---|---|
| 1 Source declares fields and checks; dialog and validator driven by it | 3-9, 10b, 12 |
| 2 One validation path | 12 |
| 3 Test that fails when a `Resolve` requirement has no field | 11 (closed loop, with the red check) |
| 4 `IsOfferedFor`/`CanResolve` decided together | 2, 10 |
| 5 One copy of the container rules | 6 |
| 6 No hard-coded exclusion list | 10b, 10c, 11 |
| 7 Compile-time contract; `Resolves` implies `Offered` over all sources | 3 (sealed, abstract), 11 |
| 8 One method returning requirements; sources changed | 3-9 (six files) |
