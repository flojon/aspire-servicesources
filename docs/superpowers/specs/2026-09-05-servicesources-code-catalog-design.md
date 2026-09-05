# Aspire.Hosting.ServiceSources — Authoring the Service Catalog in Code

**Date:** 2026-09-05
**Status:** Draft — for review before an implementation plan is written. Nothing here has shipped.
Revised 2026-09-05 after two reviews: the ordering rule was built on a miscounted set of catalog
readers, the `servicesources.local.json` requirement was left unsaid, and five ATS shapes this API
depends on turn out never to have been measured. All three are corrected below.
Revised again after a second round (an `OrdinalIgnoreCase` catalog would change lookup for existing
yaml AppHosts — finding 11) and a third (parsing `prepare.mode` at catalog load would newly latch
failures for them — finding 3).
**Resolves:** GitHub issue #134 (the service catalog should be authorable in the AppHost's own
language, so `servicesources.yaml` becomes optional rather than required).
**Would also resolve:** #73 (split the yaml DTO from the catalog domain model) — see
[The domain type](#the-domain-type).
**Re-scopes:** #133 (nest kind options under a fixed key) — see [Kind options](#kind-options).
**Wants a decision alongside:** #158 (`defaultSource`) — see finding 9.
**Leaves a seam for:** #11 (the central registry).
**Builds on:** [the typed-catalog ATS findings](2026-08-30-typed-catalog-ats-findings.md) (#71,
amended by #143) and [the 19507 findings](2026-08-30-19507-already-fixed-findings.md) (#88/#137).

---

## Motivation

The catalog is the one part of this package a developer must write in yaml. Every other statement an
AppHost makes — which services it wants, how each is configured, which kinds are registered — is
already C# or, through ATS, TypeScript. Removing the need for yaml is a goal of Aspire itself, and
the deciding argument in the #71 finding is alignment: the app model is code in the AppHost's own
language, `AddProject`/`AddContainer` are what that looks like, and ATS extends it to guest
languages. A yaml catalog in the middle of that works against the framework this package extends.

The technical objection was measured away in #71: a fluent, lambda-based catalog API crosses ATS and
runs end to end from a TypeScript AppHost. The sharing objection does not survive either — every
AppHost repository carries its own `servicesources.yaml`, so the catalog is duplicated rather than
shared, and real cross-repository sharing is #11's registry.

**This removes the committed yaml file, not configuration.** `servicesources.local.json` is
untouched and still required: `ServiceSourcesConfigCache.ResolveService` throws
`DeveloperConfiguration.NotConfiguredError` for any service without a `source`, and there is no
default (that is #158). So an AppHost on this design still ships a
`servicesources.local.json.example` and each developer still writes a `source` per service. That is
not a regression — it is what every AppHost does today — but nothing here should be read as promising
a config-free first run. The gitignored, per-developer file stays; the committed, shared file goes.

**"Optional", not "deleted".** The loader stays, for AppHosts that already have a
`servicesources.yaml` and for the case yaml genuinely serves — editing the catalog without
rebuilding the AppHost.

---

## The acceptance criteria, quoted

Cited throughout, so stated once (#134):

1. An AppHost with no `servicesources.yaml` can declare and resolve every source kind.
2. The same is true from a TypeScript AppHost.
3. An existing yaml-based AppHost keeps working unchanged.
4. A service declared twice fails with an error naming both sources.

---

## Findings that constrain the design

Read out of the code on `main` (48d2f8f), with file:line evidence. Each closes off a design that
would otherwise look reasonable.

### 1. Exactly one call reads the catalog first: `AddService`

`ServiceSourcesConfigCache.LoadedFor(builder)` memoizes a `LoadedConfig` per builder in a
`ConditionalWeakTable`, behind a lock, latching a `ServiceSourcesConfigurationException` so every
later caller is told what the first was told. `LoadedConfig.Load` is one method
(`Config/ServiceSourcesConfigCache.cs:154-166`), and its catalog line is the whole integration point:

```csharp
var catalog = ServiceCatalogLoader.Load(Path.Combine(builder.AppHostDirectory, "servicesources.yaml"));
```

`LoadedFor` has **two** call sites: `ResolveService` (`ServiceSourcesConfigCache.cs:66`, reached only
from `AddService`) and `LocalCheckoutPrefetch.Run` (`Sources/LocalCheckoutPrefetch.cs:507`) — and the
prefetch is reached from `LocalProjectSource.Resolve` (`Sources/LocalProjectSource.cs:85`), i.e.
*after* `ResolveService` already loaded on the same `AddService` call. `ServiceCatalogLoader.Load`
has exactly one production caller.

**`AddBackingService` does not read the catalog.** It goes through a separate `ConditionalWeakTable`
(`ServiceSourcesConfigCache.cs:14-16`) to `DeveloperConfiguration.ReadBackingServicesFrom`
(`Config/DeveloperConfiguration.cs:117-137`), whose own XML doc says it is read apart from the
catalog "because it needs no catalog and the catalog is a file that may not exist".
`BackingServices/BackingServiceConfigAudit.cs:145` takes the same path.

So **the ordering rule is exactly "before the first `AddService(…)`"** — the same rule
`UseDeferredCheckout` and `AddLocalKind` already carry, phrased the same way. An earlier draft of
this design generalised it to "before the first call that reads the catalog, which includes
`AddBackingService`", which is false and would have sent developers to move a line above a call that
reads nothing.

### 2. Missing yaml is a hard error today, and that error is the good one

`Config/ServiceCatalogLoader.cs:53-57`:

```csharp
if (!File.Exists(path))
{
    throw new ServiceSourcesConfigurationException(
        $"Service catalog file not found at '{path}'. Expected a 'servicesources.yaml' file in the AppHost project directory.");
}
```

Making yaml optional must not degrade that message for the AppHost that simply forgot the file. The
file being absent is only acceptable **when a code catalog was declared**; with neither, the
developer has no catalog at all and deserves to be told both ways of getting one.

### 3. Three reflection-derived yaml schemas hang off the catalog types

`ServiceCatalogLoader` derives its validation by reflection over the binding types:

- `KnownTopLevelProperties = YamlPropertyNames(typeof(ServiceMetadata))` (`:17`)
- `KnownRootProperties = YamlPropertyNames(typeof(ServiceCatalog))` (`:19`), enforced at `:66-75`
- `KnownNestedProperties` (`:29-35`), one set per `ServiceMetadata` property whose type
  `IsNestedBlock` — a *namespace* test (`:48-49`), so any class in
  `Aspire.Hosting.ServiceSources.Config` is one
- `IsReservedKindName(kind) => KnownTopLevelProperties.Contains(kind)` (`:27`), consumed by
  `Sources/LocalKindRegistry.cs:28`

`YamlProperties` skips `[YamlIgnore]` properties (`:37-39`).

**Adding a property to `ServiceMetadata` silently widens the accepted yaml schema and silently
removes a name from the kind registry's allowed set. Adding one to `ServiceCatalog` widens the
accepted root keys.** Separately, `ServiceCatalog.Services` cannot simply be retyped to hold the
domain type: `ServiceCatalogLoader.cs:60` binds `ServiceCatalog` with YamlDotNet directly, so its
value type must stay the yaml DTO. The merged map therefore needs a container of its own.

Two things a code-authored entry needs that yaml must not see:

1. `Origin` — which catalog declared it, so errors can name it (finding 5).
2. `KindOptions` — already-typed options handed straight to the handler (finding 6).

**Not** a typed `PrepareMode` on the definition, though the first draft of this design proposed one.
`PrepareMetadata.Mode` stays `string?` (`Config/PrepareMetadata.cs:68`) and the parse stays where it
is, in `PreparePlan` (`Prepare/PreparePlan.cs:95-96` into `PrepareModes.Parse`,
`Prepare/PrepareMode.cs:83-104`). Moving the catalog half to load time would make it run for **every**
entry rather than only for a service the developer actually resolved through `"local"`, and it would
run inside `ConfigLoader.Load`, which latches (`Config/ServiceSourcesConfigCache.cs:126-144`) — so a
typo in one entry's `prepare.mode` would newly fail every `AddService` in AppHosts that never select
that service. That is a change to acceptance criterion 3 bought for nothing. `WithPrepare` instead
takes the enum at the surface and stores `PrepareModes.Written(mode)`, the reverse mapping that
already exists, leaving one representation downstream. `WithPrepare` guards with `Enum.IsDefined`
first: `PrepareModes.Written` is a `Spellings.First(...)` lookup, total only over defined values, and
an ATS caller can pass an undefined integer for an enum parameter (finding 7 lists enum-as-parameter
among the unmeasured shapes). Undefined must be a `ServiceSourcesConfigurationException` naming the
four spellings, not an `InvalidOperationException`.

A fourth belongs on the conversion rather than on either type: `ServiceCatalogLoader.cs:87-92`
normalizes a blank `kind:` back to `"dotnet"`, undoing YamlDotNet assigning null over the field
initializer. That is yaml-specific cleanup — a code path never sees it, because it never runs the
deserializer — and it is the kind of step that belongs in `ToDefinition()`. (It is *not* an argument
for the split: `ServiceMetadata.Kind` is non-nullable and initialized to `"dotnet"` at
`Config/ServiceMetadata.cs:26`, so a bypassing path gets `"dotnet"`, not null.)

**How much of this forces a split, honestly.** Less than it first appears, and the codebase says so
itself: `ServiceMetadata.KindConfig` is already `object?` carrying `[YamlIgnore]`
(`Config/ServiceMetadata.cs:32-33`), assigned from the raw block at `ServiceCatalogLoader.cs:149` and
skipped by `YamlProperties` at `:39`. That is the "add a field the yaml does not see" pattern,
shipping in production, in exactly the `KindOptions` slot. So `KindOptions` needs no second type, the
typed `PrepareMode?` is gone (above), and the `Kind` normalization is disclaimed as an argument. What
is left needing a home is **`Origin` — one field.**

The case for splitting anyway rests on two things that are true regardless of field count:

- **The reflection trap.** Three schemas are derived from these types by reflection, and
  `[YamlIgnore]` is opt-*out*. Every future field is a silent yaml-schema widening and a silent
  removal from the kind registry's allowed names unless someone remembers the attribute. `KindConfig`
  is not evidence the pattern is safe; it is one field that got it right.
- **The container.** `ServiceCatalog.Services` cannot hold the merged map (`ServiceCatalogLoader.cs:60`
  binds `ServiceCatalog` with YamlDotNet directly), so *some* new type is needed whatever happens to
  `ServiceMetadata`.

The case against is a measured 12 source files and 19 test files changed for no behaviour change.
**This is a genuine cost/benefit call and it belongs to the reviewer, not to this document** — see
open question 4. If the answer is "not now", the fallback is `[YamlIgnore] Origin` on
`ServiceMetadata` plus the new container, which reaches every acceptance criterion, leaves #73 open,
and cuts the largest single task out of stage 1.

Note also that this is a *different* argument from #71's: the
[ATS findings](2026-08-30-typed-catalog-ats-findings.md) are explicit (lines 139-141) that the split
does **not** remove `IsReservedKindName`, which is a consequence of the document's layout and is
#133's job.

### 4. One entry may carry every source at once, so the code API must be additive

`README.md:1281` "### Combining sources on one catalog entry", example at `:1287-1301`: a single
entry can carry `repository:`/`project:` *and* `kubernetes:` *and* `url:` *and* `container:` blocks
simultaneously; the catalog describes how each source *would* resolve the service and
`servicesources.local.json` picks which one applies. #134's sketch spells this `FromRepository(...)`
/ `FromUrl(...)` / `FromContainer(...)`, which reads as mutually exclusive and would mislead exactly
where the package's central idea lives. Hence `With*` — see [The authoring API](#the-authoring-api),
which also gives the method → `source`-value mapping, since the method names must not become a fifth
vocabulary for a four-value set.

### 5. Eight files put `servicesources.yaml` into messages a code-declared service can reach

Reachable for a code-declared service and each naming a file that need not exist —
`ServiceSourcesConfigCache.cs:71` ("was not found in 'servicesources.yaml'"),
`DeveloperConfiguration.cs:272,275` (`AmbiguousCatalogSpellingError`, which tells you to *rename them
in 'servicesources.yaml'*), `ContainerSource.cs:38,42`, `KubernetesSource.cs:58,91`,
`UrlSource.cs:286`, `Java/JavaKindOptions.cs:67-69`, `EndpointScheme.cs:58`,
`ServiceEndpointExtensions.cs:100`. (`Config/ServiceCatalogLoader.cs:56` is a ninth runtime message
naming the file, but finding 2 replaces it, so it is not reachable for a code-declared service.
Sixteen files mention the name in all; the other seven do so only in XML doc comments, and
`ServiceSourcesConfigCache.cs:156` is the `Path.Combine`. Only the eight are what the guard test can
hold honest.)

`ServiceSourcesConfigCache.cs:71` — "was not found in `'servicesources.yaml'`" — is the one every
developer will hit first, on the first typo'd name in a code catalog.
(`AmbiguousCatalogSpellingError` is *not* in this set: finding 11 routes code-vs-code to the second
`AddService` call and code-vs-yaml to the duplicate error, leaving that message reachable only
yaml-vs-yaml, where the file does exist.) Carrying `Origin` on the entry is what lets these eight
name the right thing, and the test suite gets a blanket guard (see [Testing](#testing)).

### 6. Kind options reach a handler as an untyped yaml dictionary, round-tripped through yaml

`ServiceMetadata.KindConfig` is `object?` — the raw dictionary fished out by `RawServiceCatalog`.
`ILocalResourceKind` (public) takes it as `object? rawConfig`, and the only supported way to read it
is `LocalKindConfig.Parse<T>` (`LocalKindConfig.cs:36-69`), which **re-serializes the dictionary to
yaml and deserializes it into `T`** with a deserializer deliberately not `IgnoreUnmatchedProperties()`,
so a typo inside the block is caught. Kind-level validation lives *outside* it —
`Java/JavaKindOptions.Parse` (`:60-80`) calls `LocalKindConfig.Parse<JavaKindOptions>` at `:65` and
then runs its own `ValidatePort`/`ValidateWorkingDirectory`/`ValidateJarPath`/`ValidateWrapperPath`.

So a pass-through for an already-typed instance skips no validation. It needs three branches, not
one:

```csharp
if (rawConfig is T alreadyTyped) return alreadyTyped;   // authored in code
if (CameFromCode(rawConfig)) throw MismatchError();     // authored in code, wrong options type
… existing scalar/sequence/dictionary handling, unchanged …
```

`CameFromCode` must be narrow: YamlDotNet produces `string`, boxed primitives, `IList` and
`IDictionary`, and `LocalKindConfig.cs:43-54` already has good messages for them — it branches on
`IEnumerable and not string` for "a list" and falls back to "the scalar '…'. Check the indentation
under the kind's key.", so the predicate must mirror that test rather than checking `IList`. Only an object that is none
of those can have come from a `WithKind` call, and only that one gets the new error.

The middle branch matters: without it, a third-party kind's options object handed to the wrong
kind's `Parse<T>` falls into the existing "must be a block of key/value pairs, but found the scalar
'`Some.Package.OtherKindOptions`'. Check the indentation under the kind's key." message — advice
about yaml indentation for a service with no yaml. The new error names both types instead. (The
shipped `JavaKindOptions`/`JavaScriptKindOptions` are `internal`, so no AppHost author can produce
this with them; only an out-of-tree kind can.)

Two consequences to record rather than discover:

- **`LocalKindConfig` is public** (`LocalKindConfig.cs:13`), so this is a public *behavioural*
  change even though no signature moves. It belongs in the CHANGELOG under `### Changed`.
- The round-trip hands `Validate`, `Resolve` and `ResolveDeferred` a **fresh** instance each time;
  the pass-through shares one. Neither shipped kind can see the difference — each projects the
  parsed object into a new immutable record per call (`Java/JavaKindOptions.cs:80`,
  `JavaScript/JavaScriptLocalKind.cs:369`). The rule therefore exists for out-of-tree kinds: a
  handler must not retain or mutate its options object, and the XML doc must say so.

`ILocalResourceKind`'s signature stays untouched, so there is no repeat of #63's silent-`Validate`
migration.

### 7. ATS: the shipped surface is the precedent, and five shapes this API needs were never measured

The repo already exports a lambda-taking method with the exact flag this design needs
(`BackingServiceBuilderExtensions.cs:149`), and the generated SDK in the sample proves the projection:

```typescript
addBackingService(name: string, local: () => Promise<ResourceWithConnectionString>): ResourceWithConnectionStringPromise;
```

So `addServiceCatalog(configure: (catalog: ServiceCatalogBuilder) => Promise<void>)` has in-repo
precedent, not merely probe evidence. Two standing rules from `ServiceConfigurationExports` apply
unchanged: **no generics**, and **no two exports sharing a generated name**. The bare-interface
return caveat recorded in the #134 comment (measured in
[19507-already-fixed-findings](2026-08-30-19507-already-fixed-findings.md), Probe 1) does not bite
here — the catalog builders return *handles*, not resource builders — and in any case Probe 2 of that
same document shows the bare-interface return type-checks clean once any export declares such a
receiver, which is why the shipped `AddService` is green in CI today.

**But five shapes this design depends on are in neither findings document:**

| Shape | Where this design uses it |
| --- | --- |
| Optional / named parameters, and `string[]?` | `WithRepository(url, defaultRef:)`, `WithContainer(image, port:, defaultTag:)`, `WithPrepare(cmd, windowsCommand:, mode:)` |
| An extension method whose *receiver* is a package-owned exported class | `AsJava(this ServiceDefinitionBuilder, …)` — every measured export had an Aspire type as receiver, and `ExposeMethods` is documented for *instance* methods |
| A lambda nested inside a lambda | `catalog => … .AsJava(o => …)` — a second level of guest→host re-entrancy under one `RunSyncOnBackgroundThread` |
| An enum as a *parameter* | `WithPrepare(mode: PrepareMode.Once)` — enums were measured as DTO *fields*, never as parameters |
| The generated name `addService` on two receivers | the builder's `AddService` versus `IDistributedApplicationBuilder.AddService` |

**Therefore the plan's first task is a throwaway ATS probe of exactly these five, before any public
signature is frozen** — the same standard the rest of this decision was held to. If any fails, the
fallbacks are known and cheap: no optional parameters, instance methods on the builder instead of
extension methods, and a different method name (`Define`).

All attributes and properties this needs exist in the repo's floor, **Aspire.Hosting 13.5.2**:
`AspireExportAttribute`, `AspireDtoAttribute`, `ExposeMethods`, `ExposeProperties`,
`RunSyncOnBackgroundThread`, `MethodName` (verified in the packaged
`Aspire.Hosting.xml`, lines 11982-12235). Nothing needs a version bump.

### 8. Verifying the TypeScript half needs a CLI at or above the floor — and it works

`ci.yml:349-366` pins `ASPIRE_CLI_VERSION: '13.5.3'`: the CLI writes its own version into the host
project it generates, so a CLI **below** `$(AspireVersion)` (13.5.2, `Directory.Build.props:74`)
fails `aspire restore` with NU1605 before codegen runs. Any CLI ≥ 13.5.2 clears it; CI pins the
newest release.

Measured while writing this design: the CLI on `PATH` here is **13.5.1** and is therefore unusable
for this. With `aspire.cli` 13.5.3 installed to a tool path, `aspire restore --non-interactive
--nologo` followed by `npx tsc --noEmit -p tsconfig.apphost.json` on `samples/DemoAppHostTypeScript`
is **clean**. So the stage-gate harness is confirmed working locally before anything depends on it.
The whole `.aspire/` directory must be deleted between runs, not just `modules/`
(`ci.yml:420-424`, microsoft/aspire#19603).

### 9. Every service still needs a `source`, and there is no default

`ServiceSourcesConfigCache.ResolveService:63-88` throws `DeveloperConfiguration.NotConfiguredError`
(`Config/DeveloperConfiguration.cs:288-297`) when a service has no `source`, and a blank one takes
the same route. `servicesources.local.json` is gitignored. So a code catalog alone resolves nothing.

This is #158 (`defaultSource`, same milestone), and its own argument — *let the catalog declare it,
since the catalog is committed* — lands harder on a code catalog than on yaml, because the code
catalog is unambiguously committed and unambiguously the AppHost's own statement. `defaultSource` is
one field on the very type this design introduces.

**Recommendation: pull #158 into this work as `.WithDefaultSource("local")`**, or state plainly in
the README that a code catalog still needs `servicesources.local.json`. Doing neither leaves the
issue's headline reading as a promise the code does not keep. This is open question 1.

### 10. There is no public-API baseline, and the public surface is 11 types

No `PublicAPI.*.txt`, no approval snapshots, no ApiCompat anywhere. The public surface is
`ServiceSourcesBuilderExtensions`, `BackingServiceBuilderExtensions`, `ServiceConfigurationExtensions`,
`ServiceConfigurationExports`, `ServiceEndpointExtensions`, `ILocalResourceKind`, `LocalKindConfig`,
`DeferredLocalResource`, `ServiceSourcesConfigurationException`, `JavaServiceSourcesBuilderExtensions`,
`JavaScriptServiceSourcesBuilderExtensions` — 11 types. (Counting `public` types nested inside
`internal` ones reaches 15 or 16, but those are not part of the API.)

This design adds five or six, so roughly **half again**, with no tooling to catch a later accidental
break. Hence: every new public type is `sealed`, and the suite carries the reflection test described
in [Testing](#testing) — the same shape as
`test/…/ServiceConfigurationExportsTests.cs`, whose `ExportedMethods()` reflects only over
`ServiceConfigurationExports` and therefore covers neither `AddService` nor `AddBackingService`
today.

### 11. Case-insensitive name comparison cannot be delegated to the dictionary

`ServiceCatalog.Services` is `Ordinal`, so a *yaml* catalog can legally declare both `orders:` and
`Orders:`; both survive the load, and `DeveloperConfiguration.CanonicalizeToCatalog`
(`Config/DeveloperConfiguration.cs:234-249`) reports it at `:245` as
`AmbiguousCatalogSpellingError` — but only when a developer-config entry names the service, since
`CanonicalizeToCatalog` loops over the *developer* entries (`:216-229` detects, `:245` reports). A
catalog nobody configured still loads. There is a live test for exactly that
(`test/…/Config/DeveloperConfigurationTests.cs:302-325`).

So building the merged map as `new Dictionary<string, ServiceDefinition>(OrdinalIgnoreCase)` breaks
two things at once. That yaml throws a raw `ArgumentException` on the second key before `ReadFrom` is
ever reached, replacing a good error with a bad one; and `ResolveService`'s lookup
(`ServiceSourcesConfigCache.cs:68`) becomes case-insensitive, so `AddService("Orders")` against a
yaml `orders:` goes from "not found" to resolving — a silent behaviour change for existing yaml
AppHosts, which is acceptance criterion 3.

**The merge therefore compares `OrdinalIgnoreCase` explicitly and keeps an `Ordinal` map.** Each
collision gets the error that already fits it: code-vs-yaml is the duplicate error, yaml-vs-yaml
stays `AmbiguousCatalogSpellingError`, code-vs-code is caught at the second `AddService` call.

---

## Architecture

### The domain type

Introduce `ServiceDefinition` — internal, in `…/Catalog/` — as the composed, source-agnostic entry
everything downstream reads, and a `CodeServiceCatalog` to hold the merged map (finding 3:
`ServiceCatalog.Services` cannot be retyped, because `ServiceCatalogLoader.cs:60` binds
`ServiceCatalog` with YamlDotNet directly). `ServiceMetadata` and `ServiceCatalog` stay exactly where
they are and keep doing exactly one job: **binding yaml**. `ServiceMetadata` gains `ToDefinition()`.

```
servicesources.yaml ──> ServiceCatalog / ServiceMetadata ──┐
                        (yaml DTOs; they define             ├──> ServiceDefinition
                         the yaml schema by reflection)     │       │
AddServiceCatalog(…) ──> ServiceDefinitionBuilder ──────────┘       │
                                                                    ▼
                                    IServiceSource.Resolve · LocalCheckoutPrefetch
                                    DeferredCheckout · LocalGitCheckout
```

`ServiceDefinition` carries everything `ServiceMetadata` carries, with `Kind` already normalized to
`"dotnet"`, plus `Origin` (`Yaml` with the path, or `Code`) and `KindOptions`. **No typed
`PrepareMode`** — `Prepare.Mode` stays the string it is today, parsed where it is parsed today
(finding 3); adding the enum here is what would move the parse to load time and latch it.

**Blast radius, measured.** All internal — no public signature mentions `ServiceMetadata`
(`Config/ServiceMetadata.cs:5`, `IServiceSource.cs:6`) — but wider than a first pass suggests:

| File | `ServiceMetadata` sites |
| --- | --- |
| `Git/LocalGitCheckout.cs` | 103, 127, 200, 218, 228, 286, 476 |
| `Sources/LocalProjectSource.cs` | 21, 212, 236, 269, 312, 340 |
| `Sources/LocalCheckoutPrefetch.cs` | 311, 420, 439, 592, 640 |
| `Sources/DeferredCheckout.cs` | 116 (record field), 209, 265, 436 |
| `Sources/KubernetesSource.cs` | 10, 41, 86 |
| `Sources/UrlSource.cs` | 44, 278 |
| `Sources/ContainerSource.cs` | 9, 33 |
| `Config/ServiceCatalogLoader.cs` | 17, 30, 49 (the reflection roots, which must keep pointing at the DTO); `:51-60`, `:77-92` and `:147-153` are where `ToDefinition()` gets wired |
| `Config/ServiceCatalog.cs` | 5, 14 |
| `Config/ServiceMetadata.cs` | 5 (the declaration itself, plus the new `ToDefinition()`) |
| `IServiceSource.cs` | 16 |
| `Config/ServiceSourcesConfigCache.cs` | 63 |

Twelve source files, of which `Git/LocalGitCheckout.cs` is the surprise — it is not in the `Sources/`
folder and takes `ServiceMetadata` at seven sites. `Config/RawServiceCatalog.cs:6` and
`Config/PrepareMetadata.cs:9` and `LocalKindConfig.cs:8` mention the type only in XML doc comments
and need no code change — fifteen files name it in all.
Nothing in `Prepare/` moves, now that the mode stays a string (finding 3).

plus **19 test files** that construct or pass `ServiceMetadata` directly, across all three test
projects. It is a mechanical change, but it is not a small one, and it is why the split is a task of
its own rather than a side effect.

> **Alternative, genuinely open:** put `Origin` on `ServiceMetadata` with `[YamlIgnore]` and keep the
> new container only for the merged map. It works — `KindConfig` already does exactly this — and it
> removes the largest task in stage 1. What it keeps is the opt-out trap in finding 3: the next
> field added without the attribute widens the yaml schema and shrinks the kind registry's allowed
> names, with nothing to catch it. Finding 3 tallies both sides; open question 4 asks for the call.

### The authoring API

```csharp
builder.AddServiceCatalog(catalog =>
{
    catalog.AddService("orders")
        .WithRepository("https://github.com/dotnet/aspire-samples", defaultRef: "main")
        .WithProject("samples/health-checks-ui/HealthChecksUI.ApiService/HealthChecksUI.ApiService.csproj");

    catalog.AddService("inventory")
        .WithUrl("https://httpbin.org")
        .WithContainer("nginxdemos/hello", port: 80, defaultTag: "latest");

    catalog.AddService("routing")
        .WithRepository("https://github.com/example/planning-routing")
        .WithPrepare(["./prepare.sh"], windowsCommand: ["pwsh", "-File", "prepare.ps1"], mode: PrepareMode.Once)
        .AsJava(o => o.MavenGoal("spring-boot:run").Port(8989));
});
```

**Which method enables which `source`** — the mapping a reader needs, since the method names are not
the four values a developer types into `servicesources.local.json`:

| Builder method | yaml it replaces | `"source"` it enables |
| --- | --- | --- |
| `WithRepository` + `WithProject` / `AsJava` / `AsJavaScript` / `WithKind` | `repository:`, `project:`, `defaultRef:`, `kind:` | `"local"` |
| `WithUrl` | `url:` | `"url"` |
| `WithContainer` | `container:` | `"container"` |
| `WithKubernetes` | `kubernetes:` | `"kubernetes"` |

Calls are **additive** (finding 4). A second call to the same one is a configuration error naming the
service and the block, not a silent overwrite. `WithProject` and `defaultRef` modify `WithRepository`
rather than standing beside it; the plan should try folding them into it
(`WithRepository(url, project:, defaultRef:)`) if finding 7's probe clears optional parameters, since
that also removes a chain.

Public types (all `sealed`, except the enum):

| Type | Role |
| --- | --- |
| `ServiceCatalogBuilder` | `AddService(string)` → a definition builder; accumulates entries |
| `ServiceDefinitionBuilder` | the `With*`/`As*` chain; returns itself |
| `PrepareMode` | promoted from internal; `WithPrepare` takes `PrepareMode?` |
| `JavaKindOptionsBuilder`, `JavaScriptKindOptionsBuilder` | the fluent kind-options handles |

plus `AddServiceCatalog` on `ServiceSourcesBuilderExtensions`, beside `UseDeferredCheckout` and
`AddLocalKind`:

```csharp
[AspireExport(RunSyncOnBackgroundThread = true)]
public static IDistributedApplicationBuilder AddServiceCatalog(
    this IDistributedApplicationBuilder builder, Action<ServiceCatalogBuilder> configure)
```

**Separate from `AddService`, not an inline overload** — settled in #143 recommendation 7 and not
reopened. Inline sugar can be layered over a separate catalog later without a break; the reverse
cannot.

`AddServiceCatalog` calls `DeveloperConfigFileSource.EnsureRegistered(builder)` first, as every other
entry point does (`ServiceSourcesBuilderExtensions.cs:90,186,213`,
`BackingServiceBuilderExtensions.cs:162`). Called twice it **appends**, so a helper method can
contribute entries; a name declared twice across calls is the same duplicate error as any other.

**Names.** `AddService(name)` rejects null, empty and whitespace at the call. Two code-declared
names differing only by case are rejected at the second `AddService` call, naming both.

Name comparison across the two catalogs is `OrdinalIgnoreCase`, because `DeveloperConfiguration` is,
and an ordinal check would let code `Orders` and yaml `orders` both through only to surface much
later as `AmbiguousCatalogSpellingError` against a yaml file (finding 5). **But the merge must do
that comparison itself rather than delegating it to an `OrdinalIgnoreCase` dictionary** — see
finding 11. `[ResourceName]`
is deliberately **not** used: it is analyzer-only, so it buys nothing at runtime and nothing at all
through ATS.

### Kind options

The primitive is `WithKind`, and the language sugar is built on it:

```csharp
public ServiceDefinitionBuilder WithKind(string kind, object? options = null);
```

`AddLocalKind` is public, so third parties register kinds; today their options arrive from the yaml
`<kind>:` block. Without `WithKind` an out-of-tree kind would be unconfigurable from code and
acceptance criterion 1 ("every source kind") would hold only for the two kinds this package ships.
`WithKind`'s `options` argument only works once `LocalKindConfig.Parse<T>` has the branches of
finding 6, which is why both land together in stage 1.
`AsJava`/`AsJavaScript` are sugar over it, and the README shows the three lines a third-party
package writes to add its own.

Each hands a **fluent options handle** to the caller's lambda, writing into the existing internal
options class:

```csharp
[AspireExport(ExposeMethods = true)]
public sealed class JavaKindOptionsBuilder
{
    public JavaKindOptionsBuilder MavenGoal(string goal);
    public JavaKindOptionsBuilder Port(int port);
    …
}
```

A handle rather than a public `[AspireDto]` options class, deliberately: `JavaKindOptions` is 274
lines of yaml-bound properties *and* their validation, and making it public would freeze that shape
as API and turn every yaml-schema change into a breaking one — reintroducing finding 3's coupling on
the kind side just after removing it on the catalog side.

(#134's "satellite packages extend the same builder" is stale: #187 folded the satellites into core.
The extension-method shape is kept anyway, because it is what a genuine third-party kind package
must use.)

**This re-scopes #133.** Kind options authored in code have no sibling yaml keys to collide with, so
the nesting change buys nothing for the code path. #133 shrinks to "stop the flat yaml layout getting
worse", and `IsReservedKindName` becomes a rule about *yaml-declared* kinds only — which is what it
always was.

### Composition, freezing, and the errors

`LoadedConfig.Load` becomes:

1. Read the code catalog off the builder (`ConditionalWeakTable`) and **freeze** it. A
   `ServiceCatalogBuilder` captured and mutated after this point contributes nothing, so freezing is
   what makes that a stated rule instead of a silent loss.
2. Load `servicesources.yaml` **if it exists**. Absent *and* no code catalog → finding 2's message,
   extended to name both remedies.
3. Merge into one map, comparing names `OrdinalIgnoreCase` **explicitly** and routing each kind of
   collision to its own error (finding 11): code-vs-yaml to the duplicate error below, yaml-vs-yaml
   to the existing `AmbiguousCatalogSpellingError`, code-vs-code to the `AddService` error above.
   The map itself stays `Ordinal`, so lookup for an existing yaml AppHost is byte-for-byte what it
   is today.
4. Hand the merged key set to `DeveloperConfiguration.ReadFrom` exactly as today, so
   canonical-spelling reconciliation covers code-declared names too.

Nothing here branches on `ExecutionContext.IsRunMode`. `aspire publish` and manifest generation see
the same composed catalog, and a code catalog is strictly better there — there is no file to ship.

**Duplicate** — an error naming both sources, not a merge and not a precedence rule:

> Service `'payments'` is declared twice: in code by `AddServiceCatalog(…)` and in
> `'/path/to/servicesources.yaml'`. A service belongs to one catalog; remove one of the two. To vary
> a service per developer, set its `source` in `servicesources.local.json` instead.

The last sentence is load-bearing: a duplicate is far more likely someone reaching for an override,
and per-developer *source selection* already has a home.

**Ordering** — `AddServiceCatalog` after the catalog has been read:

> `AddServiceCatalog(…)` was called after the service catalog had already been read, so its entries
> could not be seen. Because a service is resolved as it is added, the catalog must be declared
> before the first `AddService(…)` — near the top of the AppHost, next to `UseDeferredCheckout()` and
> `UseJava()`.

Thrown, not warned: the alternative is an AppHost resolving against a catalog missing half its
entries, reporting "service not found" against a name the developer can see declared three lines up.

**Which errors latch.** The duplicate and no-catalog errors are raised inside
`ConfigLoader<LoadedConfig>.Load`, so they latch and every later `AddService` is told the same thing
— consistent with today. The ordering error is raised in `AddServiceCatalog`, outside that load, so
it does not latch and would re-throw per call; there is only ever one such call reached, so this is
stated rather than fixed.

**Memory model.** The "already read" flag lives on `ConfigLoader<T>` beside `_loaded`, which is only
ever touched under `_gate`; the probe takes the same lock. This matters because
`RunSyncOnBackgroundThread = true` means `AddServiceCatalog` can itself run on a background thread.

---

## What this deliberately does not do

- **It does not remove `servicesources.local.json`.** Finding 9. Unless #158 is pulled in, a code
  catalog still needs a `source` per service.
- **It does not remove yaml.** The loader stays as one provider; whether yaml is retired at 1.0 is
  left open.
- **It does not merge code and yaml entries for one service.** Duplicates are an error.
- **It does not change `ILocalResourceKind`.** No public interface member moves, so nothing repeats
  #63's silent-`Validate` migration.
- **It does not fix #133.** It re-scopes it and explains why the code path does not need it.
- **It does not define a serialization for the catalog.** `ServiceDefinition` is left plainly
  serializable so #11 has an object to return; no format is committed to.

## Consequences accepted

- The public surface grows by about half, in a package with no API-compatibility tooling
  (finding 10). Mitigated by `sealed`, by the reflection test, and by probing the ATS shapes before
  freezing signatures — not eliminated.
- `LocalKindConfig.Parse<T>` changes observable behaviour for a public method (finding 6), and kind
  handlers now share one options instance across `Validate`/`Resolve`/`ResolveDeferred` instead of
  getting a fresh parse each time.
- The domain split touches twelve source files and nineteen test files for no behaviour change. That
  cost is real and is the reason for the staging below.
- Catalog **name comparison** becomes case-insensitive across the two catalogs while the map itself
  stays `Ordinal` (finding 11). Nothing about an existing yaml AppHost's lookup changes; the cost is
  that the merge carries collision handling by hand instead of getting it from a comparer.
- Two ways to author one catalog is two things to document and two paths to keep tested. The README
  grows; `smoketest-config-layers.sh` grows.

---

## Staging

One issue, but not one pull request — matching the repo's own delivery shape (the database source
shipped stage 1 as `AddBackingService` with the rest still open):

| Stage | Contents | Acceptance reached |
| --- | --- | --- |
| **0** | Throwaway ATS probe of finding 7's five unmeasured shapes. No shipped code. | — (de-risks stage 1) |
| **1** | `ServiceDefinition`/`CodeServiceCatalog` split; `AddServiceCatalog` with all four sources **and** `[AspireExport]` on the builders from the start; `WithKind` **with** the `LocalKindConfig.Parse<T>` branches it depends on; yaml optional; duplicate/ordering/name/collision errors; `Origin` threaded into the error strings; C# **and** TypeScript samples with no yaml | Criteria 3 and 4 in full; criteria 1 and 2 for `dotnet` and for any kind configured through `WithKind` |
| **2** | `WithPrepare`; `AsJava`/`AsJavaScript` handles (the shipped kinds' options classes are `internal`, so they are unreachable *typed* until these land — a dictionary passed to `WithKind` round-trips in stage 1) | Criteria 1 and 2 in full — parity with the yaml loader, closing the gap #134's second comment raises |

The exports move into stage 1 deliberately: they cost nothing at runtime, the whole argument of this
design rests on the shape crossing ATS, and shipping the public builders in one release and
discovering in the next that they do not project would be a breaking change in a package with no
ApiCompat (finding 10). Stage 2 is then purely additive.

**This staging is a recommendation, not a decision.** The issue reads as one deliverable.

---

## Testing

Mirroring the repo's layout, `Method_Condition_ExpectedOutcome`:

- `test/…/Catalog/ServiceCatalogBuilderTests.cs` — each `With*` populates the definition; a repeated
  block is an error naming service and block; `With*` combinations are additive (finding 4); null,
  empty and whitespace names rejected; two names differing only by case rejected naming both;
  `WithKind` carries an arbitrary options object.
- `test/…/Catalog/CatalogCompositionTests.cs` — code-only, yaml-only, both-disjoint,
  both-overlapping (the duplicate error, asserting **both** sources appear), neither (finding 2's
  extended message); a yaml catalog declaring `orders:` and `Orders:`, with a developer-config entry
  naming `orders`, still produces `AmbiguousCatalogSpellingError` and not an `ArgumentException`
  (finding 11); `AddService("Orders")`
  against a yaml `orders:` still reports not-found; a code name and a yaml name differing only by
  case is the duplicate error, not
  `AmbiguousCatalogSpellingError`.
- `test/…/AddServiceCatalogTests.cs` — the ordering error after `AddService`; **no** ordering error
  after `AddBackingService` (finding 1, asserted so the wrong rule cannot creep back); two calls
  append; a builder mutated after freeze contributes nothing; the developer-config source is
  registered by the call.
- `test/…/Catalog/CatalogErrorMessageTests.cs` — the finding-5 guard: for a code-only catalog, **no**
  `ServiceSourcesConfigurationException` message contains the string `servicesources.yaml`. Cheap,
  and it is the only thing that will keep the eight call sites honest as they change.
- `test/…/Config/ServiceCatalogLoaderTests.cs` — extended: a missing file is no longer unconditional
  failure. `Load_EveryKnownPropertyOnOneService_LoadsWithoutError`
  (`Config/ServiceCatalogLoaderTests.cs:386`) stays the schema-completeness guard and gains a sibling
  asserting `ServiceDefinition` carries every `ServiceMetadata` property, so the two cannot drift.
- `test/…/Catalog/CatalogExportsTests.cs` — the finding-10 reflection test: every exported catalog
  method non-generic, uniquely named, every builder method returning the builder.
- `test/…/LocalKindConfigTests.cs` — `Parse<T>` returns an already-typed instance unchanged; a
  wrong-typed object gets the new mismatch error naming both types; a dictionary still round-trips.
- **Smoke test**: `scripts/smoketest-config-layers.sh` extended with a code-catalog AppHost that has
  no yaml — composition and developer-config key reconciliation only meet at runtime.
- **ATS**: the stage-1 TypeScript sample, restored and strict-`tsc`'d by `📘 typescript export
  surface`. Per finding 8 a local run needs `aspire.cli` ≥ 13.5.2 on a tool path; the CLI on `PATH`
  here is 13.5.1 and fails NU1605.

---

## Documentation

- **README** — a new `## Authoring the catalog in code` section after `## Install`, the method →
  `source` mapping table, the three lines a third-party kind package writes, and an explicit note
  that `servicesources.local.json` is still required (finding 9). `## Getting started` (currently
  `README.md:121-1468`) is reframed to present the two authoring surfaces before dropping into yaml.
  This is the largest documentation change the repo has taken.
- **CHANGELOG** — `### Added` for the API, `### Changed` for yaml becoming optional **and** for
  `LocalKindConfig.Parse<T>`'s new behaviour, hand-edited into `## [Unreleased]` with `[#134]:`
  appended to the link block (the fragment system in #185 was not merged).
- This spec's `**Status:**` header updated when the work lands, as
  `2026-08-28-servicesources-prepare-step-design.md:4` does.

---

## Open questions for the reviewer

1. **`servicesources.local.json`, and #158.** Finding 9. Pull `defaultSource` into this work so the
   headline goal is actually reached, or document the requirement and leave #158 alone? This is the
   one that changes what the feature *is*.
2. **Staging.** Three stages as above, or one PR?
3. **`With*` versus the issue's `From*`**, and whether `WithProject`/`defaultRef` fold into
   `WithRepository`.
4. **Is the DTO/domain split worth it now?** Finding 3 tallies it: the honest forcing case is one
   field (`Origin`) plus a reflection trap, against 12 source and 19 test files changed for no
   behaviour change — and `KindConfig` proves the cheap `[YamlIgnore]` route works. Split (closing
   #73), or take the cheap route and leave #73 open? This is the largest single cost in the plan.
5. **Is yaml targeted for removal at 1.0?** This design assumes kept indefinitely as one provider.
6. **Serialization.** `ServiceDefinition` is left plainly serializable for #11 but nothing serializes
   it. Is that the right amount of anticipation?
