# Aspire.Hosting.ServiceSources — `defaultSource` on the Service Catalog

**Date:** 2026-09-16
**Status:** Draft
**Resolves:** GitHub issue #158 (a catalog entry should be able to declare which source a developer
gets for free, so `git clone && aspire run` needs less per-developer setup).
**Builds on:** [the code-catalog design](2026-09-05-servicesources-code-catalog-design.md) (#134,
Stage 1 + Stage 2 merged as of this writing — `ServiceDefinition`/`CodeServiceCatalog`/
`ServiceCatalogBuilder`/`ServiceDefinitionBuilder` are real, shipped types, not a sketch). That
design's Reviewer decisions, question 1, explicitly deferred `defaultSource` rather than absorbing
it, "noted there as needing a code-catalog twin from day one" — this spec is that twin, plus the
yaml half.
**Related:** #161 (a stale field surviving a source switch across configuration layers — informs
why only the bare `source` string is ever projected, never a per-source field); #76 / PR #177 (the
clone-storm fix `LocalCheckoutPrefetch`'s candidate filter exists to preserve); #133 (catalog
nesting shape — unaffected by this change, see [Non-goals](#non-goals-and-scope-boundaries)).
**Milestone:** 0.6.0 — "defaultSource (#158) moved here from 0.4.0 so the field is designed once
across both the yaml and the code surface... rather than designed in yaml now and again a release
later."

---

## Motivation

Quoted from #158's own body: the goal is zero-config first run, without making `"local"` a
*package*-level default — that would mean `git clone && aspire run` silently clones and compiles
every `"local"` catalog entry on a fresh machine, including on a private repo with no git
credential configured. The proposal instead lets the **catalog** declare a per-service default,
since `servicesources.yaml` (or a code-declared catalog) is committed and already carries
`defaultRef`:

```yaml
common-auth:
  repository: https://github.com/pulsen-omsrg/common-authorization
  project: WebUi/WebUi.csproj
  defaultRef: feature/aspire-13-2
  defaultSource: local        # new, symmetric with defaultRef
```

The ticket's own investigation comment (2026-08-31, by the repo owner) is the authoritative source
for this design — later ticket comments supersede the body, and this is the only comment on #158.
It already worked through six alternatives (A–F), found two real hazards in the naive version of
its own recommendation, and left explicit constraints for whoever implements it. This spec verifies
those findings against the code as it exists today (post-#134, which the investigation predates —
`AddServiceCatalog` did not exist yet on 2026-08-31) and turns the recommendation into a concrete
design.

---

## Acceptance, as built from the ticket + notes-file recon

- [ ] Catalog entries gain a `defaultSource` field (yaml); `ServiceCatalogLoader` must accept it
  without a schema change to its unknown-key rejection (it reads the schema by reflection).
- [ ] A code-declared catalog entry gets the equivalent capability — the "code-catalog twin."
- [ ] `defaultSource` is projected as the bottom configuration layer, not a new branch inside
  `ResolveService`.
- [ ] Only the bare `source` string is projected — never a per-source field (`url`, `tag`, etc.).
- [ ] `NotConfiguredError`'s diagnostic is correct once defaulted entries populate `Services`.
- [ ] `LocalCheckoutPrefetch`'s clone-candidate filter excludes default-derived entries.
- [ ] `UnusedCheckoutsMessage`/`FailedCheckoutMessage` never tell a developer to delete a
  `servicesources.local.json` line that does not exist for a defaulted service.
- [ ] Documentation states that `defaultSource: local` means CI clones unless CI pins its own
  source.
- [ ] A changelog entry, per the repo's `CHANGELOG.md` convention.

---

## Findings against the current code

Read on `claude/implement-ticket-158-a2a083` (base `origin/main` @ `40b2752`).

### 1. The domain type is already unified — the "twin" is cheap, not a second design

The investigation comment reasoned about a world where `AddServiceCatalog` was still a proposal.
It has since shipped (#134 Stage 1 + Stage 2, PRs #299/#304 merged). Both a yaml entry
(`ServiceMetadata.ToDefinition()`,
`src/Aspire.Hosting.ServiceSources/Config/ServiceMetadata.cs:67-86`) and a code-declared one
(`ServiceDefinitionBuilder.Build()`,
`src/Aspire.Hosting.ServiceSources/Catalog/ServiceDefinitionBuilder.cs:206-229`) now produce the
same `ServiceDefinition`
(`src/Aspire.Hosting.ServiceSources/Config/Catalog/ServiceDefinition.cs`), merged into one
`CodeServiceCatalog` by `ServiceSourcesConfigCache.LoadedConfig.Load`
(`Config/ServiceSourcesConfigCache.cs:253-471`). Everything downstream of that merge — developer
configuration, `LocalCheckoutPrefetch`, the diagnostics — reads `ServiceDefinition` and does not
know or care which catalog produced it.

This means a single field, `ServiceDefinition.DefaultSource`, fed from either origin, *is* the
twin: there is no second downstream mechanism to build. This is a materially different, and
cheaper, situation than the one the investigation comment reasoned about, and it is why this spec
recommends implementing both surfaces together rather than staging them (see
[Scope decision: yaml and code-catalog together](#scope-decision-yaml-and-code-catalog-together)).

### 2. The clone-storm hazard is real and reproducible against the current filter

`LocalCheckoutPrefetch.Run`'s candidate filter
(`src/Aspire.Hosting.ServiceSources/Sources/LocalCheckoutPrefetch.cs:630-666`) builds its parallel
clone set from `config.DeveloperConfig.Services`:

```csharp
var candidates = config.DeveloperConfig.Services
    .Where(entry => string.Equals(entry.Value.Source, "local", StringComparison.OrdinalIgnoreCase))
    .Where(entry => config.Catalog.Services.ContainsKey(entry.Key))
    .Where(entry => LocalGitCheckout.IsContainedCheckoutDirectoryName(entry.Key))
    ...
```

`config.DeveloperConfig.Services` is populated by binding `IConfiguration`
(`DeveloperConfiguration.ReadFrom`, `Config/DeveloperConfiguration.cs:105-176`). If `defaultSource`
is projected into that same configuration tree — which every alternative below does, by design —
every catalog entry declaring `defaultSource: local` becomes indistinguishable, at this filter,
from a developer's own explicit `"local"` entry. Confirmed by reading the filter, not merely
asserted: nothing at this point in the pipeline carries provenance, so without an explicit
exclusion this filter *would* re-create #76 (PR #177) — cloning every defaulted-to-local catalog
entry in parallel regardless of whether any `AddService()` call ever asks for it.

### 3. `NotConfiguredError`'s `Services.Count == 0` branch does not need a code change

`DeveloperConfiguration.NotConfiguredError` (`Config/DeveloperConfiguration.cs:385-398`):

```csharp
public ServiceSourcesConfigurationException NotConfiguredError(string serviceName) =>
    Services.Count == 0
        ? NothingConfiguredError(serviceName)
        : new ServiceSourcesConfigurationException(
            $"Service '{serviceName}' has no source configured. ..."
            + MisspelledRootKeyNote()
            + MisspelledServiceNameNote(serviceName));
```

The acceptance checklist (inherited from recon) says this needs "re-basing" from "`Services.Count
== 0`" to "no default and no explicit entry." Tracing it against the actual code: `Services` is
whatever `DeveloperConfiguration.ReadFrom` binds out of `IConfiguration` — it does not distinguish
"bound from an explicit layer" from "bound from a projected default." If `defaultSource` is
projected as an ordinary configuration key (as this design requires — see
[Projection](#projection-a-config-layer-not-a-resolver-branch)), a catalog with even one
`defaultSource` makes `Services.Count > 0`, and the branch above already reads as "no default *and*
no explicit entry, for **any** catalog service" without a single line changing. Two things confirm
this is the right (not merely accidental) behaviour rather than a gap:

- `MisspelledRootKeyNote()` is appended in **both** branches (`NotConfiguredError`'s own else-arm,
  and inside `NothingConfiguredError`, `:537-547`), so a misspelled `services` root key is still
  reported even when some other service's default keeps `Services.Count` above zero.
- `NothingConfiguredError`'s message is truthful exactly when it fires: it only fires when *no*
  catalog service has a default and *no* developer entry exists anywhere, which is precisely when
  "no service has a source" is still an accurate global claim.

**Decision: no code change to `DeveloperConfiguration.NotConfiguredError`/`NothingConfiguredError`.**
The projection design re-bases the diagnostic as a side effect of where defaults enter the
pipeline, not as a special case bolted onto the diagnostic itself. Stated explicitly here so the
plan phase does not add redundant special-casing.

### 4. `UnusedCheckoutsMessage`/`FailedCheckoutMessage` are fixed by the same exclusion as finding 2

`LocalCheckoutPrefetch.cs:220-222` and `:366-368` already hedge — "usually their entries in
servicesources.local.json" and "usually the entr(ies) in `{DeveloperConfiguration.FileName}`" —
because the entry can already arrive from appsettings, an environment variable, or the command
line. They are not, today, unconditionally wrong for a case with no file entry. But nothing today
generates a *catalog-defaulted, no-entry-anywhere* case, so nothing has exercised "usually" against
"never" — and a defaulted service genuinely has no configuration-layer line to point at, "usual" or
not.

Both messages are built from `_servicesOnCheckout`
(`LocalCheckoutPrefetch.cs:115, 285-293, 269-278`), which is populated only for services that
entered `_checkouts` — i.e., only for services that survived the `candidates` filter in finding 2.
**The exclusion `LocalCheckoutPrefetch` needs for the clone-storm hazard (finding 2) is the same
exclusion that keeps a defaulted service out of these messages**: a service excluded from
`candidates` is never recorded on a checkout, so it can never appear in `UnusedCheckoutsMessage` or
`FailedCheckoutMessage`. One change closes both acceptance items; see
[Default-derived exclusion](#default-derived-exclusion).

### 5. Configuration precedence: inserting truly *below* `servicesources.local.json` needs a specific call order

`DeveloperConfigFileSource.Registration.Register` puts the local file's provider in with
`builder.Configuration.Sources.Insert(0, source)` (`Config/DeveloperConfigFileSource.cs:176`), and
its own doc comment states the rule: `Sources.Insert(0, ...)` puts a provider at the **lowest**
precedence, because `ConfigurationRoot` resolves a key by scanning `Sources`/`Providers` from the
highest index down, returning the first hit — the standard .NET behavior (`ConfigurationRoot`'s
own `GetConfiguration` walks the provider list backwards). Consequently:

- Whichever `Insert(0, ...)` call happens **later in time** ends up at index 0, pushing whatever
  was there to index 1 — i.e., the later `Insert(0, ...)` call wins the *lowest*-precedence slot.
- To land the `defaultSource` layer below `servicesources.local.json`, this design's insert must
  happen **after** `servicesources.local.json` is already registered, not before.

`servicesources.local.json` is registered by `DeveloperConfigFileSource.EnsureRegistered`, called
at the top of `DeveloperConfiguration.ReadFrom` (`Config/DeveloperConfiguration.cs:108`) and by
every other entry point (`AddServiceCatalog`, `AddBackingService`, etc., per the code-catalog
design's finding 1). But the catalog's own `defaultSource` values are only known inside
`LoadedConfig.Load`, *after* the code catalog is frozen and yaml is merged
(`Config/ServiceSourcesConfigCache.cs:253-417`) — which can be the **first** thing that ever runs,
if the AppHost's first call is a plain `AddService(...)` with no `AddServiceCatalog`/
`AddBackingService` before it. In that path, `servicesources.local.json` would not yet be
registered when `LoadedConfig.Load` reaches the point where it knows the catalog.

**This is a real sequencing hazard, not a hypothetical one**: naively inserting the defaults layer
first and calling `DeveloperConfiguration.ReadFrom` (which registers the local file) second would
put `defaultSource` **above** `servicesources.local.json` — silently inverting the ticket's stated
precedence and letting a catalog default win over a developer's own file. The design in
[Projection](#projection-a-config-layer-not-a-resolver-branch) calls
`DeveloperConfigFileSource.EnsureRegistered(builder)` explicitly, before computing or inserting the
defaults layer, specifically to close this gap. `EnsureRegistered` is already idempotent
(`Config/DeveloperConfigFileSource.cs:152-159`, guarded by `_registered`), so calling it a second
time from a new call site is a documented no-op, not a new registration path.

### 6. `IsReservedKindName` and the unknown-property check need no code change for the yaml field itself

`ServiceCatalogLoader`'s three reflection-derived schemas (`KnownTopLevelProperties`,
`KnownNestedProperties`, `IsReservedKindName`, `Config/ServiceCatalogLoader.cs:26-38, 49-55`) are
all derived from `ServiceMetadata`'s public, non-`[YamlIgnore]` properties. Adding
`public string? DefaultSource { get; set; }` to `ServiceMetadata` is automatically accepted by the
unknown-top-level-property check (`:198-206`) and automatically reserves `defaultSource` as a kind
name the same way `repository`/`project`/`kind` already are — with no other line changed. This is
exactly the reflection design's stated purpose (`ServiceCatalogLoader.cs:23-25`): "a property added
... can never be accepted by the typed pass while being rejected as unknown."

The **value** of `defaultSource` does need its own validation — reflection only proves the *key* is
recognized, not that `"defaultSource: bogus"` is meaningful — see
[Validation](#validation-defaultsource-is-a-closed-vocabulary).

---

## Architecture

### The domain field

Add `public string? DefaultSource { get; init; }` to `ServiceDefinition`
(`Config/Catalog/ServiceDefinition.cs`), fed from two places:

- **Yaml:** `public string? DefaultSource { get; set; }` on `ServiceMetadata`
  (`Config/ServiceMetadata.cs`), carried through in `ToDefinition()` (`:67-86`) alongside `Kind`/
  `KindOptions`/`Origin`.
- **Code:** `ServiceDefinitionBuilder.WithDefaultSource(string source)`
  (`Catalog/ServiceDefinitionBuilder.cs`), following the file's own `RequireUnset` convention (a
  second call is the same "already called" error every other block raises, `:195-203`), carried
  into `Build()` (`:206-229`) as `DefaultSource = _defaultSource`.

Neither `RepositoryMetadata`/`RepositoryBuilder` nor a catalog-root `defaults:` block gets this
field — see [Non-goals](#non-goals-and-scope-boundaries) for why.

### Validation: `defaultSource` is a closed vocabulary

`DeveloperConfigShape.Service.SourceNames` (`Config/DeveloperConfigShape.cs:24-25`) is already the
single source of truth for what a developer's own `source:` value may be:
`{"local", "url", "kubernetes", "container"}`. `defaultSource` pre-fills exactly that same field, so
it is validated against exactly that same set, in both places it can be authored:

- **Yaml**, at load time, in the existing per-service loop in `ServiceCatalogLoader.Load`
  (`:163-266`), alongside the `RepositoryRef` checks already there: `defaultSource` present and not
  a member of `DeveloperConfigShape.Service.SourceNames` is a
  `ServiceSourcesConfigurationException` naming the service and listing the valid values, matching
  the file's existing phrasing convention (`"Service '{name}': ... Expected one of: " +
  string.Join(", ", ...)`). A blank scalar (`defaultSource:` with nothing after it) is treated as
  absent, the same way an empty developer-config field means "unset" elsewhere in this codebase
  (`DeveloperConfiguration.NormalizeBlankToAbsent`, `:225-251`) — a catalog author clearing a
  default they no longer want should not have to delete the line.
- **Code**, at the `WithDefaultSource` call, with the identical set and an identical message shape,
  so an AppHost author and a catalog yaml author see the same vocabulary error.

**Deliberately not validated**: whether the entry actually carries the corresponding block (e.g.
`defaultSource: kubernetes` with no `kubernetes:`/`WithKubernetes(...)`). A developer typing
`source: kubernetes` into `servicesources.local.json` for such an entry is not caught at
config-load time today; it is caught wherever resolution reads a null `Kubernetes` block. Giving
`defaultSource` a stricter check than a developer's own typed selection would be a new,
inconsistent rule for no benefit — a catalog author's mistake here surfaces at the exact same place
a developer's would.

### Projection: a config layer, not a resolver branch

Inside `ServiceSourcesConfigCache.LoadedConfig.Load`
(`Config/ServiceSourcesConfigCache.cs:253-471`), between building the merged `catalog` (`:417`) and
calling `DeveloperConfiguration.ReadFrom` (`:423`):

1. Call `DeveloperConfigFileSource.EnsureRegistered(builder)` explicitly — idempotent, and
   necessary here specifically to guarantee `servicesources.local.json`'s provider already occupies
   index 0 before this step's own insert (finding 5). Every other call site of this same
   guard exists for the same reason: to make the chain complete before something reads it.
2. For every `(name, definition)` in `catalog.Services` where `definition.DefaultSource` is
   non-null: read the **current** effective value of
   `builder.Configuration[$"{DeveloperConfiguration.ServicesKey}:{name}:source"]` — i.e. before
   this step's own layer exists. A non-blank result means some real layer (the local file,
   appsettings, user secrets, an environment variable, the command line) already names a source for
   this service; skip it, contributing nothing for that key. A blank or absent result means nothing
   has claimed this service's source yet; stage
   `{DeveloperConfiguration.ServicesKey}:{name}:source = definition.DefaultSource` for the new layer,
   and record `name` in a new set (see [Default-derived exclusion](#default-derived-exclusion)).
3. If the staged set is non-empty, insert one `MemoryConfigurationSource` built from it at
   `builder.Configuration.Sources.Insert(0, ...)` — landing, per finding 5, strictly below
   `servicesources.local.json`.

This makes the pre-insert snapshot, not provider identity or a second configuration build, the
mechanism that tells "explicitly configured" from "default-derived" — see the next section for why
that distinction cannot be made by comparing effective values alone.

No new branch is added to `ResolveService` or `ServiceSourcesConfigCache`. Every consumer downstream
of `DeveloperConfiguration.Services` — resolution, the ambiguous-spelling check, the "not configured"
diagnostics — sees a defaulted entry exactly as it already knows how to see any other configured
entry, because by the time any of them run, it already is one.

**Opting out of a default.** Because the projected layer is the *lowest* precedence, any higher
layer can override it, including with an explicit "unset" — a higher layer setting
`ServiceSources:Services:<name>:source` to an empty string fully shadows the projected value for
that exact key (this is not new behavior: `ResolveService`, `Config/ServiceSourcesConfigCache.cs:
152-155`, already treats a blank `Source` as "not configured," which is exactly `NotConfiguredError`'s
existing route). A developer or CI pipeline that wants to refuse a catalog's default rather than
inherit it sets `ServiceSources__Services__<name>__Source=` (empty) and gets the ordinary "no source
configured" error instead of the default silently applying.

### Default-derived exclusion

**Why value comparison cannot substitute for provenance.** A developer's own explicit entry can
legitimately name the *same* value the catalog defaults to (`servicesources.local.json` writes
`"source": "local"` for a service the catalog also defaults to `local`) — that is a deliberate,
reviewed opt-in and must stay eligible for the parallel prefetch exactly as it is today. Comparing
the final, merged value against the catalog's `DefaultSource` cannot distinguish "this value is here
because the developer wrote it" from "this value is here only because nothing else did" when the
two strings happen to match. The pre-insert snapshot in step 2 above sidesteps this entirely: it
asks the question *before* the default layer exists, so there is nothing to compare against — the
snapshot is blank if and only if nothing but the default would supply this key.

`LoadedConfig` gains `public required IReadOnlySet<string> DefaultedServiceNames { get; init; }`
(`StringComparer.Ordinal`, matching `catalog.Services`'s own keying — both are keyed on the
catalog's canonical spelling by construction, so no case-insensitive comparer is needed here).
`LocalCheckoutPrefetch.Run`'s candidate filter (`Sources/LocalCheckoutPrefetch.cs:630-666`) gains one
more `.Where`:

```csharp
.Where(entry => !config.DefaultedServiceNames.Contains(entry.Key))
```

placed beside the existing filters, with a comment explaining the hazard it exists to prevent
(mirroring the file's own convention of documenting *why* each filter step exists, not merely what
it does).

**Consequence, accepted deliberately** (this is the ticket's own stated interim, not a gap this
design introduces): a service resolved only through a catalog default, with no explicit entry, is
excluded from the parallel prefetch. If the AppHost actually calls `AddService(...)` for it,
`GetRepoRoot` finds no prefetched checkout and resolves it directly on that call's own thread
(`LocalCheckoutPrefetch.cs:560-589`, the existing "not in the prefetch set" path) — a correct but
serialized cold clone, not a parallel one. The #76 fix's actual remedy (a warm-up window that
observes the burst of `AddService()` calls before prefetching) is out of scope here; excluding
default-derived entries is the "cheaper interim" the ticket's own investigation names.

### Scope decision: yaml and code-catalog together

**Decision: implement `ServiceMetadata.DefaultSource` (yaml) and
`ServiceDefinitionBuilder.WithDefaultSource` (code) in the same change**, not staged, and not
deferred to a follow-up issue.

This is a judgment call against what #134 actually committed to, made explicitly rather than left
implicit:

- #134's Reviewer decisions, question 1, decided *against* pulling `defaultSource` into #134 itself
  — but its stated reason was that #158 "turned up unresolved problems of its own" (the clone-storm
  re-creation, #157's precedence ordering) that #134's own design should not inherit. It did not
  decide that the code-catalog surface should get its `defaultSource` support later than yaml's —
  it explicitly flagged the opposite: #158 was "noted there as needing a code-catalog twin from day
  one."
- The reason #134 staged its *own* delivery (Stage 0 ATS probe, Stage 1 core split, Stage 2
  additive sugar) was real, measured risk: freezing new public types on unmeasured ATS shapes in a
  package with no API-compatibility tooling (#134 finding 10), and a 12-source/19-test-file domain
  split (finding 3). Neither risk applies here. `WithDefaultSource(string)` is a single-parameter
  method on an already-`[AspireExport(ExposeMethods = true)]` type
  (`Catalog/ServiceDefinitionBuilder.cs:14`), strictly simpler than `WithUrl(string)`/
  `WithProject(string)`, both already shipped and already crossing ATS without incident — no new ATS
  shape is introduced, so no probe is needed.
- The domain unification finding 1 above establishes that the "twin" is one field plus one builder
  method, not a second design — the cost that justified staging #134 does not exist here.

Staging this into two PRs for its own sake, with no corresponding risk to manage, would be
process for its own sake — the exact anti-pattern this skill's own guidance warns against ("three
loops for one ticket is the thing to avoid... if that is where a ticket is heading, the size gate
was probably wrong").

---

## Non-goals and scope boundaries

- **No `defaults:` catalog-root block** (alternative C). Per-service `defaultSource` composes
  cleanly with one later if per-service repetition becomes a real complaint; nothing here forecloses
  it, and nothing here needs it.
- **No `RepositoryMetadata`/`RepositoryBuilder` field.** `defaultSource` selects which of
  `local`/`url`/`container`/`kubernetes` a *service* uses; a `repositories:` entry only ever backs
  the `local` source and has no notion of the other three, so there is no coherent meaning for a
  repository-level default. `defaultRef` lives on both types because a ref is meaningful for a
  repository regardless of which service uses it; a source selection is not.
- **No change to `#133`'s scope.** `defaultSource` adds one more `ServiceMetadata`/
  `ServiceDefinition` property; it does not touch kind-option nesting.
- **No `ExecutionContext.IsRunMode` branch.** The ticket's own investigation considered and rejected
  gating `defaultSource: local` on run-vs-publish mode: "CI integration tests run in run mode too,"
  so the lever would not reliably distinguish "a developer's inner loop" from "a pipeline that
  forgot to pin." The mitigation is documentation (below), not a runtime gate.
- **No change to how an explicit `source` value combining with other layers works** — #161's stale-
  field problem is about a higher layer changing `source` while a lower layer's per-source field
  (`url`, etc.) survives. This design projects only the bare `source` string and nothing else,
  specifically so it cannot contribute a stale field for #161 to catch: there is no `local.path`,
  `url.url`, etc. in the projected layer, ever.

---

## Security / attack-surface note

**What changes about trust, precisely.** Before this change, resolving a service to `"local"` —
which clones an arbitrary repository (and, via `prepare:`, runs an arbitrary command inside the
resulting working tree) — required an individual developer's own affirmative, per-service action:
writing an entry into their own gitignored `servicesources.local.json`, or into an environment
variable/appsettings layer they control. After this change, the **catalog author alone** — anyone
who can land a change to the committed `servicesources.yaml`, or (once the code-catalog twin ships)
to the AppHost's own `AddServiceCatalog(...)` call — can make that same clone-and-run happen for
every developer and every CI run that does not explicitly opt out, with no per-developer action at
all.

**Why this is not a new capability, only a changed default.** Anyone who can commit to
`servicesources.yaml`/the AppHost's catalog-declaring code already fully controls the `repository:`
URL and the `prepare:` command a `"local"`-sourced service runs — that is unconditionally true
today, with or without `defaultSource`, for any service a developer chooses `"local"` for. Committing
to this repository already implies the trust level "can specify what gets cloned and what shell
command runs against the clone." `defaultSource` does not grant a new capability to a new class of
actor; it removes the requirement that each individual developer take an affirmative step before an
already-fully-specified action executes. The yaml file is exactly as trusted (or as attacker-
reachable, if the repository's supply chain is compromised) as every other line in it — the code
signature this ticket edits does not change that.

**What genuinely is new**, and worth naming plainly: a developer or CI operator who never opens
`servicesources.yaml` can now be silently running a clone-and-build of a repository they never
explicitly asked to run, discovered only by noticing the clone or the running process — where
before, finding `"local"` configured anywhere required them (or someone) to have written it. This is
exactly the ticket's own "CI still has to pin" caveat, generalized to developers too. This design
resolves it as a **documentation requirement** (below), consistent with `defaultRef`'s own
established precedent — a catalog can already silently pin a specific branch, and that is likewise
handled by documentation rather than a runtime gate. Whether a runtime *notice* (visible without
inspecting logs closely) is also warranted the first time a service resolves through a catalog
default is a real, separate product trade-off — see
[Open Questions](#open-questions).

---

## Documentation requirement

The README section this design most directly extends is `## Getting started` / the catalog schema
reference (yaml) and `## Authoring the catalog in code` (added by #134). Both need a new paragraph,
in substance:

> A catalog entry may declare `defaultSource` (yaml) / call `.WithDefaultSource(...)` (code) to name
> which source a developer gets without writing anything into `servicesources.local.json`.
> **`defaultSource: local` means every developer, and CI, clones and builds that repository by
> default** — including on a machine or pipeline that never explicitly asked for it. If CI should
> not clone, CI must pin its own source (an environment variable, or its own configuration layer)
> rather than relying on the absence of a file.

---

## Changelog entry

Under `## [Unreleased]` / `### Added`, matching the existing register (see `CHANGELOG.md:75-82` for
the #134 entry this one sits beside):

> - **Catalog entries can declare a `defaultSource`, so a service resolves without a
>   `servicesources.local.json` entry at all** ([#158]). `defaultSource: local` in yaml, or
>   `.WithDefaultSource("local")` in a code-declared catalog, projects that value as the
>   lowest-precedence configuration layer — below `servicesources.local.json`, so any real layer
>   still overrides it. Only the bare source name is projected, never a per-source field, so
>   switching a defaulted service to a different source from a higher layer never leaves a stale
>   field behind. **A catalog declaring `defaultSource: local` means every developer, and CI, clones
>   that repository by default** — pin an explicit source in CI if that is not wanted. A
>   default-derived entry is excluded from the parallel checkout prefetch (#76); its first clone, if
>   the AppHost actually adds it, runs serialized rather than in parallel with the others.

(Add `[#158]: https://github.com/flojon/aspire-servicesources/issues/158` to the link block at the
bottom of `CHANGELOG.md`, in the existing ascending-numeric order.)

---

## Testing (pointer for the plan phase)

Not exhaustive here — left to `superpowers:writing-plans` — but the shapes a plan must cover,
matching the repo's `Method_Condition_ExpectedOutcome` convention:

- `ServiceCatalogLoaderTests`: `defaultSource` accepted as a known key; an invalid value rejected
  naming the four valid sources; a blank scalar treated as absent; `IsReservedKindName` reserves
  `defaultSource` as a kind name (falls out of the reflection derivation, but the existing blanket
  schema-completeness test, `Load_EveryKnownPropertyOnOneService_LoadsWithoutError`, must be updated
  to include it).
- `ServiceCatalogBuilderTests`/`ServiceDefinitionBuilderTests`: `WithDefaultSource` sets the field,
  a second call is the standard "already called" error, an invalid value is rejected identically to
  the yaml case.
- A composition-level test (new or extended in `CatalogCompositionTests`): a catalog default
  populates `DeveloperConfiguration.Services` when nothing else configures the service; an explicit
  higher-layer entry (including one setting the identical value) overrides it and is *not* excluded
  from the prefetch; a higher-layer explicit blank ("") opts out and reproduces
  `NotConfiguredError`.
- `LocalCheckoutPrefetchTests`: a defaulted-to-`local` service with no explicit entry never appears
  in the parallel candidate set, never appears in `UnusedCheckoutsMessage`, and (if `AddService` is
  actually called for it) resolves via the direct, non-prefetched path.
- `smoketest-config-layers.sh` (per the notes file's own verify-leg analysis, this is the leg that
  most directly exercises config layering) gains a catalog entry with `defaultSource` and asserts
  the precedence order end to end, including an override from an `appsettings.*.json` layer.
- `smoketest-local-source.sh` gains a `defaultSource: local` entry to prove the clone-storm
  exclusion holds against a real clone, not just a mocked prefetch.

---

## Open Questions

1. **Should resolving a service through a catalog default emit a visible, non-error notice** (the
   existing `ServiceSourcesWarnings.For(builder).AddNotice(...)` mechanism already used for the
   "services share a repository, not grouped" warning in `LoadedConfig.Load`,
   `Config/ServiceSourcesConfigCache.cs:452-458`, would be a natural, low-cost fit), so a developer
   who never opens `servicesources.yaml` can still discover *why* a service is cloning without being
   told by an error? This is a genuine trade-off between the ticket's own stated goal (silent,
   zero-config first run) and the attack-surface note above (silent-by-design is also silent to a
   developer who would want to know). It is not required by the acceptance checklist as written,
   and adding it is scope this spec can propose but should not decide unilaterally — a human should
   confirm whether it belongs in this change or a fast-follow.
2. **Is `defaultSource`/`WithDefaultSource` the final name**, or should the ticket's own
   `defaultSource:` yaml spelling be kept but the code-side method named differently for
   readability (`.WithDefaultSource(...)` vs. something shorter)? This spec picked `WithDefaultSource`
   for consistency with the file's existing `With*` convention (`WithRepository`, `WithUrl`,
   `WithContainer`, `WithKubernetes`, `WithKind`, `WithPrepare`); flagged here only because naming is
   cheap to bikeshed and easy to settle in the plan review rather than here.
