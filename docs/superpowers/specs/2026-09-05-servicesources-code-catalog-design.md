# Aspire.Hosting.ServiceSources — Authoring the Service Catalog in Code

**Date:** 2026-09-05
**Status:** Draft — for review before an implementation plan is written. Nothing here has shipped.
**Resolves:** GitHub issue #134 (the service catalog should be authorable in the AppHost's own
language, so `servicesources.yaml` becomes optional rather than required).
**Would also resolve:** #73 (split the yaml DTO from the catalog domain model) — see
[The domain type](#the-domain-type), which argues the split is forced here rather than optional.
**Re-scopes:** #133 (nest kind options under a fixed key) — see
[Kind options](#kind-options-in-code).
**Leaves a seam for:** #158 (`defaultSource`), #11 (the central registry).
**Builds on:** [the typed-catalog ATS findings](2026-08-30-typed-catalog-ats-findings.md) (#71,
amended by #143) and [the 19507 findings](2026-08-30-19507-already-fixed-findings.md) (#137).

---

## Motivation

The catalog is the one part of this package a developer must write in yaml. Every other statement an
AppHost makes — which services it wants, how each is configured, which kinds are registered — is
already C# or, through ATS, TypeScript. Removing the need for yaml is a goal of Aspire itself, and
the deciding argument in the #71 finding is alignment: the app model is code in the AppHost's own
language, `AddProject`/`AddContainer` are what that looks like, and ATS extends it to guest
languages. A yaml catalog in the middle of that works against the framework this package extends.

The technical objection was measured away in #71: a fluent, lambda-based catalog API crosses ATS and
runs end to end from a TypeScript AppHost, with the lambda's fluent calls re-entering .NET on a
handle and producing real Aspire resources. The sharing objection does not survive either — every
AppHost repository carries its own `servicesources.yaml`, so the catalog is duplicated rather than
shared, and real cross-repository sharing is #11's registry.

**"Optional", not "deleted".** The loader stays, for AppHosts that already have a
`servicesources.yaml` and for the case yaml genuinely serves — editing the catalog without
rebuilding the AppHost.

---

## Findings that constrain the design

These are read out of the code as it stands on `main` (5996068), not assumed. Each one closes off a
design that would otherwise look reasonable.

### 1. The catalog is loaded lazily, once, by the first thing that asks — there is no lifecycle hook

`ServiceSourcesConfigCache.LoadedFor(builder)` memoizes a `LoadedConfig` per builder in a
`ConditionalWeakTable`, behind a lock, latching a `ServiceSourcesConfigurationException` so every
later caller is told what the first was told. `LoadedConfig.Load` is one method:

```csharp
var catalog = ServiceCatalogLoader.Load(Path.Combine(builder.AppHostDirectory, "servicesources.yaml"));
return new LoadedConfig
{
    Catalog = catalog,
    DeveloperConfig = DeveloperConfiguration.ReadFrom(builder, catalog.Services.Keys),
};
```

**That single line is the whole integration point.** Four call sites reach it —
`ServiceSourcesBuilderExtensions.AddService`, `BackingServiceBuilderExtensions.AddBackingService`,
`Sources/LocalCheckoutPrefetch.Run`, and `BackingServices/BackingServiceConfigAudit` — so a
code-authored catalog does not need a new pipeline. It needs to be *present on the builder before
that method runs*.

It also fixes the shape of the ordering rule. The rule is not "before the first `AddService`"; it is
**before the first call that reads the catalog**, which includes `AddBackingService`. The error must
say that, because an AppHost that calls `AddBackingService` first would otherwise be told to move a
line above a call it does not have.

### 2. Missing yaml is a hard error today, and that error is the good one

`ServiceCatalogLoader.Load` (line 53):

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

### 3. `ServiceMetadata` is simultaneously the yaml DTO, the domain model, and the yaml *schema*

```csharp
internal sealed class ServiceMetadata
{
    public string Repository { get; set; } = "";
    public string Project { get; set; } = "";
    public string? DefaultRef { get; set; }
    public KubernetesMetadata? Kubernetes { get; set; }
    public UrlMetadata? Url { get; set; }
    public ContainerMetadata? Container { get; set; }
    public PrepareMetadata? Prepare { get; set; }
    public string Kind { get; set; } = LocalKinds.Dotnet;
    [YamlIgnore] public object? KindConfig { get; set; }
}
```

`ServiceCatalogLoader` derives its validation from this type by reflection —
`KnownTopLevelProperties` is its camel-cased property names, `KnownNestedProperties` is the same for
each property whose type `IsNestedBlock` (a *namespace* test: any class in
`Aspire.Hosting.ServiceSources.Config` beside it), and
`IsReservedKindName(kind) => KnownTopLevelProperties.Contains(kind)` is what stops
`LocalKindRegistry.Register` taking the name `url` or `prepare`.

**The consequence for this work: adding a property to `ServiceMetadata` silently widens the accepted
yaml schema and silently removes a name from the kind registry's allowed set.** A code-authoring API
that needs to carry anything the yaml does not — a typed kind-options object, a `defaultSource`
seam, a provenance marker saying which source declared the entry — cannot put it there without
changing yaml's meaning as a side effect. This is what forces the DTO/domain split here, and it is a
different and better reason than the one #71 gave for it. The #143 amendment is explicit that the
split does *not* remove `IsReservedKindName` (that is a consequence of the document's layout, and is
#133's job), so the split must be justified on this coupling alone — and it is.

### 4. One entry may carry every source at once, so the code API must be additive

README "Combining sources on one catalog entry": a single entry can carry `repository:`/`project:`
*and* `kubernetes:` *and* `url:` *and* `container:` blocks simultaneously; the catalog describes how
each source *would* resolve the service and `servicesources.local.json` picks which one applies. The
sketch in #134 spells this `FromRepository(...)` / `FromUrl(...)` / `FromContainer(...)`, which reads
as mutually exclusive and would mislead exactly where the package's central idea lives.

**Recommendation: `With*` rather than `From*`** — `WithRepository`, `WithUrl`, `WithContainer`,
`WithKubernetes` — matching both Aspire's own `With…` convention and the additive semantics. A
second call to the same one is a configuration error naming the service and the block, not a
silent overwrite.

### 5. Kind options reach a handler as an untyped yaml dictionary, round-tripped through yaml

`ServiceMetadata.KindConfig` is `object?` — the raw `Dictionary<object, object>` fished out of the
yaml by `RawServiceCatalog`. `ILocalResourceKind` (public) takes it as `object? rawConfig`, and the
only supported way to read it is `LocalKindConfig.Parse<T>(rawConfig, serviceName)`, which
**re-serializes the dictionary to yaml and deserializes it into `T`** with a deserializer that
deliberately is not `IgnoreUnmatchedProperties()`, so a typo inside the block is caught.

A code-authored `.AsJava(o => o.MavenGoal("spring-boot:run"))` produces a `JavaKindOptions` instance
directly. Serializing it back to a dictionary so `Parse<T>` can parse it again would be absurd. The
cheap, compatible fix is a pass-through at the top of `Parse<T>`:

```csharp
if (rawConfig is T alreadyTyped) return alreadyTyped;
```

One statement. It keeps `ILocalResourceKind`'s signature untouched — **no public interface member
changes, so no repeat of #63's silent-`Validate` migration** — and it means every existing kind
handler works with a code-authored options object without being recompiled or edited.

### 6. ATS: the fluent handles are safe; anything returning `IResourceBuilder<T>` is not

From the #143 amendment on #134, measured on 13.5.1:

| `[AspireExport]` method returns | strict `tsc` |
| --- | --- |
| `IResourceBuilder<IResourceWithServiceDiscovery>` | ❌ 6 × TS2552 on its own |
| `IResourceBuilder<ConcreteClass>` | ✅ |
| `IResourceBuilder<IInterface>` + `[AspireExport]` on the interface | ❌ |

The catalog's fluent handles return *handles*, not resource builders, so they are unaffected — and
`[AspireExport(ExposeMethods = true)]` on a concrete builder **class** is exactly shape 2a/2c, which
ran end to end. `AddServiceCatalog` itself must carry `RunSyncOnBackgroundThread = true`, because the
lambda re-enters the host through ATS; `AddBackingService` is the existing precedent for that flag in
this repo.

Two rules from `ServiceConfigurationExports` apply unchanged: **no generics** (ATS erases `T` to its
constraint, or drops the method), and **no two exports sharing a generated name**.

All four attributes and properties this needs — `AspireExportAttribute`, `AspireDtoAttribute`,
`ExposeMethods`, `ExposeProperties`, `RunSyncOnBackgroundThread`, `MethodName` — are present in
`Aspire.Hosting` **13.5.2**, the repo's floor (verified against the packaged assembly). Nothing here
needs a version bump.

### 7. Verifying the TypeScript half needs a CLI at or above the floor

`ci.yml`'s `📘 typescript export surface` job pins `ASPIRE_CLI_VERSION: '13.5.3'` and explains why:
the CLI pins its own version into the host project it generates, so a CLI **below** `$(AspireVersion)`
(13.5.2) fails `aspire restore` with NU1605 before codegen runs. The CLI installed on this machine is
**13.5.1**, which is below the floor. Reproducing the TS half locally therefore needs
`dotnet tool install --tool-path <tmp> aspire.cli --version 13.5.3` first, as CI does — not the
`~/.aspire/bin/aspire` on `PATH`. The job also `rm -rf .aspire`s nothing by default; a local
reproduction must delete the whole `.aspire/` directory between runs (microsoft/aspire#19603).

### 8. There is no public-API baseline, and the public surface is 15 types

No `PublicAPI.*.txt`, no ApiCompat, no approval snapshots. The nearest thing is
`test/Aspire.Hosting.ServiceSources.Tests/ServiceConfigurationExportsTests.cs`, which reflects over
the exported methods and asserts they are non-generic, uniquely named, cover the documented shapes
and chain. **A code catalog roughly doubles the public surface, with no tooling to catch a later
accidental break** — so this design carries its own reflection test in the same style, and every new
public type is `sealed`.

---

## Architecture

### The domain type

Introduce `ServiceDefinition` — internal, in `…/Catalog/` — as the composed, source-agnostic entry
that everything downstream reads. `ServiceMetadata` stays exactly where it is and keeps doing exactly
one job: **binding yaml**. It gains one method, `ToDefinition()`.

```
servicesources.yaml ──> ServiceMetadata ──┐
                        (yaml DTO;         ├──> ServiceDefinition ──> IServiceSource.Resolve
                         defines the       │                          LocalCheckoutPrefetch
                         yaml schema)      │                          DeferredCheckout
AddServiceCatalog(…) ──> ServiceDefinitionBuilder ──┘
```

`ServiceDefinition` carries what `ServiceMetadata` carries, plus two things that must not touch the
yaml schema:

- `CatalogSource Origin` — `Yaml` or `Code`, with the yaml path when it is `Yaml`. This is what makes
  the duplicate error able to name both sources, and what lets a "not found" message say which
  catalogs were searched.
- `object? KindOptions` — already-typed options from the code API, handed to the kind handler
  unchanged (finding 5). Yaml entries put their raw dictionary in the same slot; the handler cannot
  tell the difference because `LocalKindConfig.Parse<T>` absorbs both.

Consumers change by signature only: `IServiceSource.Resolve`, `LocalCheckoutPrefetch.*`,
`DeferredCheckout.Register/RegisterKind` and `ServiceSourcesConfigCache.ResolveService` take
`ServiceDefinition` instead of `ServiceMetadata`. All internal; `ILocalResourceKind` is untouched.

> **This closes #73**, but for finding 3's reason rather than #71's. If the reviewer disagrees that
> the split is worth its blast radius, the fallback is to add the two fields to `ServiceMetadata`
> with `[YamlIgnore]` — which works, and costs a permanent trap: `[YamlIgnore]` keeps a property out
> of `YamlProperties`, so the field would *not* widen the schema, but the next person adding a field
> without the attribute would, silently. Recorded as an alternative, not recommended.

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

Three new public types, all `sealed`:

| Type | Role | ATS |
| --- | --- | --- |
| `ServiceCatalogBuilder` | `AddService(string)` → a definition builder; accumulates entries | `[AspireExport(ExposeMethods = true)]` |
| `ServiceDefinitionBuilder` | the `With*`/`As*` chain; returns itself | `[AspireExport(ExposeMethods = true)]` |
| `PrepareMode` | promoted from internal — `WithPrepare` takes the enum, not a string | plain enum, crosses as-is |

plus `AddServiceCatalog` on `ServiceSourcesBuilderExtensions`, next to `UseDeferredCheckout` and
`AddLocalKind`:

```csharp
[AspireExport(RunSyncOnBackgroundThread = true)]
public static IDistributedApplicationBuilder AddServiceCatalog(
    this IDistributedApplicationBuilder builder, Action<ServiceCatalogBuilder> configure)
```

**Separate from `AddService`, not an inline overload** — settled in #143 recommendation 7 and not
reopened here. "What a service *is*" and "I depend on this service" are the seam this package exists
to provide; inline sugar can be layered over a separate catalog later without a break, and the
reverse cannot.

`AddServiceCatalog` calls `DeveloperConfigFileSource.EnsureRegistered(builder)` first, as every other
entry point does, so the configuration chain is complete from that line on. Called twice, it
**appends** — two calls are how a helper method contributes entries — and a name declared twice
across them is the same duplicate error as any other.

### Kind options in code

`.AsJava(...)` / `.AsJavaScript(...)` are extension methods on `ServiceDefinitionBuilder`, shipped
beside `UseJava()`/`UseJavaScript()` in `…/Java/` and `…/JavaScript/`. Each sets `Kind` and hands a
**fluent options handle** to the caller's lambda:

```csharp
[AspireExport(ExposeMethods = true)]
public sealed class JavaKindOptionsBuilder      // writes into the existing internal JavaKindOptions
{
    public JavaKindOptionsBuilder MavenGoal(string goal);
    public JavaKindOptionsBuilder Port(int port);
    …
}
```

A fluent handle rather than a public `[AspireDto]` options class, deliberately: `JavaKindOptions` is
274 lines of yaml-bound properties *and* their validation, and making it public would freeze that
shape as API and turn every yaml-schema change into a breaking one — reintroducing finding 3's
coupling on the kind side just after removing it on the catalog side. The handle is shape 2a/2c from
the ATS findings, which is the shape that ran end to end.

**This re-scopes #133.** Kind options authored in code have no sibling yaml keys to collide with, so
the nesting change buys nothing for the code path. #133 shrinks to "stop the flat yaml layout getting
worse", and `IsReservedKindName` becomes a rule about *yaml-declared* kinds only — which is what it
always was.

### Composition, and the two errors that matter

`LoadedConfig.Load` becomes:

1. Read the code catalog, if any, off the builder (`ConditionalWeakTable`, like every other
   per-builder state in this package).
2. Load `servicesources.yaml` **if it exists**. If it does not and no code catalog was declared,
   throw finding 2's message, extended to name both remedies.
3. Merge into one `Dictionary<string, ServiceDefinition>`.
4. Hand `catalog.Services.Keys` to `DeveloperConfiguration.ReadFrom` exactly as today — the merged
   key set, so canonical-spelling reconciliation covers code-declared names too.

**Duplicate.** A name declared in both is an error naming both sources — not a merge, not a
precedence rule:

> Service `'payments'` is declared twice: in code by `AddServiceCatalog(…)` and in
> `'/path/to/servicesources.yaml'`. A service belongs to one catalog; remove one of the two. To vary
> a service per developer, set its `source` in `servicesources.local.json` instead.

The last sentence is load-bearing: a duplicate is far more likely someone reaching for an override,
and per-developer *source selection* already has a home.

**Ordering.** `AddServiceCatalog` after the catalog has been read is an error stating the rule
plainly:

> `AddServiceCatalog(…)` was called after the service catalog had already been read, so its entries
> could not be seen. Because a service is resolved as it is added, the catalog must be declared
> before the first call that reads it — `AddService(…)`, `AddBackingService(…)` — near the top of the
> AppHost, next to `UseDeferredCheckout()` and `UseJava()`.

Detection: `ServiceSourcesConfigCache` exposes whether its `ConfigLoader<LoadedConfig>` has already
produced a value. This is a *thrown* error, not a warning: the alternative is an AppHost that
silently resolves against a catalog missing half its entries and reports "service not found" against
a name the developer can see declared three lines up.

`LocalCheckoutPrefetch.Run` swallows `ServiceSourcesConfigurationException` by design ("speculation
must never be the thing that fails the `AddService` call"), so it cannot reach the ordering error
first, and `BackingServiceConfigAudit` runs at `BeforeStart`, after every declaration. Neither
changes.

---

## Staging

This is one issue but not one pull request. Measured against the repo's own delivery shape — the
database source shipped stage 1 as `AddBackingService` (#144/#199) with the rest still open — the
work splits three ways, each independently shippable, each keeping every acceptance criterion of the
previous:

| Stage | Contents | Acceptance reached |
| --- | --- | --- |
| **1** | `ServiceDefinition` split; `AddServiceCatalog` in C# with all four sources; yaml optional; duplicate and ordering errors; C# sample with no yaml | "An AppHost with no `servicesources.yaml` can declare and resolve every source kind"; "an existing yaml AppHost keeps working"; "a service declared twice fails naming both" |
| **2** | `[AspireExport]` on the catalog builders; TypeScript sample with no yaml; `typecheck-typescript` extended | "The same is true from a TypeScript AppHost" |
| **3** | `WithPrepare`; `AsJava`/`AsJavaScript` options handles; `LocalKindConfig.Parse<T>` pass-through | Feature parity with the yaml loader; closes the gap the #134 comment raises |

Stage 1 is the one that carries the risk (the domain split touches every consumer); stages 2 and 3
are additive. **This staging is a recommendation for the reviewer, not a decision** — the issue reads
as one deliverable, and the acceptance criteria are only all met at the end of stage 3.

---

## Testing

Mirroring the repo's layout, tests named `Method_Condition_ExpectedOutcome`:

- `test/…/Catalog/ServiceCatalogBuilderTests.cs` — each `With*` populates the definition; a repeated
  block is an error naming the service and the block; `AddService` twice in one lambda is the
  duplicate error; `With*` combinations are additive (finding 4).
- `test/…/Catalog/CatalogCompositionTests.cs` — code-only, yaml-only, both-disjoint, both-overlapping
  (the duplicate error, asserting **both** sources appear in the message), neither (finding 2's
  extended message).
- `test/…/AddServiceCatalogTests.cs` — ordering error after `AddService` and after
  `AddBackingService`; two `AddServiceCatalog` calls append; the developer-config source is
  registered by the call.
- `test/…/Config/ServiceCatalogLoaderTests.cs` — extended: a missing file is no longer unconditional
  failure. `Load_EveryKnownPropertyOnOneService_LoadsWithoutError` stays the schema-completeness
  guard, and gains a sibling asserting `ServiceDefinition` carries every `ServiceMetadata` property,
  so the two models cannot drift.
- `test/…/CatalogExportsTests.cs` — the finding-8 reflection test: every exported catalog method is
  non-generic, uniquely named, and every builder method returns the builder.
- `test/…/LocalKindConfigTests.cs` — extended: `Parse<T>` returns an already-typed instance
  unchanged, and still round-trips a dictionary.
- **Smoke test**: `scripts/smoketest-config-layers.sh` extended with a code-catalog AppHost that has
  no yaml, since composition and the developer-config key reconciliation only meet at runtime.
- **ATS**: the stage-2 TypeScript sample, restored and strict-`tsc`'d by `📘 typescript export
  surface`. Per finding 7 this needs `aspire.cli` 13.5.3 on a tool path locally; the CLI on `PATH`
  here is 13.5.1 and will fail NU1605.

---

## Documentation

- **README** — a new `## Authoring the catalog in code` section after `## Install`, and `## Getting
  started` reframed to present the two authoring surfaces before dropping into yaml. This is the
  largest doc change the repo has taken: `## Getting started` currently runs lines 121–1468 and is
  written as though yaml is the only catalog.
- **CHANGELOG** — `### Added` for the API and `### Changed` for yaml becoming optional, hand-edited
  into `## [Unreleased]` with `[#134]:` appended to the link block (the fragment system in #185 was
  not merged).
- This spec's `**Status:**` header updated to `Implemented` with a link to the plan, per the
  convention every other spec here follows.

---

## Open questions for the reviewer

1. **Staging.** Three PRs as above, or one? The acceptance criteria are only all met at stage 3.
2. **`With*` versus the issue's `From*`.** Finding 4 says the blocks are additive and `From*` reads
   exclusive. Renaming departs from the sketch in #134.
3. **#73.** This design closes it as a consequence, for finding 3's reason. Worth confirming that is
   wanted here rather than kept as its own change.
4. **Is yaml targeted for removal at 1.0?** This design assumes kept indefinitely as one provider,
   as #134 assumes. Nothing here forecloses removal.
5. **Serialization.** `ServiceDefinition` is left plainly serializable so #11 has a catalog object to
   return, but nothing serializes it yet and no format is committed to. Confirm that is the right
   amount of anticipation.
6. **`defaultSource` (#158)** is in the same milestone and adds a field to exactly this type. Land it
   before, after, or together?
