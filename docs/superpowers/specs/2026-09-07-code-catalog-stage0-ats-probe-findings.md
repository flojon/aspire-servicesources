# Code Catalog Stage 0 — ATS Probe Findings

**Date:** 2026-09-07
**Resolves:** the Stage 0 row of the Staging table in
[`2026-09-05-servicesources-code-catalog-design.md`](2026-09-05-servicesources-code-catalog-design.md)
— finding 7's table of five shapes that design depends on but had never been measured.
**Method:** measured, not reasoned about, following the same standard as
[`2026-08-30-19507-already-fixed-findings.md`](2026-08-30-19507-already-fixed-findings.md). A
throwaway probe file (`src/Aspire.Hosting.ServiceSources/Catalog/Stage0AtsProbe.cs`) declared one
`[AspireExport]` member per shape; `samples/DemoAppHostTypeScript` (whose `aspire.config.json`
already points at `../../src`) was restored against it with Aspire CLI 13.5.3 (the repo's
`$(AspireVersion)` floor is 13.5.2; the CLI on `PATH` in this environment is 13.5.1 and fails
`aspire restore` with NU1605, so 13.5.3 was installed to a job-scoped tool path for this
measurement) and strict-`tsc`'d against `tsconfig.apphost.json` — the same command
`ci.yml`'s `📘 typescript export surface` job runs. The probe file and the temporary calls added to
`apphost.mts` were reverted after measurement; nothing here ships. No production code changed.

## Result: 4 of 5 shapes cross cleanly as designed; 1 needed a different fix than expected

| # | Shape | Result |
| --- | --- | --- |
| 1 | Optional/named parameters, and `string[]?` | **Clean.** |
| 2 | Extension method whose receiver is a package-owned exported class | **Clean.** |
| 3 | A lambda nested inside a lambda | **Clean.** |
| 4 | An enum as a parameter | **Clean.** |
| 5 | The generated name `addService` on two receivers | **Collides — but the design's own fallback name (`MethodName`) does not fix it.** An explicit capability `id` does. |

## 1 — Optional/named parameters, and `string[]?`

Probed:

```csharp
[AspireExport]
public static Stage0ProbeCatalogBuilder Stage0ProbeWithRepository(
    this Stage0ProbeCatalogBuilder catalog, string url, string? defaultRef = null) => catalog;

[AspireExport]
public static Stage0ProbeCatalogBuilder Stage0ProbeWithPrepare(
    this Stage0ProbeCatalogBuilder catalog,
    string[] command,
    string[]? windowsCommand = null,
    Stage0ProbeMode mode = Stage0ProbeMode.Once) => catalog;
```

Generated TypeScript collapses the trailing optional parameters into an options bag, one interface
per method, which is exactly the ergonomic shape `WithRepository(url, defaultRef:)` and
`WithPrepare(cmd, windowsCommand:, mode:)` need:

```typescript
export interface Stage0ProbeWithRepositoryOptions {
    defaultRef?: string;
}
export interface Stage0ProbeWithPrepareOptions {
    windowsCommand?: string[];
    mode?: Stage0ProbeMode;
}
stage0ProbeWithRepository(url: string, options?: Stage0ProbeWithRepositoryOptions): Stage0ProbeCatalogBuilderPromise;
stage0ProbeWithPrepare(command: string[], options?: Stage0ProbeWithPrepareOptions): Stage0ProbeCatalogBuilderPromise;
```

`string[]?` projects as `windowsCommand?: string[]` — the nullable array survives, no fallback
(never-null empty array) needed. Called with realistic syntax
(`catalog.stage0ProbeWithRepository('https://github.com/example/probe', { defaultRef: 'main' })`,
`.stage0ProbeWithPrepare(['./prepare.sh'], { windowsCommand: [...], mode: Stage0ProbeMode.Once })`) —
`0 errors`.

## 2 — Extension method whose receiver is a package-owned exported class

Probed:

```csharp
[AspireExport(ExposeMethods = true)]
public sealed class Stage0ProbeCatalogBuilder
{
}

public static class Stage0AtsProbeExtensions
{
    [AspireExport]
    public static Stage0ProbeCatalogBuilder Stage0ProbeWithRepository(
        this Stage0ProbeCatalogBuilder catalog, string url, string? defaultRef = null) => catalog;
    // … Stage0ProbeWithPrepare, Stage0ProbeAsJava, Stage0ProbeWithMode, same shape.
}
```

