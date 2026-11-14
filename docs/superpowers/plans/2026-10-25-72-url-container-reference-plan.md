# Container Consumers of a `"url"` Service: Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** A container that references a `"url"`-sourced service starts, with the service's URL injected as a literal, instead of being refused by `UnregisteredServiceStartupGuard`.

**Architecture:** At `BeforeStartEvent`, for each container the existing shape match (`ConsumedUrlService`) identifies, a new internal `UrlContainerReferenceRewrite` removes the shape annotations that make DCP try to plumb networking (`EndpointReferenceAnnotation`, `Reference` `ResourceRelationshipAnnotation` targeting a `"url"` facade) and replaces each `EnvironmentCallbackAnnotation` / `CommandLineArgsCallbackAnnotation` with a wrapper that runs the original, then rewrites values referring to the facade into literals computed from the facade's own `EndpointAnnotation` (never through `GetValueAsync`). A pure helper `UrlFacadeLiteral` owns the literal/property mapping.

**Spec:** `docs/superpowers/specs/2026-10-25-72-url-container-reference-design.md` (probe table in its section 2 is the behavioural source of truth).

**Scope decisions (from the human, binding):** no closing of the opaque-callback gap (wrap only shape-matched consumers); loopback is a docs sentence only, no warning; polyglot and other Aspire versions are left to CI and are not claimed in the CHANGELOG.

## Global Constraints

- No public API change. New types are `internal`.
- Only `ContainerResource` consumers with a `"url"` facade are touched. Host-process consumers, other sources, and the facade's `EndpointAnnotation`/`GetEndpoint` are unchanged.
- The wrapper never throws (a throw in a callback is the silent `FailedToStart`). Unrewritable values are logged via `ServiceSourcesWarnings`/logger through `Name`/`Raw` and never print the URL; the value is left in place.
- Literal is `scheme://host[:port]` only (no path/query/userinfo); port omitted when it equals the scheme default (80 http, 443 https).
- A `ReferenceExpression` keeps non-facade providers as providers (no `GetValueAsync` on foreign providers, so a `ParameterResource` secret is never flattened). If the public builder API cannot express this, fall back per spec 3.1.3 (rewrite only all-facade expressions, log the mixed one).
- Wrap idempotence via a `ConditionalWeakTable` of wrapper instances; do not subclass Aspire annotation types.
- The rewrite lives in/called from the guard's `BeforeStartEvent` handler, before `DropWaitsOnUnregisteredServices`.
- Comments: only the non-obvious WHY, one short sentence. No ticket or upstream issue numbers in any comment or message text written by this change (acceptance item 5); remove the existing `#58/#72`/`#72` mentions where this change makes them stale.
- Messages composed via the `Raw`/`Name` seam (a raw `string` hole does not compile).
- New test classes carry traits per CLAUDE.md: classes using `AppHostDirectory`/`TempDirectories` get `[Trait("IO", "true")]`; pure helper tests carry none. No `Timing`/`Concurrency`.
- During implementation run `dotnet test -f net10.0` filtered to the class; the full run and `-warnaserror` build happen in the final task.

## File Structure

- Create `src/Aspire.Hosting.ServiceSources/Sources/UrlFacadeLiteral.cs` (pure mapping).
- Create `src/Aspire.Hosting.ServiceSources/Sources/UrlContainerReferenceRewrite.cs` (annotation surgery + wrappers).
- Modify `src/Aspire.Hosting.ServiceSources/Sources/UnregisteredServiceStartupGuard.cs` (throw -> rewrite call; remarks).
- Modify comments: `UrlSource.cs`, `IServiceSource.cs`, `DisabledSource.cs`.
- Create `test/Aspire.Hosting.ServiceSources.Tests/Sources/UrlFacadeLiteralTests.cs`, `.../Sources/UrlContainerReferenceTests.cs`, plus a small `CallbackRunner` helper in `TestHelpers.cs` (or its own file).
- Modify `test/.../ContainerConsumerTests.cs`, `DisabledConsumerWaitTests.cs` (comment), `UrlConsumerWaitTests.cs` (if its gap wording changes).
- Modify docs: `docs/sources/url.md`, `docs/getting-started/samples.md`, `docs/sources/disabled.md` (if it points at the refusal), both sample `Program.cs`, both TypeScript `apphost.mts` if they carry the stale line; `CHANGELOG.md`; `scripts/smoketest-container-source.sh`.

