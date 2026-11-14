# Interactive Source Picker (part 2: the prompt, persistence, `Configure`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A fresh clone's unconfigured services get a dashboard dialog that asks which source each should use, saves the answers to `servicesources.local.json`, and offers a per-row `Configure` command.

**Architecture:** Part 1 (merged, #471) already resolves an unconfigured service to `UnconfiguredServiceResource` behind the `ServiceResource` facade, opt-in via `UseInteractiveServiceConfiguration()`. This plan adds: per-source "offered when" predicates, the follow-up field rules, a config-file writer, blank-shadow detection, one `BeforeStartEvent` handler that raises an un-awaited `PromptInputsAsync` (no deadline), and a `Configure` resource command that reuses the same flow.

**Tech Stack:** C# / .NET 10, Aspire.Hosting 13.x `IInteractionService`, xUnit. Source: `src/Aspire.Hosting.ServiceSources`, tests: `test/Aspire.Hosting.ServiceSources.Tests`.

**Spec:** `docs/superpowers/specs/2026-08-28-servicesources-interactive-source-picker-design.md`

## Global Constraints

- The prompt is raised from `BeforeStartEvent`, never `AfterResourcesCreatedEvent` (deadlocks behind `WaitFor`); it runs on a task nobody awaits and never throws.
- One `InputType.Choice` input per unconfigured service in a single form; source list = `repository`, `container`, `url`, `kubernetes`, `path`, `disabled`, filtered per service.
- No deadline. A closed dialog changes nothing, logs one line naming the placeholders, and is not re-raised. `IsAvailable == false` logs one warning naming each service and its key.
- Same gate as part 1: `InteractiveServiceConfiguration.IsEnabled` / `InteractivePrompts` (`CI`, `SERVICESOURCES_INTERACTIVE`). Strict mode and publish mode unchanged.
- Answers merge into `servicesources.local.json` beside the AppHost, preserving existing entries; file created if absent. Written behind a small interface (#69 may move it).
- After save: `PromptNotificationAsync` with `MessageIntent.Success`, saying it takes effect on the **next run**.
- All displayed config values go through `Raw.Escaped`/`Raw.Literal`; dialog is plain text (`EnableMessageMarkdown = false`, `EnableDescriptionMarkdown = false`), matching `SourcePrompt`.
- Test classes carry `IO`/`Concurrency`/`Timing` traits per `CLAUDE.md`.
- Out of scope: changing an already-resolved service's source (#70 remainder), stop-AppHost command, `IConfiguration`/user-secrets persistence (#69).

## Review Focus

- Service name with markdown/`:`/`.` characters in the dialog and as a JSON key — rendered literally, written under the right key.
- `servicesources.local.json` exists but is malformed, empty, or has a root that is not an object — must not be clobbered; surface an error notification, write nothing.
- Local file has `services` with differently-cased service key (`Orders` vs `orders`) — merge updates the existing entry, not a duplicate.
- Dialog answered with a choice that was not offered (stale/forged value) — rejected, nothing written.
- Two services answered in one form where one follow-up fails validation — the valid one is still saved; the failure is reported.

---

### Task 1: Per-source "offered when" predicate and choice list

**Files:**
- Modify: `src/Aspire.Hosting.ServiceSources/IServiceSource.cs`, `Sources/LocalProjectSource.cs`, `Sources/ContainerSource.cs`, `Sources/KubernetesSource.cs`, `ServiceSourcesBuilderExtensions.cs` (expose the source dictionary internally)
- Create: `Sources/SourceChoices.cs`
- Test: `test/Aspire.Hosting.ServiceSources.Tests/Sources/SourceChoicesTests.cs`

**Interfaces:**
- Produces: `IServiceSource.IsOfferedFor(ServiceDefinition definition) => true` (default interface method); `internal static class SourceChoices { IReadOnlyList<string> All(); IReadOnlyList<string> For(ServiceDefinition definition); }`

- [ ] **Step 1: Write failing tests** in `SourceChoicesTests`: `All_EqualsTheSourceDictionaryKeys` (set-equal to `repository, container, url, kubernetes, path, disabled`); `For_DefinitionWithoutContainerBlock_OmitsContainer`; `For_WithoutRepository_OmitsRepository`; `For_WithoutKubernetesService_OmitsKubernetes`; `For_AnyDefinition_AlwaysOffersUrlPathDisabled`; `For_FullyDeclaredDefinition_OffersAllSix`. Build `ServiceDefinition`s the way `CatalogCompositionTests` does.
- [ ] **Step 2: Run** `dotnet test --filter "FullyQualifiedName~SourceChoicesTests"` → FAIL (type missing).
- [ ] **Step 3: Implement** the default method on `IServiceSource` (doc: a source states its own precondition so third-party sources, #54, need no hard-coding). Override in `LocalProjectSource` → `LocalGitCheckout.HasRepositoryToClone(definition)`; `ContainerSource` → container block has `image` and `port` (reuse the check inside `ResolveContainerConfig` without throwing — extract a `TryGet…` helper both use); `KubernetesSource` → kubernetes block names a `service`. `SourceChoices.For` filters the dictionary by `IsOfferedFor`, ordered as listed in Global Constraints.
- [ ] **Step 4: Run** the filter again → PASS.
- [ ] **Step 5: Commit** `feat: per-service source choices for the interactive picker`.

### Task 2: Follow-up field rules

**Files:**
- Create: `Sources/SourceFollowUp.cs`
- Test: `test/.../Sources/SourceFollowUpTests.cs`

**Interfaces:**
- Consumes: `ServiceDefinition` (catalog entry).
- Produces: `internal sealed record FollowUpField(string Key, string Label, bool Required, InputType InputType)`; `SourceFollowUp.FieldsFor(string source, ServiceDefinition definition) : IReadOnlyList<FollowUpField>`. `Key` is the path under the service entry, e.g. `kubernetes:context`.

- [ ] **Step 1: Write failing tests** pinning the spec table: `kubernetes` → required `kubernetes:context`, required `kubernetes:port` only when catalog has no port, optional `kubernetes:namespace`, `kubernetes:scheme`; `path` → required `path:path` unless catalog has `path:`; `url` → required `url:url` unless catalog has a url; `repository`, `container`, `disabled` → empty. (Optional `ref`/`tag` are deliberately **not** asked: "the common answer needs no follow-up".) Use the exact config key spellings from `PathDeveloperConfig`, `UrlDeveloperConfig`, `KubernetesDeveloperConfig`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** `FieldsFor` as a switch on `source`. **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: follow-up fields per chosen source`.

### Task 3: Developer config writer

**Files:**
- Create: `Config/IDeveloperConfigWriter.cs` (interface + `JsonFileDeveloperConfigWriter`)
- Test: `test/.../Config/JsonFileDeveloperConfigWriterTests.cs` (`[Trait("IO","true")]`)

**Interfaces:**
- Produces: `interface IDeveloperConfigWriter { Task<SaveResult> SaveAsync(string serviceName, string source, IReadOnlyDictionary<string,string> fields, CancellationToken ct); }` with `record SaveResult(bool Saved, string? Error)`. `JsonFileDeveloperConfigWriter(string appHostDirectory)` targets `Path.Combine(dir, DeveloperConfiguration.FileName)`.

- [ ] **Step 1: Write failing tests**: `Save_NoFile_CreatesItWithServicesEntry`; `Save_ExistingEntries_ArePreserved` (other services, `backingServices`, `repositories`, `repositoryRoot` untouched); `Save_DifferentlyCasedExistingKey_UpdatesThatEntry`; `Save_RoundTrip_ReloadsThroughDeveloperConfigLoader` (entry reads back with `Source` and the nested block); `Save_MalformedFile_ReturnsErrorAndLeavesFileByteIdentical`; `Save_RootNotObject_ReturnsError`; `Save_ServiceNameWithColonAndDot_WrittenAsLiteralKey`; `Save_ConcurrentCalls_BothSurvive` (two services, `Task.WhenAll`; add `Concurrency` trait).
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement** with `System.Text.Json.Nodes`: read → modify `services` → write to a temp file in the same directory → `File.Replace`/move; serialize calls with a static `SemaphoreSlim`. Field keys with `:` become nested objects.
- [ ] **Step 4: Run** → PASS. **Step 5: Commit** `feat: merge picker answers into servicesources.local.json`.

### Task 4: Answer validation and shadow detection

**Files:**
- Create: `Sources/PickerAnswers.cs`
- Test: `test/.../Sources/PickerAnswersTests.cs`

**Interfaces:**
- Consumes: `SourceChoices.For`, `SourceFollowUp.FieldsFor`, `DeveloperConfigValidator.ValidateAll`, `DeveloperConfigShape`.
- Produces: `PickerAnswers.Validate(string service, string source, IReadOnlyDictionary<string,string> fields, ServiceDefinition definition) : string? error` (null = ok; rejects a source not in `SourceChoices.For`, missing required fields; loads the answers into a `MemoryConfigurationSource` section and runs `ValidateAll`); `PickerAnswers.ShadowingProvider(IConfigurationRoot root, string service) : string?` — name of a provider other than the file's memory provider that holds `ServiceSources:Services:<service>:source` with a blank value.

- [ ] **Step 1: Write failing tests**: `Validate_SourceNotOffered_Rejects` (container on a definition with no container block); `Validate_MissingRequiredField_Rejects`; `Validate_InvalidField_ReportsValidatorMessage` (e.g. whitespace-padded url); `Validate_Valid_ReturnsNull`; `ShadowingProvider_BlankInHigherLayer_NamesIt`; `ShadowingProvider_NoBlank_ReturnsNull`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement.** Identify the file's layer as the provider inserted by `DeveloperConfigFileSource` (index 0 memory source). **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: validate picker answers and detect blank shadowing`.

### Task 5: The prompt, orchestration, and wiring

**Files:**
- Create: `Sources/UnconfiguredPrompt.cs` (form build/map + orchestration), `Sources/UnconfiguredRegistry.cs` (per-builder list of `(name, definition)` recorded when `UnconfiguredSource.Resolve` runs)
- Modify: `Sources/UnconfiguredSource.cs` (record into registry; subscribe once per builder to `BeforeStartEvent`); `Sources/UnregisteredServiceStartupGuard.cs` only if the wait-safety test (Step 1) shows `IResourceWithoutLifetime` is insufficient
- Test: `test/.../Sources/UnconfiguredPromptTests.cs` (`IO`, `Timing`)

**Interfaces:**
- Consumes: Tasks 1–4; `IInteractionService`, `InteractivePrompts.DisabledReason`.
- Produces: `UnconfiguredPrompt.Build(IReadOnlyList<Unconfigured> services) : SourcePromptContent`; `UnconfiguredPrompt.RunAsync(IServiceProvider, IReadOnlyList<Unconfigured>, IDeveloperConfigWriter, ILogger, CancellationToken) : Task` — the whole flow (form → follow-ups per service needing them → validate → save → notification), reused by Task 6.

- [ ] **Step 1: Write failing tests** with a fake `IInteractionService` recording calls and returning scripted results:
  - `Build_OneChoiceInputPerService_WithFilteredOptions_PlainText` (names with markdown stay literal).
  - `Run_Answers_SavedAndSuccessNotificationSaysNextRun`.
  - `Run_KubernetesAnswer_RaisesFollowUpForContext`; `Run_ContainerAnswer_RaisesNoFollowUp`.
  - `Run_UnofferedChoice_RejectedNothingWritten`.
  - `Run_OneInvalidFollowUp_OtherServiceStillSaved_FailureReported`.
  - `Run_DialogClosed_WritesNothing_LogsOneLineNamingPlaceholders_NotReRaised`.
  - `Run_HostStopping_StartsNothing`.
  - `Run_InteractionUnavailable_LogsOneWarningNamingServicesAndKeys`.
  - `Run_ShadowedByHigherLayer_StillSaves_NotificationNamesProvider`.
  - `Subscription_PlaceholdersExist_RaisesPromptOnce_NoPlaceholders_RaisesNone` (thin integration over a real builder; also asserts no prompt when `InteractivePrompts` is off).
  - `WaitFor_Placeholder_ConsumerStartsAndPromptStillRaised` (the deadlock check; if it fails, apply the guard change in Modify list).
  - `TwoPrompts_PartlyConfigured_BothRaisedForTheirOwnServices` (source-pick dialog for configured cold checkouts, this one for placeholders); record whether the fake shows them together or in sequence in a test comment.
- [ ] **Step 2: Run** → FAIL.
- [ ] **Step 3: Implement.** Dialog title `Choose a source for unconfigured services`; no deadline token — only `ApplicationStopping`. Handler calls `Task.Run(() => RunAsync(...), CancellationToken.None)` and swallows/logs exceptions, mirroring `DeferredCheckout`'s un-awaited prompt. Title/message distinct from "Choose services to check out".
- [ ] **Step 4: Run** → PASS; also run `dotnet test --filter "IO!=true&Timing!=true"` to confirm no regressions in the hermetic subset.
- [ ] **Step 5: Commit** `feat: dashboard prompt for unconfigured services`.

### Task 6: `Configure` command on the placeholder

**Files:**
- Modify: `Sources/UnconfiguredSource.cs` (add `.WithCommand("configure", "Configure", …)` to the registered resource)
- Test: extend `UnconfiguredPromptTests` (or `UnconfiguredServiceTests`)

**Interfaces:**
- Consumes: `UnconfiguredPrompt.RunAsync` for a single-element list.

- [ ] **Step 1: Write failing tests**: `Configure_Command_Exists_OnPlaceholder`; `Configure_Command_RunsSamePromptAndSave_AsPushPath` (fake interaction; same saved file as push path); `Configure_Command_FailsGracefully_WhenInteractionUnavailable` (returns `ExecuteCommandResult` failure with message, no throw).
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** — command executes `RunAsync` for just that service and returns `CommandResults.Success()`/`Failure(message)`. **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: Configure command on unconfigured services`.

### Task 7: Durability check, docs, changelog

**Files:**
- Test: `UnconfiguredServiceTests` (durability, `IO`, `Timing`)
- Modify: `CHANGELOG.md`, `docs/sources/` page for the picker (or `disabled.md` neighbour), spec header status

- [ ] **Step 1: Write test** `PlaceholderState_SurvivesDcpUpdates` — start a real `CreateBuilderThatCanStart` app with a placeholder, let startup finish, assert the resource's `State` text is `Not configured` and `Reason` property is present via `ResourceNotificationService`. If it fails, fix per microsoft/aspire#13647 (republish state after start) in `UnconfiguredSource.cs`.
- [ ] **Step 2: Run** `dotnet test` (whole suite) → all PASS.
- [ ] **Step 3: Docs/CHANGELOG** — user-facing description: opt-in call, `SERVICESOURCES_INTERACTIVE`, what is saved where, takes effect on next run. Update spec status line to "Implemented".
- [ ] **Step 4: Commit** `docs: interactive source picker`.

---

## Self-Review

- **Spec coverage:** per-service filtered choices (T1), follow-up table (T2), merge persistence + interface (T3), #43 validation reuse + blank-shadow (T4), BeforeStartEvent push, no deadline, closed/unavailable/stop, notification, wait safety, two-prompt, thin integration (T5), Configure pull (T6), durability + docs (T7). Strict/publish/decision matrix/placeholder construction/consumer safety are already covered by part 1's `UnconfiguredServiceTests`.
- **Open measurements the spec flags** (kept as assertions, not assumptions): wait safety of `IResourceWithoutLifetime` (T5), whether two prompts show together (T5), DCP state clobbering (T7).
