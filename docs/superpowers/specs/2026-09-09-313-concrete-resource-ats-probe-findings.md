# Can `AddService` return a concrete resource type? — measured findings

**Status:** Finding, for [issue #313](https://github.com/flojon/aspire-servicesources/issues/313)
**Date:** 2026-09-09
**Base commit:** `e1496f8`
**Measured against:** Aspire CLI **13.5.3** (the version `📘 typescript export surface` pins),
`Aspire.Hosting` at the repo floor **13.5.2**, `samples/DemoAppHostTypeScript`, strict
`npx tsc --noEmit -p tsconfig.apphost.json` over the generated SDK plus the AppHost.

## Verdict

**Yes.** An `[AspireExport]` method returning `IResourceBuilder<TConcrete>`, where `TConcrete` is a
public class declared in this package, generates a complete TypeScript binding — the method, the
handle interface, and the `*Promise` / `*PromiseImpl` wrapper pair. The handle carries **Aspire's
own vocabulary**, not a stub: `withEnvironment`, `withReference`, `waitFor`, `waitForCompletion`,
`withArgs`, `withHttpEndpoint`, `withHttpsEndpoint`, `getEndpoint`. A consumer's
`withReference(service)` and `waitFor(service)` type-check against it.

So #313's gating question is answered in the affirmative, and the three consequences it lists —
`Configure<T>`, the ten `WithService*` shims, `As<T>()` — are **debt, not permanent trade-offs**.

**The blocker recorded in the code is real but misattributed.** It is not "custom" or "narrow" that
the generator refuses. It is **interfaces**. A concrete class is exactly the shape that fixes it.

## What was measured

Five shapes added to the real package, one `aspire restore` apiece with `.aspire/` deleted first
(microsoft/aspire#19603), then a strict typecheck.

| # | C# shape | Handle members | Typechecks? |
|---|---|---|---|
| A | `IResourceBuilder<SpikeExportedResource>` — `public sealed class … : ContainerResource, IResourceWithServiceDiscovery`, `[AspireExport]` | **102** | ✅ |
| B | The same class **without** `[AspireExport]` | **101** | ✅ |
| C | `IResourceBuilder<SpikeFacadeBareResource>` — `: Resource, IResourceWithServiceDiscovery` | **61** | ✅ |
| D | `IResourceBuilder<SpikeFacadeRichResource>` — `: Resource` + 4 capability interfaces | **76** | ✅ |
| E | `IResourceBuilder<ISpikeCustomResource>` — package-defined **interface**, unannotated | **1** (`toJSON`) | ❌ TS2552 |
| F | The same interface **with** `[AspireExport(ExposeMethods = true)]` | **1** (`toJSON`) | ❌ TS2552 |

A `[AspireExport]` extension method whose receiver is the concrete builder
(`this IResourceBuilder<SpikeExportedResource>`) also projects, landing on the handle as
`withSpikeThing(value: string): SpikeExportedResourcePromise`. That is the mechanism the ten
`WithService*` shims would no longer need.

### The call sites, verified rather than inferred

Appended to `samples/DemoAppHostTypeScript/apphost.mts`, `tsc` exit 0, no diagnostics:

```typescript
// Aspire's own vocabulary, directly on the handle — no withService* infix, no Configure<T>.
const spike = await builder
  .addSpikeExported('spike')
  .withEnvironment('DEMO', 'true')
  .withSpikeThing('hello')
  .withHttpsEndpoint();

// The consumer path that has to keep working.
await builder
  .addExecutable('spike-probe', process.execPath, '.', ['-e', 'console.log(1)'])
  .withReference(spike)
  .waitFor(spike);
```

## Correction to what the code asserts

`Sources/ResolvedService.cs:9-16` and `ServiceSourcesBuilderExtensions.cs:76-81` both state that the
generator "emits nothing at all for an exported method returning a custom interface", and conclude
that the bare `IResourceBuilder<IResourceWithServiceDiscovery>` return is load bearing.

The *observation* reproduces (shapes E and F). The *generalisation* does not:

- **`[AspireExport]` on the interface does not help** (shape F is identical to shape E). This also
  narrows `2026-08-30-typed-catalog-ats-findings.md`, whose row 3 attributed the failure to the
  type being unannotated — true for a standalone builder interface, false for a resource interface
  inside `IResourceBuilder<T>`.
- **"Emits nothing" is not quite what happens.** The method binding *is* emitted; what is missing is
  the `*Promise` / `*PromiseImpl` pair, so the generated SDK references types it never declared:

  ```
  .aspire/modules/aspire.mts(11458,40): error TS2552: Cannot find name 'SpikeCustomResourcePromise'.
  ```

  That is [microsoft/aspire#19507](https://github.com/microsoft/aspire/issues/19507) — the same
  failure `2026-08-30-19507-already-fixed-findings.md` measured, and today's `AddService` only
  escapes it because the ten shims declare the bare interface as an extension-method *receiver*,
  which materialises the pair as a side effect.
- **The deciding factor is concrete class vs interface, and only that.** Shape B shows a package
  resource type needs no attribute at all: an unannotated concrete class got 101 members against the
  annotated one's 102, and the single difference was the probe's own extension method.

So the constraint the current signature was chosen to satisfy is one a concrete return type does not
have. Returning `IResourceBuilder<ServiceResource>` is not a risk to the TypeScript AppHost — it is
strictly better shaped for it than what ships today.

## What the C# facade would have to declare

Shapes C and D price the choice of base class, which is the part #313 correctly flags as "not a
one-line change". A single `ServiceResource` cannot derive from `ContainerResource`,
`ExecutableResource` and `ProjectResource` at once, so it would derive from `Resource` — and a bare
`Resource` does **not** get the vocabulary the issue is asking for.

Declaring four capability interfaces buys back exactly the 15 that matter:

```
waitFor  waitForCompletion  waitForStart  withArgs  withArgsCallback  withEnvironment
withEnvironmentCallback  withReference  withReferenceEnvironment  withOtlpExporter
withCertificateTrustScope  withDeveloperCertificateTrust  withHttpsCertificateConfiguration
withHttpsDeveloperCertificate  withoutHttpsCertificate
```

— from `IResourceWithEnvironment`, `IResourceWithArgs`, `IResourceWithEndpoints` and
`IResourceWithWaitSupport`. Endpoint naming (`withHttpEndpoint`, `withHttpsEndpoint`, `getEndpoint`,
`withEndpoint`, `withUrls`) is already present on a bare `Resource`, since those extensions are
constrained to `IResource`.

What a `Resource`-derived facade gives up is the container-only vocabulary — `withImage`,
`withBindMount`, `withVolume`, `withEntrypoint`, `withDockerfile`, `withLifetime` and 20 others.
That is the honest cost, and it is arguably correct: those are properties of *how a service resolved*,
which is precisely what this package exists to abstract over.

## What this spike did **not** answer

The ATS question is settled; the C#-side modelling question is not, and it is the harder half:

1. **How one `ServiceResource` sits above four different real resources.** Every source today
   returns the resource Aspire actually runs (`ServiceContainerResource`, `ServiceExecutableResource`,
   `ServiceUrlResource`, and Aspire's own `ProjectResource` for `local`). A `Resource`-derived facade
   is not any of those, so either it is a second resource in the model that delegates, or the sources
   converge on one type. Neither was probed here.
2. **Whether `WithReference` still resolves at runtime.** Only the typecheck was run — no
   `aspire run`. The generated binding compiling is not the same claim as DCP wiring
   `services__<name>__http__0` from a facade.
3. **`Configure`'s skip-with-warning behaviour for `url` and `kubernetes`.** Real behaviour, and
   whatever replaces `Configure<T>` still needs somewhere to keep it.

## Recommendation

1. **Un-gate #313 and #314.** The feasibility question that blocked both is answered yes.
2. **Fix the two code comments** in `ResolvedService.cs` and `ServiceSourcesBuilderExtensions.cs`
   before the next reader treats the bare interface as load bearing. It is load bearing *today* —
   because the shims carry the wrapper pair — but not for the stated reason, and not under a
   concrete return type. Cheap and independent of any redesign.
3. **Design the facade against shape D**, and settle open question 1 above before writing code — it,
   not ATS, is what makes this expensive.
4. **Keep the shims exported until the concrete type lands.** Per the 19507 finding, removing
   `[AspireExport]` from them while `AddService` still returns the bare interface reintroduces the
   TS2552 failures immediately.

## Reproducing

```bash
dotnet tool install --tool-path /tmp/aspire-cli aspire.cli --version 13.5.3
export PATH=/tmp/aspire-cli:$PATH
cd samples/DemoAppHostTypeScript
npm ci
rm -rf .aspire                                   # microsoft/aspire#19603 — the whole directory
aspire restore --non-interactive --nologo        # exits 0 even when the SDK it wrote is invalid
npx tsc --noEmit -p tsconfig.apphost.json        # the step that actually gates the surface
```

Member counts come from the generated `.aspire/modules/aspire.mts`, reading the members of each
`export interface <Handle> { … }` block.
