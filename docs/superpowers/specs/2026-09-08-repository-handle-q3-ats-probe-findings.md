# ATS Probe — Inherited Instance Methods on an Exported Handle (#291, open question 3)

**Date:** 2026-09-08
**Status:** Measured. All four shapes cross. Answers open question 3 of
[the repository-handle design](2026-09-08-repository-handle-design.md) **yes**, and turns that
design's rejected separate-builder-type alternative into its recommended shape.
**Method:** the same one [stage 0](2026-09-07-code-catalog-stage0-ats-probe-findings.md) used — a
throwaway probe file under `src/Aspire.Hosting.ServiceSources/`, `aspire restore` in
`samples/DemoAppHostTypeScript` (whose `aspire.config.json` points at `../../src`), the generated
`.aspire/modules/aspire.mts` read directly, and `npx tsc --noEmit -p tsconfig.apphost.json` at the
call site. Probe code reverted; only this document survives.
**Toolchain:** Aspire CLI `13.5.3` (on `PATH` here, above the repo's `$(AspireVersion)` floor of
`13.5.2`), .NET SDK `10.0.400`. Tree at `ef70ff5`.

---

## Why this was asked

The design wanted `monorepo.AddService(…)` to return a builder that simply *has* no
`WithRepository`, so a grouped service naming its own repository becomes a **compile** error rather
than the runtime configuration error the design settled for. That needs the six shared `With*`
methods declared once and inherited by two derived handles — and the design rejected it, unmeasured,
on the fear that each shared method would become a second receiver for its generated capability id:
the collision [stage 0's finding 5](2026-09-07-code-catalog-stage0-ats-probe-findings.md) measured as
**silent**.

That fear was misplaced, and the reason is finding 1.

## 1 — Capability ids for `ExposeMethods` instance methods are receiver-qualified

This is the finding everything else follows from. An instance method projected by
`[AspireExport(ExposeMethods = true)]` gets a capability id of the form

```
{TypeNamespace}/{ExportedTypeName}.{camelCaseMethodName}
```

not the `{AssemblyName}/{camelCaseMethodName}` that an **extension** method exported with
`[AspireExport]` gets. Read straight out of the generated SDK, for one shared C# method inherited by
two exported handles:

```
'Aspire.Hosting.ServiceSources.Catalog/Q3ProbeAlphaBuilder.q3ProbeSharedFromUnexportedBase'
'Aspire.Hosting.ServiceSources.Catalog/Q3ProbeBetaBuilder.q3ProbeSharedFromUnexportedBase'
```

The receiver type is *part of the id*. Two receivers therefore cannot collide on a method name at
all, however many of them there are.

**Stage 0's finding 5 does not generalise to instance methods, and this is worth stating plainly
because Stage 1 acted on it as though it did.** The shape stage 0 measured as colliding was an
*extension* method:

```csharp
[AspireExport]
public static Stage0ProbeCatalogBuilder AddService(
    this Stage0ProbeCatalogBuilder catalog, string name) => catalog;   // → …ServiceSources/addService
```

Extension methods are exported as free functions and are keyed by assembly, so that one really did
collide with `ServiceSourcesBuilderExtensions.AddService`. An instance method on the same receiver
would not have. See finding 4 for the direct measurement, and for what it means for Stage 1's
`addServiceToCatalog`.

## 2 — Inherited instance methods project, from an unexported base (variant A)

```csharp
public abstract class Q3ProbeUnexportedBase                       // NOT exported
{
    public string Q3ProbeSharedFromUnexportedBase(string value) => value;
}

[AspireExport(ExposeMethods = true)]
public sealed class Q3ProbeAlphaBuilder : Q3ProbeUnexportedBase { public string Q3ProbeOnlyOnAlpha(string v) => v; }

[AspireExport(ExposeMethods = true)]
public sealed class Q3ProbeBetaBuilder : Q3ProbeUnexportedBase { public string Q3ProbeOnlyOnBeta(string v) => v; }
```

Generated:

```typescript
export interface Q3ProbeAlphaBuilder {
    toJSON(): MarshalledHandle;
    q3ProbeOnlyOnAlpha(value: string): Promise<string>;
    q3ProbeSharedFromUnexportedBase(value: string): Promise<string>;   // ← inherited
}
export interface Q3ProbeBetaBuilder {
    toJSON(): MarshalledHandle;
    q3ProbeOnlyOnBeta(value: string): Promise<string>;
    q3ProbeSharedFromUnexportedBase(value: string): Promise<string>;   // ← inherited
}
```

**Clean, and better than clean: the base does not appear in the generated SDK at all.** No
`Q3ProbeUnexportedBase` handle type, no interface, nothing — the inherited method is projected onto
each concrete receiver and the base stays invisible to a TypeScript AppHost. That is exactly what a
shared implementation detail should do.

Build: `0 Warning(s), 0 Error(s)` — no `ASPIREEXPORT013`.

## 3 — The same works with an exported base, but pollutes the SDK (variant B)

Marking the base `[AspireExport(ExposeMethods = true)]` also works — an abstract class exports
without complaint — and both derived handles still carry the inherited method. The difference is
that the base now *also* materialises as its own handle type:

```
type Q3ProbeExportedBaseHandle = Handle<'…Catalog.Q3ProbeExportedBase'>;
export interface Q3ProbeExportedBase { toJSON(): MarshalledHandle; q3ProbeSharedFromExportedBase(value: string): Promise<string>; }
```

**So leave the base unexported.** Variant A is the shape to use; variant B adds a type no AppHost can
obtain an instance of.

## 4 — Two receivers, both with an instance `AddService`, no explicit ids (variant D)

```csharp
[AspireExport(ExposeMethods = true)]
public sealed class Q3ProbeFirstReceiver { public string AddService(string name) => name; }

[AspireExport(ExposeMethods = true)]
public sealed class Q3ProbeSecondReceiver { public string AddService(string name) => name; }
```

Build: `0 Warning(s)`. Generated, with the shipped extension method's id still present and unchanged:

```
'Aspire.Hosting.ServiceSources.Catalog/Q3ProbeFirstReceiver.addService'
'Aspire.Hosting.ServiceSources.Catalog/Q3ProbeSecondReceiver.addService'
'Aspire.Hosting.ServiceSources/addService'                                 ← the shipped extension
```

Both project as plain `addService` on their own receiver. Nothing dropped, nothing aliased, no
warning.

### Stage 1's explicit `addServiceToCatalog` id is unnecessary

Measured directly, by removing it from the shipped Stage-1 code:

```csharp
[AspireExport]                                    // was [AspireExport("addServiceToCatalog")]
public ServiceDefinitionBuilder AddService(string name)
```

Build: `0 Warning(s), 0 Error(s)`. Regenerated SDK:

```typescript
export interface ServiceCatalogBuilder {
    toJSON(): MarshalledHandle;
    addService(name: string): ServiceDefinitionBuilderPromise;
}
```

with `'Aspire.Hosting.ServiceSources.Catalog/ServiceCatalogBuilder.addService'` at
`aspire.mts:10337` and the shipped `'Aspire.Hosting.ServiceSources/addService'` still at `:12140`.
All three of stage 0's signals — build warning, generated class body, call-site `tsc` — say there is
no collision.

**Consequence.** Stage 1 named its capability `addServiceToCatalog` to avoid a collision that could
not occur, so a TypeScript AppHost writes `catalog.addServiceToCatalog('orders')` where #134's and
#291's own sketches both write `catalog.addService('orders')`. Stage 1 is unreleased, so this is free
to correct — see the recommendation at the end. The probe was reverted, so `addServiceToCatalog` is
still what the tree does today.

## 5 — A generic self-typed base projects, *and* keeps fluent chaining (variant E)

The shape a shared base needs if `WithProject` is to return the derived type rather than the base:

```csharp
public abstract class Q3ProbeGenericBase<TSelf>
    where TSelf : Q3ProbeGenericBase<TSelf>
{
    public TSelf Q3ProbeSharedFluent(string value) => (TSelf)this;
}

[AspireExport(ExposeMethods = true)]
public sealed class Q3ProbeEpsilonBuilder : Q3ProbeGenericBase<Q3ProbeEpsilonBuilder>
{
    public string Q3ProbeOnlyOnEpsilon(string value) => value;
}
```

Generated — note the return type:

```typescript
export interface Q3ProbeEpsilonBuilder {
    toJSON(): MarshalledHandle;
    q3ProbeOnlyOnEpsilon(value: string): Promise<string>;
    q3ProbeSharedFluent(value: string): Q3ProbeEpsilonBuilderPromise;   // ← the DERIVED promise
}
```

The generic parameter is resolved to the derived type, so a derived-only method chains off a shared
one. Verified at the call site rather than inferred:

```typescript
const q3Eps = builder.q3ProbeEpsilon();
console.log(await q3Eps.q3ProbeSharedFluent('eps-shared').q3ProbeOnlyOnEpsilon('eps-own'));
```

This was the risk that would have made the whole approach not worth it — a shared method returning
the base would have broken every chain after it, in C# and in TypeScript. It does not.

## Call-site verification

Every shape above exercised together in `samples/DemoAppHostTypeScript/apphost.mts` — inherited
methods on both derived receivers for variants A and B, `addService` on both variant-D receivers, and
the variant-E chain — then:

```
npx tsc --noEmit -p tsconfig.apphost.json   →   exit 0, no diagnostics
```

## Verdict

| Shape | Result |
| --- | --- |
| 1. Capability id scheme for `ExposeMethods` instance methods | **Receiver-qualified** — two receivers cannot collide |
| 2. Inherited methods project from an **unexported** base | **Clean**, and the base stays out of the SDK |
| 3. Inherited methods project from an **exported** base | Works, but adds an unobtainable handle type — don't |
| 4. Instance `AddService` on two receivers, no explicit ids | **Clean** — and Stage 1's explicit id was never needed |
| 5. Generic self-typed base with derived-type fluent returns | **Clean** — chaining survives |

**Open question 3 is answered: measure it, and the separate builder type is cheap.** The design's
rejection of it rested on a cost that does not exist. Two runtime configuration errors —
`WithRepository` and `WithPrepare` on a grouped service — become compile errors, which is #291's own
stated ambition applied one level further in.

**Recommendations:**

1. **Adopt the split builder types** in the repository-handle design: a public, unexported generic
   base carrying the shared `With*` methods, and two exported derived handles.
2. **Drop the planned `addServiceToRepository` explicit id** — plain `AddService` on the repository
   handle projects as `addService`, matching #291's TypeScript sketch verbatim.
3. **Separately, and as a #134 correction rather than part of #291: revert Stage 1's
   `addServiceToCatalog` to plain `AddService`.** It is unreleased, one sample calls it
   (`DemoAppHostTypeScriptCodeCatalog/apphost.mts`), and leaving it means the TypeScript surface
   carries a name invented to dodge a collision that cannot happen.
4. **Add a note to the stage-0 findings** recording that its finding 5 is specific to *extension*
   methods, so the next reader does not re-apply it to an instance method and invent another id.