`Stage0ProbeCatalogBuilder` has no members of its own — every method it has comes from extension
methods declared elsewhere (`Stage0AtsProbeExtensions`), the same shape `AsJava(this
ServiceDefinitionBuilder, …)` needs. All four extension methods targeting it project onto the
generated `Stage0ProbeCatalogBuilderImpl` class as instance methods — confirmed by reading the
generated class body directly (`.aspire/modules/aspire.mts:9762-9834`), not merely by a clean `tsc` —
and are exercised at the call site shown under Shapes 1, 3 and 4 (`catalog.stage0ProbeWithRepository(…)`
and the chain that follows it). Clean.

## 3 — A lambda nested inside a lambda

Probed:

```csharp
[AspireExport]
public static Stage0ProbeCatalogBuilder Stage0ProbeAsJava(
    this Stage0ProbeCatalogBuilder catalog, Action<Stage0ProbeJavaOptionsBuilder> configure) => catalog;

[AspireExport(RunSyncOnBackgroundThread = true)]
public static IDistributedApplicationBuilder Stage0ProbeAddServiceCatalog(
    this IDistributedApplicationBuilder builder, Action<Stage0ProbeCatalogBuilder> configure) => builder;
```

Called as the design's own sketch shapes it:

```typescript
await builder.stage0ProbeAddServiceCatalog(async (catalog) => {
  const withPrepare = /* … */;
  await withPrepare.stage0ProbeAsJava(async (options) => {
    await options.mavenGoal('spring-boot:run');
  });
});
```

Second-level guest-to-host re-entrancy, under the outer `RunSyncOnBackgroundThread = true` — `0
errors`. Clean.

## 4 — An enum as a parameter

Probed both as a required parameter and inside an optional-parameter bag:

```csharp
public enum Stage0ProbeMode { Once, Always }

[AspireExport]
public static Stage0ProbeCatalogBuilder Stage0ProbeWithMode(
    this Stage0ProbeCatalogBuilder catalog, Stage0ProbeMode mode) => catalog;
```

Generated as a proper TypeScript string enum:

```typescript
export enum Stage0ProbeMode {
    Once = "Once",
    Always = "Always",
}
stage0ProbeWithMode(mode: Stage0ProbeMode): Stage0ProbeCatalogBuilderPromise;
```

Called with `withPrepare.stage0ProbeWithMode(Stage0ProbeMode.Always)` and, inside
`Stage0ProbeWithPrepareOptions`, `{ mode: Stage0ProbeMode.Once }` — both `0 errors`. Clean, in both
positions the design needs it (`WithPrepare(…, mode:)` and a bare `WithKind`-adjacent parameter).

## 5 — The generated name `addService` on two receivers — the one that failed

> **Correction (#309):** the probe below declares `AddService` as an **extension** method
> (`this Stage0ProbeCatalogBuilder catalog`), which is exported as a free function keyed
> `{AssemblyName}/{camelCaseMethodName}` — flat, with no receiver qualification, so it genuinely
> collides with `ServiceSourcesBuilderExtensions.AddService`. Stage 1 then applied this finding's
> conclusion (give it an explicit capability `id`) to `ServiceCatalogBuilder.AddService`, which is
> an **instance** method exposed via `ServiceCatalogBuilder`'s own `[AspireExport(ExposeMethods =
> true)]` — those are always receiver-qualified as `{TypeName}.{camelCaseMethodName}`, explicit
> attribute or not, so the collision this finding describes cannot occur for it. Do not re-apply
> this finding to an instance method; see #309 and
> `docs/superpowers/specs/2026-09-08-repository-handle-q3-ats-probe-findings.md`.

Probed, first attempt, matching the design's stated shape exactly (`ServiceCatalogBuilder.AddService`
alongside the shipped `ServiceSourcesBuilderExtensions.AddService`,
`src/Aspire.Hosting.ServiceSources/ServiceSourcesBuilderExtensions.cs:84`):

```csharp
[AspireExport]
public static Stage0ProbeCatalogBuilder AddService(
    this Stage0ProbeCatalogBuilder catalog, string name) => catalog;