## Task 1: `UrlFacadeLiteral` (pure mapping)

**Test first:** `UrlFacadeLiteralTests` (no traits; builds an `EndpointAnnotation` directly):
`Url_OmitsDefaultHttpsPort`, `Url_OmitsDefaultHttpPort`, `Url_KeepsNonDefaultPort`, `Property_Scheme_IsUriScheme`, `Property_Host_And_IPV4Host_AreTargetHost`, `Property_Port_And_TargetPort_AreThePort`, `Property_HostAndPort_AlwaysKeepsThePort`, `Property_Url_EqualsTheLiteral`, `Property_Unknown_ReturnsNull` (a cast integer outside the known enum; the caller logs).

- [ ] Write the tests; run, confirm they fail to compile/fail.
- [ ] Implement `UrlFacadeLiteral.Literal(EndpointAnnotation)` and `TryProperty(EndpointAnnotation, EndpointProperty, out string)`; port-default test compares against `UriScheme`.
- [ ] Run the class green. Commit.

## Task 2: Rewrite `WithReference` for a container; remove the refusal

**Test first:** invert the two refusal tests in `ContainerConsumerTests` (keep names' intent, rename to `UrlSourcedService_ReferencedByAContainer_DoesNotThrow` and `..._ConsumedByAContainerViaWithEnvironment_DoesNotThrow`), and start `UrlContainerReferenceTests` (`[Trait("IO","true")]`) with `WithReference_InjectsBothEnvironmentVariablesAsLiterals` (asserts `INVENTORY_HTTPS` and `services__inventory__https__0` equal the literal) and `WithReference_RemovesTheShapeAnnotations` (no `EndpointReferenceAnnotation` / `Reference` relationship targeting the facade remain).

- [ ] Add test infra: `CallbackRunner.EnvironmentAsync(IResource)` and `ArgsAsync(IResource)` that build `EnvironmentCallbackContext` / `CommandLineArgsCallbackContext` (check the exact constructors against Aspire 13.6.1 via the compiled assembly), run every matching callback annotation in order, and return the produced dictionary/list. Smoke it with a trivial `WithEnvironment("X","y")` test so infra failures are distinguishable from product failures.
- [ ] Run the new/inverted tests; confirm they fail (the guard still throws).
- [ ] Implement `UrlContainerReferenceRewrite.Apply(ContainerResource consumer, IReadOnlyCollection<ServiceResource> urlFacades, ...)`: remove shape annotations; replace env and args callback annotations with wrappers (value rewrite handles `EndpointReference` only in this task).
- [ ] In the guard, replace the `throw` with a call to the rewrite; rewrite `ConsumedUrlService` to return all consumed `"url"` facades (or call it per facade) so a container with two url services is fully handled. Keep `DropWaitsOnUnregisteredServices` and flush unchanged.
- [ ] Run `ContainerConsumerTests` and `UrlContainerReferenceTests` green; `UrlSourcedService_ReferencedByAHostProcess_IsLeftAlone` and `..._MerelyParentedToAContainer_IsLeftAlone` still pass. Commit.

## Task 3: Rewrite every value form

**Test first**, in `UrlContainerReferenceTests`, one per probe-table row: `WithEnvironment_EndpointReference_BecomesLiteral`, `WithEnvironment_ReferenceExpression_KeepsSuffix` (`<url>/x`), `WithEnvironment_EndpointProperty_Url_BecomesLiteral`, `WithEnvironment_EachEndpointProperty_MapsPerTheSpec`, `WithArgs_EndpointReference_BecomesLiteral`, `WithEnvironment_MixedReferenceExpression_KeepsTheForeignProviderAsAProvider` (provider is a `ParameterResource`; assert the produced expression still contains it and was not resolved/flattened), `WithEnvironment_UnrelatedValue_IsUntouched`, `WithEnvironment_OpaqueLambdaAlone_IsNotRewritten` (documents the kept gap: no shape annotations, so the consumer is not matched).

- [ ] Run, confirm failures.
- [ ] Implement the recursive value rewrite (`EndpointReference`, `EndpointReferenceExpression`, `ReferenceExpression` via builder with facade providers replaced by literal text), computed only from `UrlFacadeLiteral`. If the builder cannot preserve foreign providers, implement the spec 3.1.3 fallback and adjust the mixed test to assert the documented fallback (value untouched + log).
- [ ] Green; commit.

## Task 4: Idempotence, no-throw, and the unrewritable log

**Test first:** `RunningThePreflightTwice_LeavesOneWrapperPerCallback`, `AContainerReferencingAContainerSourcedService_IsUntouched` (annotation instances identical before/after), `AValueTheRewriteCannotHandle_IsLoggedAndLeftInPlace` (uses `TestHelpers` warning capture; assert the message names consumer, key and service name and does not contain the URL), `ARewriteFailure_DoesNotThrowFromTheCallback`.

- [ ] Run, confirm failures.
- [ ] Add the `ConditionalWeakTable` wrapper marker; wrap value rewriting in a catch that logs and leaves the original value; log unrewritable values through the `Name`/`Raw` seam.
- [ ] Green; commit.

## Task 5: Stale comments, docs and samples

**Test first:** none new (no behaviour). The guard is a text-only change verified by the existing build with `-warnaserror` and by grepping that no `#72`/`#58/#72` remains in the files touched.

- [ ] Reword: guard class and `ConsumedUrlService` remarks (name exactly the two remaining gaps: opaque callback alone / hand-written annotation, and connection-string consumers; state subscription-order limits per spec section 4), `UrlSource` header, `IServiceSource` remark, `DisabledSource` remark, `DisabledConsumerWaitTests` comment, `UrlConsumerWaitTests` if worded around the refusal.
- [ ] Docs: `docs/sources/url.md` (remove "refused", container caveat; one sentence that a loopback URL means the container itself), `docs/getting-started/samples.md`, `docs/sources/disabled.md` if needed, both sample `Program.cs` "a container cannot reference it" lines and any TypeScript sample equivalents.
- [ ] `dotnet build -c Release -warnaserror`; commit.

## Task 6: CHANGELOG

- [ ] Under `## [Unreleased]` add `### Added`: a container can now `WithReference` (or otherwise consume) a `"url"`-sourced service; the URL is injected as `scheme://host[:port]`; the pre-flight no longer refuses. Note the two remaining gaps and that a loopback URL addresses the container itself. Do not claim TypeScript/other-version coverage. Leave the old 0.x "tracked as #72" line as history. (Not Fixed: the refusal was designed, nothing that worked changes.)
- [ ] Commit.

## Task 7: Live smoke test

**Test first:** the assertion itself, written before wiring the sample: extend `scripts/smoketest-container-source.sh` (or add a sibling called from CI the same way) with a container referencing a `url`-sourced service, asserting `docker inspect` shows the literal env value and a `wget` from inside the container returns content.

- [ ] Add the consumer to the sample AppHost path the script uses (minimal, without disturbing existing sample behaviour), pointing the url service at a public stable host; run the script locally (docker is present) and record output in the notes file. If it cannot run locally, name why and leave to CI.
- [ ] Commit.

## Task 8: Final verification (once)

- [ ] `dotnet restore`; `dotnet build -c Release --no-restore -warnaserror`; `dotnet test -c Release --no-build` (all legs from the notes file); the `dotnet pack` leg. Paste outputs into the notes file.
- [ ] Run the other smoke scripts that fit locally; CI-only legs (polyglot, aspire matrix) named as not run.
- [ ] Confirm the checklist: acceptance 1 (Tasks 2-3, 7), 2 (host-process and non-url tests), 3 (n/a, route works; the guard no longer throws for matched shapes), 4 (gaps stated, logged), 5 (comment sweep), 6 (kept gap test), 7 (Task 6).

## Notes for the PR body

Overlaps open PR #495 on `IServiceSource.cs` and `CHANGELOG.md`; whoever lands second rebases. Out of scope per the human: wrapping all containers, loopback warning, polyglot/other-version claims, publish-mode behaviour.
