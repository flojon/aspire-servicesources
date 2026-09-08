# ATS Probe — Inherited Instance Methods on an Exported Handle (#291, open question 3)

**Date:** 2026-09-08
**Status:** Measured, nine shapes. Answers open question 3 of
[the repository-handle design](2026-09-08-repository-handle-design.md) **yes**.
Extended the same day: review objected that `monorepo.AddService(…)` inverts ownership — the catalog
owns services, and a repository is one source's worth of a service's configuration, not a container
for services — which sent shapes **6–9** after a different question: can a handle travel as a
*parameter*, so services stay on the catalog? It can (finding 6), which makes the split builder types
of findings 2–5 unnecessary; but the natural spelling is a C# overload, and an overload **silently
loses one half** (finding 7). Findings 8 and 9 are the fix and its cost.
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

## 6 — An exported handle crosses as a *parameter*, and need not be awaited

Probed after review raised that `monorepo.AddService("orders")` inverts ownership — the catalog owns
services; a repository is one source's worth of a service's configuration, not a container for
services. The shape that does not invert it needs a handle to travel as an argument:

```csharp
[AspireExport(ExposeMethods = true)]
public sealed class Q3ProbeServiceHandle
{
    public string Q3ProbeWithRepositoryHandle(Q3ProbeRepoHandle repository) => "handle-accepted";

    public string Q3ProbeWithRepositoryHandleAndProject(Q3ProbeRepoHandle repository, string? project = null) =>
        project ?? "no-project";
}
```

Generated:

```typescript
export interface Q3ProbeServiceHandle {
    toJSON(): MarshalledHandle;
    q3ProbeWithRepositoryHandle(repository: Awaitable<Q3ProbeRepoHandle>): Promise<string>;
    q3ProbeWithRepositoryHandleAndProject(repository: Awaitable<Q3ProbeRepoHandle>, options?: Q3ProbeWithRepositoryHandleAndProjectOptions): Promise<string>;
}
```

**Clean, and more forgiving than expected.** The parameter type is `Awaitable<T>`, so a TypeScript
AppHost can pass the un-awaited promise straight from `AddRepository`:

```typescript
const q3Repo = builder.q3ProbeRepo();          // deliberately NOT awaited
await q3Svc.q3ProbeWithRepositoryHandle(q3Repo);
await q3Svc.q3ProbeWithRepositoryHandle(await builder.q3ProbeRepo());   // awaited works too
```

An optional scalar beside the handle collapses into the usual options bag. Both forms type-check.

## 7 — An overload pair on one receiver silently loses one, with **no** warning

This is the trap, and it is quieter than the collision stage 0 found. The real choice is
`WithRepository(string url)` versus `WithRepository(RepositoryBuilder repository)` — a C# overload:

```csharp
[AspireExport(ExposeMethods = true)]
public sealed class Q3ProbeOverloadHandle
{
    public string Q3ProbeWithRepo(string url) => url;
    public string Q3ProbeWithRepo(Q3ProbeRepoHandle repository) => "handle";
}
```

Build: `0 Warning(s), 0 Error(s)` — **not even an `ASPIREEXPORT013`**. Generated:

```typescript
export interface Q3ProbeOverloadHandle {
    toJSON(): MarshalledHandle;
    q3ProbeWithRepo(url: string): Promise<string>;      // ← the handle overload is simply gone
}
```

So an overload is worse than a colliding name: stage 0's collision at least emitted a warning, and
this emits nothing. A design that overloads an exported instance method loses one overload with no
signal at all until someone notices the method missing from the SDK.

## 8 — An explicit id plus `MethodName` rescues the overload

```csharp
public string Q3ProbeWithRepo(string url) => url;

[AspireExport("q3ProbeWithRepoShared", MethodName = "q3ProbeWithRepoShared")]
public string Q3ProbeWithRepo(Q3ProbeRepoHandle repository) => "handle";
```

Both project, and both type-check at the call site:

