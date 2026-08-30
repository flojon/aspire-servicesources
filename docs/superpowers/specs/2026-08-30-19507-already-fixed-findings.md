# The 13.6.0 CLI requirement is stale — measured findings

**Status:** Finding, for [issue #88](https://github.com/flojon/aspire-servicesources/issues/88)
**Date:** 2026-08-30
**Measured against:** Aspire CLI **13.5.3** (the newest release) and **13.5.1** (the installed CLI),
`Aspire.Hosting` at the repo floor 13.5.2 / 13.5.1 respectively.

## Verdict

**`samples/DemoAppHostTypeScript` type-checks clean and runs end to end on released Aspire CLI
13.5.3.** The "Requires Aspire CLI 13.6.0+" caveat in the README, in #88, and in the 2026-08-29
Discord announcement is **no longer true**.

No code change is needed. #88 is unblocked today, against a CLI pinned to 13.5.3 or newer.

This was measured, not reasoned about: strict `tsc --noEmit` over the generated SDK, plus a real
`aspire run` with the injected environment read back off the running container.

## What actually happened

[microsoft/aspire#19507](https://github.com/microsoft/aspire/issues/19507) is real, and the README's
description of it is accurate: the TypeScript code generator does not emit the `*Promise` /
`*PromiseImpl` wrapper pair for a bare Aspire interface, so a method returning
`IResourceBuilder<IResourceWithServiceDiscovery>` references an undeclared
`ResourceWithServiceDiscoveryPromise`.

What the README misses is **what makes the generator emit that pair**. It is not the return type.
It is the interface appearing as an extension-method **receiver**:

```csharp
public static IResourceBuilder<IResourceWithServiceDiscovery> WithServiceEnvironment(
    this IResourceBuilder<IResourceWithServiceDiscovery> service, ...)
//       ^^^^ this receiver is what materialises ResourceWithServiceDiscoveryPromise
```

The eight `ServiceConfigurationExports` shims added by **PR #62** (`a39a03e`, 2026-08-23) all
declare exactly that receiver. They were added to solve #53, and they carry the wrapper pair for
`AddService` as a side effect.

### Ablation, on the real package

Removing only the `[AspireExport]` attribute from those eight shims — changing nothing else, and
leaving `AddService` untouched — reproduces the README's failure exactly, on CLI 13.5.3:

```
.aspire/modules/aspire.mts(10370,31): error TS2552: Cannot find name 'ResourceWithServiceDiscoveryPromise'.
... 6 errors
```

Restoring the attributes returns it to zero. So the eight shims are precisely what carries the
wrapper pair for the whole surface, `AddService` included.

### An unresolved discrepancy in the history

PR #57 (`afa9ffd`, 2026-08-27) merged **after** #62 and records "regenerating the same sample with
released 13.5.1 still reproduces all six TS2552 errors" — with the shims already present for four
days. That contradicts the measurement here, and this finding does not explain it. Candidates not
run down: a stale generator under `.aspire/` (the hazard #57 itself documents as
[microsoft/aspire#19603](https://github.com/microsoft/aspire/issues/19603)), or codegen reading a
package build that predated the shims. What is not in doubt is the current state, which was measured
on two CLIs with the ablation above isolating the cause.

## The controlled isolation

Two throwaway probe assemblies, one codegen run each, on CLI 13.5.1.

**Probe 1 — return type only, no bare-interface receiver.** Four shapes in one run:

| `[AspireExport]` method returns | Generated TypeScript | strict `tsc` |
| --- | --- | --- |
| `IResourceBuilder<IResourceWithServiceDiscovery>` | `ResourceWithServiceDiscoveryPromise` | ❌ undeclared — 6 × TS2552 |
| `IResourceBuilder<ConcreteClass>`, **no attribute** | `ProbePlainResourcePromise` | ✅ declared |
| `IResourceBuilder<ConcreteClass>` + `[AspireExport]` on the class | `ProbeHandleResourcePromise` | ✅ declared |
| `IResourceBuilder<IInterface>` + **`[AspireExport]` on the interface** | `ProbeServiceHandlePromise` | ❌ undeclared — 6 × TS2552 |

**Probe 2 — the same bare-interface return, plus one shim whose receiver is the bare interface.**
`tsc`: **clean, zero errors.** The wrapper pair is emitted, and the shim's return type is
*specialised to the concrete receiver* rather than to the interface:

```ts
// on ProbeSvcResourcePromise — not ResourceWithServiceDiscoveryPromise
withProbeEnvViaInterface(name: string, value: string): ProbeSvcResourcePromise;
```

The only variable between the two probes is the presence of a bare-interface receiver. That is the
cause.

## The real sample

| CLI | `AspireVersion` | `aspire restore` | `tsc --noEmit` | `aspire run` |
| --- | --- | --- | --- | --- |
| 13.5.1 (installed) | pinned to 13.5.1 to dodge NU1605 | clean | **0 errors** | containers up |
| **13.5.3 (newest release)** | **repo default (13.5.2 floor)** | clean | **0 errors** | containers up |

On 13.5.1 the repo's 13.5.2 floor trips `NU1605: Detected package downgrade: Aspire.Hosting from
13.5.2 to 13.5.1`, because the CLI pins its own version for the generated host project. That is a
*floor* problem, not a codegen problem, and it disappears on 13.5.3, which pins 13.5.3.

Runtime evidence, read off the running `payments` container on 13.5.3:

```
DEMO_INJECTED_BY_APPHOST=true                                  # withServiceEnvironment() — this package's ATS shim
services__inventory__http__0=http://inventory.dev.internal:80  # withServiceReference() — real service discovery
```

So the guest AppHost resolved a service, configured it through an ATS shim, and Aspire's own
service-discovery wiring reached the consuming container — the whole loop, on a released CLI.

## Corrections this forces

- **README** — the "Requires Aspire CLI 13.6.0+" block and the note on the TypeScript sample are
  wrong. The floor for the sample is the repo's own Aspire floor, 13.5.2, and what that needs is a
  CLI that pins 13.5.2 or newer, i.e. **13.5.3+**.
- **#88** — not blocked. It can land now against a CLI pinned to 13.5.3. Its own proposed shape
  (a `ci.yml` job doing `npm ci` + `aspire restore` + `tsc --noEmit`) works as written.
- **#134** — the claim that a code-authored catalog would have "wider CLI reach than the
  `AddService` shipped today" is **no longer a differentiator**: both reach 13.5.3. That argument
  should be struck from #134's motivation, which still stands on the alignment-with-Aspire grounds.
- **Discord** — the 2026-08-29 announcement's "needs CLI 13.6" caveat is stale.

## The handle-type hypothesis, specifically

The proposal was to apply #71's handle-type technique — `[AspireExport]` on the type — to
`AddService`'s return, to make the generator emit a declared wrapper.

**That specific mechanism does not work**, and probe 1 measures why: an `[AspireExport]`-annotated
*interface* used as the `T` in `IResourceBuilder<T>` fails identically to the bare one, 6 × TS2552.
`[AspireExport]` on the type is not what produces the wrapper. What produces it is `T` being a
**concrete class**, which needs no attribute at all.

#71's shape 2b — an annotated interface that *did* project — is not a counter-example: there the
handle was a lambda parameter and a direct return, not the `T` inside `IResourceBuilder<T>`.

It is also moot, and would have been costly. Making `AddService` return a concrete class is blocked
by two findings of the 2026-08-22 design that still hold: `local`/dotnet must return Aspire's own
`ProjectResource` for `WithProjectDefaults` (finding 5), and satellite kinds return Aspire's own
types such as `JavaScriptAppResource` (finding 7). No package-owned concrete class can be a base of
those, and `Resource` — the only common concrete ancestor — does not implement
`IResourceWithServiceDiscovery`, so returning it would break `WithReference` for every C# consumer.
The only shape that satisfies all of it is a facade, which the 2026-08-22 design deleted on purpose.

So: right instinct that the mechanics were worth re-measuring, wrong mechanism — and the problem it
was aimed at had already been solved by a change made for an unrelated reason.

## Reproducing

```bash
cd samples/DemoAppHostTypeScript
npm ci
rm -rf .aspire                      # microsoft/aspire#19603 — stale generator across CLI builds
aspire restore                      # with a CLI >= 13.5.3
npx tsc --noEmit -p tsconfig.apphost.json
```

A CLI older than the repo's Aspire floor fails at `restore` with NU1605 before codegen runs; that is
the floor, not #19507. `AspireVersion=<cli version>` overrides it for a one-off check.