```

**This collides, and fails silently rather than loudly.** Three independent signals, in order of how
early each would actually be noticed:

1. **Build time** — a warning, not an error (so `-warnaserror` in CI's `build` job would not catch it;
   the `typecheck-typescript` job's `tsc` is what actually would, downstream):

   ```
   ASPIREEXPORT013(56,68) warning: Polyglot capability ID 'Aspire.Hosting.ServiceSources/addService'
   is defined by multiple exports in this assembly: Aspire.Hosting.ServiceSources.Catalog.Stage0AtsProbeExtensions…
   ```

2. **Generated SDK** — the second export is dropped entirely, not merged or aliased. Reading
   `Stage0ProbeCatalogBuilderImpl`'s generated class body directly
   (`.aspire/modules/aspire.mts:9762-9834`) shows exactly the other four probed methods and no
   `addService`/`stage0ProbeAddService` of any spelling — the export simply isn't there. The
   surviving `addService` in the generated file is the pre-existing one on
   `IDistributedApplicationBuilder`, unchanged.
3. **Call site** — attempting `withMode.addService('probe-nested-service')` (`withMode` typed as
   `Stage0ProbeCatalogBuilder`) fails strict `tsc`:

   ```
   apphost.mts(83,12): error TS2339: Property 'addService' does not exist on type 'Stage0ProbeCatalogBuilder'.
   ```

### The design's stated fallback (`MethodName`) does not fix it

Finding 7 named an explicit `MethodName` as the fix. Measured:

```csharp
[AspireExport(MethodName = "stage0ProbeAddServiceToCatalog")]
public static Stage0ProbeCatalogBuilder AddService(
    this Stage0ProbeCatalogBuilder catalog, string name) => catalog;
```

Rebuilding reproduced the **identical** `ASPIREEXPORT013` warning, unchanged. `MethodName`'s own XML
doc is explicit about why, once read carefully: it "renames the generated SDK's method name;" the
capability ID two exports collide on is derived from the C# method name itself (or an explicit `id`),
never from `MethodName`. `MethodName` is for disambiguating what a *consumer* sees when two IDs
happen to already be distinct; it does nothing for the ID collision itself.

### What actually fixes it: an explicit capability `id`

```csharp
[AspireExport("stage0ProbeAddServiceToCatalog")]
public static Stage0ProbeCatalogBuilder AddService(
    this Stage0ProbeCatalogBuilder catalog, string name) => catalog;
```

Rebuild: `0 errors, 0 warnings` — the `ASPIREEXPORT013` warning is gone entirely. The generated SDK
now carries `stage0ProbeAddServiceToCatalog(name: string): Stage0ProbeCatalogBuilderPromise` on
`Stage0ProbeCatalogBuilder`, invoking capability id `Aspire.Hosting.ServiceSources/stage0ProbeAddServiceToCatalog`
— distinct from `IDistributedApplicationBuilder.AddService`'s
`Aspire.Hosting.ServiceSources/addService`. Called as
`withMode.stage0ProbeAddServiceToCatalog('probe-nested-service')` — `0 errors`.

## Verdict for Stage 1

Four of the five shapes the design (`WithRepository(url, defaultRef:)`, `WithContainer(image, port:,
defaultTag:)`, `WithPrepare(cmd, windowsCommand:, mode:)`, the `AsJava`/`AsJavaScript` nested-lambda
handles, `WithPrepare`'s `mode: PrepareMode.Once`) depend on need no design change: they cross ATS
exactly as specified.

**One signature in the design must change before Stage 1 freezes it.**
`ServiceCatalogBuilder.AddService(string)` — named identically to the shipped
`ServiceSourcesBuilderExtensions.AddService(this IDistributedApplicationBuilder, [ResourceName]
string)` — collides on generated capability ID. Left as specified, this would not fail Stage 1's own
build (a warning, not an error) or its `dotnet test` suite (nothing there exercises generated
TypeScript); it would only surface downstream, as either a silently-missing method in the guest-language
SDK or — worse, if the collision resolved the other way — the catalog's `AddService` silently
invoking the wrong capability at runtime. `ci.yml`'s `📘 typescript export surface` job would catch it
before merge, but only because that job exists; the failure mode is exactly the kind finding 7 was
written to catch before a signature freezes; the design's own fallback for this row (`MethodName`)
should be corrected to say **explicit capability `id`** instead. Stage 1's implementation plan should
give `ServiceCatalogBuilder.AddService` (or whatever name is chosen for the catalog-scoped entry
point) an explicit `[AspireExport("…")]` id distinct from `addService`, e.g.
`"addServiceToCatalog"` or similar — the exact name is Stage 1's call, not this stage's.