```typescript
export interface Q3ProbeOverloadHandle {
    toJSON(): MarshalledHandle;
    q3ProbeWithRepo(url: string): Promise<string>;
    q3ProbeWithRepoShared(repository: Awaitable<Q3ProbeRepoHandle>): Promise<string>;
}
```

`MethodName` is doing real work here, unlike in stage 0's finding 5 where it was the wrong tool: the
two ids are already distinct once one is explicit, and `MethodName` is what stops the *generated
method name* from colliding on the interface. Both are needed — the id to separate the capabilities,
`MethodName` to separate what the consumer sees.

## 9 — An explicit id is namespace-scoped, so it gives up receiver qualification

Worth knowing before reaching for one. The rescued overload's capability id is:

```
'Aspire.Hosting.ServiceSources.Catalog/q3ProbeWithRepoShared'
```

— namespace-qualified, with **no receiver**, unlike the implicit
`…Catalog/Q3ProbeOverloadHandle.q3ProbeWithRepo` beside it. So an explicit id trades a
receiver-scoped name for a namespace-wide one and can therefore collide with an explicit id on a
different receiver.

**Guidance: do not reach for an explicit id unless disambiguating an overload.** Implicit ids are
already safe across receivers (finding 1); an explicit one opts out of that protection. This is also
the sharpest form of why Stage 1's `addServiceToCatalog` is a mistake rather than merely redundant —
it moved a safe receiver-scoped id into the namespace-wide pool for no benefit.

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
| 6. An exported handle as a **parameter** | **Clean**, and arrives as `Awaitable<T>` — no `await` required |
| 7. An **overload pair** on one receiver | **Silently loses one, with no warning at all** |
| 8. Overload rescued by explicit id **plus** `MethodName` | **Clean** — both needed, one for the id, one for the name |
| 9. What an explicit id costs | It is **namespace-scoped**, giving up receiver qualification |

**Open question 3 is answered — inherited methods project, and a split builder type is cheap.** But
shapes 6 and 7, probed afterwards in response to the ownership-inversion review, matter more for what
the design should actually do:

- **Shape 6 removes the reason to invert ownership.** A handle travels as an argument, so
  `catalog.AddService("orders").WithRepository(monorepo)` works, and every service stays declared on
  the catalog. That is the shape the review asked for, and it makes shapes 2, 3 and 5 — the split
  builder types and the generic base — **unnecessary**: with one builder type there is nothing to
  split, and the "grouped service also names its own URL" case collapses into the additive
  `RequireUnset` error the design already has for every other block.
- **Shape 7 is the one to be careful about.** The natural spelling of that API is a C# overload, and
  an overload loses one half in silence. Shape 8 is the fix, and shape 9 says what it costs.

**Recommendations:**

1. **Keep services on the catalog and pass the repository handle** — `WithRepository(monorepo)`,
   per shape 6. Drop `AddService` from the repository handle, and with it the split builder types
   and the generic base: they were solving a problem the inverted shape created.
2. **Spell the handle form as a C# overload with an explicit ATS id**:
   `[AspireExport("withSharedRepository", MethodName = "withSharedRepository")]` on
   `WithRepository(RepositoryBuilder)`, per shapes 7 and 8. Without both attributes' arguments it
   disappears from the SDK with no diagnostic.
3. **Add an export-surface assertion for it.** Shape 7 fails silently, so the only guard against a
   future refactor dropping the overload is a test that reads the generated interface — nothing in
   C# will complain.
4. **Do not use an explicit id anywhere else**, per shape 9: implicit ids are receiver-scoped and
   safe, explicit ones are namespace-wide and are not.
5. **Separately, as a #134 correction: revert Stage 1's `addServiceToCatalog` to plain
   `AddService`.** It is unreleased, one sample calls it
   (`DemoAppHostTypeScriptCodeCatalog/apphost.mts`), and by shape 9 it actively traded a safe id for
   a namespace-wide one to dodge a collision that cannot happen.
6. **Add a note to the stage-0 findings** recording that its finding 5 is specific to *extension*
   methods, so the next reader does not re-apply it to an instance method and invent another id.
