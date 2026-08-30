# Spike: can a callback parameter cross the ATS boundary?

**Status:** Findings — throwaway spike, code on branch `worktree-ats-callback-spike` only
**Date:** 2026-08-30
**Question:** The [backing-service design](2026-08-15-servicesources-database-source-design.md)
states, under Guest-language exports, that `AddBackingService`'s `local` parameter "is a `Func<>`
returning a resource builder, and **a callback cannot cross the ATS boundary at all**." That claim
was never prototyped, and it is the doc's largest open item. Is it true?

## Answer

**No. The claim is false.** A callback parameter — including the exact
`Func<IResourceBuilder<IResourceWithConnectionString>>` shape the design proposes — exports through
ATS, generates real TypeScript, and works end-to-end at run time.

The real constraint is narrower and has a one-attribute fix: **invoking the delegate synchronously
from the exported method deadlocks the JSON-RPC channel.** Aspire's own analyzer catches this at
build time and names the remedies.

## Evidence

Environment: Aspire CLI 13.5.1, .NET 10.0.400, Node 24.0.2, Linux. Run against
`samples/DemoAppHostTypeScript` with `AspireVersion=13.5.1` (the repo floor is 13.5.2, above the
locally installed CLI — unrelated to the question).

### 1. ATS models callbacks as a first-class category

`~/.aspire/bin/Aspire.TypeSystem.xml`:

- `AtsTypeCategory.Callback` — "Callback types (delegates) that are registered and invoked by ID."
- `AtsParameterInfo.IsCallback` — "Callbacks are inferred from delegate types (Func, Action, custom
  delegates)."
- `AtsParameterInfo.CallbackParameters` / `.CallbackReturnType`, and a whole
  `AtsCallbackParameterInfo` type.

### 2. All three probe shapes compile and export

`src/Aspire.Hosting.ServiceSources/AtsCallbackProbe.cs` declares three `[AspireExport]` methods:
a zero-arg `Func<>` returning a handle (the design's exact shape), an `Action<>` taking a handle,
and a `Func<string>`. The build **succeeds**. The only diagnostic is ASPIREEXPORT010, once per
method:

> Exported builder method 'ProbeFuncReturnsHandle' directly or transitively invokes synchronous
> delegate parameter 'local'. Defer the callback, expose an async delegate, or set
> RunSyncOnBackgroundThread = true to avoid polyglot deadlocks.

The diagnostic presupposes callbacks cross the boundary; it is about *how* the delegate is invoked,
not whether it can be exported.

### 3. Codegen emits real TypeScript, marshalled by callback ID

From the generated `.aspire/modules/aspire.mts`:

```ts
probeFuncReturnsHandle(name: string, local: () => Promise<ResourceWithConnectionString>): ResourceWithConnectionStringPromise;
```

and its implementation:

```ts
const localId = registerCallback(async () => { return await local(); });
const rpcArgs: Record<string, unknown> = { builder: this._handle, name, local: localId };
const result = await this._client.invokeCapability<IResourceWithConnectionStringHandle>(
    'Aspire.Hosting.ServiceSources/probeFuncReturnsHandle', rpcArgs);
```

Aspire's own API sits directly above ours in the same generated interface and takes a callback the
same way — so this is a supported path, not an accident:

```ts
addHealthCheck(name: string, check: () => Promise<HealthCheckResult>): DistributedApplicationBuilderPromise;
```

### 4. Run A — synchronous invoke deadlocks

With plain `[AspireExport]`, `aspire run` never starts. The host log names it:

```
Capability Aspire.Hosting.ServiceSources/probeFuncReturnsHandle failed with ConnectionLostException:
The JSON-RPC connection with the remote party was lost before the request could complete.
   at Aspire.Hosting.RemoteHost.Ats.CapabilityDispatcher...
```

The guest never logged the callback firing. This is exactly the failure ASPIREEXPORT010 predicts.

### 5. Run B — `RunSyncOnBackgroundThread = true` fixes it

Changing one attribute to `[AspireExport(RunSyncOnBackgroundThread = true)]` — the only change
between runs — clears the ASPIREEXPORT010 warning and makes the AppHost start. Guest output:

```
[probe] calling probeFuncReturnsHandle
[probe] >>> guest callback INVOKED by host
[probe] >>> guest callback created inner resource, returning handle
[probe] probeFuncReturnsHandle RETURNED, handle = present
[probe] building + running
```

Timestamps put the whole round trip at ~54 ms. Note line 3 in particular: the guest callback called
**back into the host** (`builder.addConnectionString(...)`) while the host was blocked awaiting that
same callback, and the resulting handle round-tripped to the host as the callback's return value.
Reentrancy works. The dashboard came up and the app ran.

## Consequences for the backing-service design

- The "Guest-language exports" section's conclusion — that the `"local"` branch has no
  guest-language equivalent — does not hold. A TypeScript AppHost can write
  `await builder.addBackingService('orders-db', async () => builder.addPostgres('pg').addDatabase('orders'))`.
- Two of the doc's open questions shrink or disappear. The largest one (how guest languages declare
  a `"local"` backing service) is answered by the callback itself. The catalog-vs-local.json
  question no longer has "a declarative local spec has to live somewhere" pushing on it, so it can
  be decided on its own merits.
- One new design constraint replaces them: `AddBackingService` must not invoke `local` synchronously
  on the RPC thread. `RunSyncOnBackgroundThread = true` is the cheapest fix and is what this spike
  verified; deferring the invoke or exposing an async delegate are the other two the analyzer names.
  Whichever is chosen, ASPIREEXPORT010 enforces it at build time.
- `[AspireExportIgnore]` on `AddBackingService` is therefore unnecessary.

## Caveats

- Verified on Aspire 13.5.1 only. The repo floor is 13.5.2 and CI runs a version matrix; the probe
  should be re-run on the floor and the latest leg before the design depends on it.
- The probe returned a `ConnectionStringResource` created by `addConnectionString`, not a real
  `addPostgres(...).addDatabase(...)` — that needs the Postgres integration package in the sample.
  The marshalling is the same (both are handles), but the fuller shape is worth confirming.
- `RunSyncOnBackgroundThread = true` was verified to remove the deadlock; its interaction with the
  package's other exports was not examined.

## Reproducing

The probe methods are in `src/Aspire.Hosting.ServiceSources/AtsCallbackProbe.cs` and the probe
AppHost in `samples/DemoAppHostTypeScript/probe-apphost.mts.txt` (copy over `apphost.mts`). Both are
throwaway and must not merge to `main`.

```
dotnet build src/Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.csproj
cd samples/DemoAppHostTypeScript
AspireVersion=13.5.1 aspire restore
AspireVersion=13.5.1 aspire run --non-interactive
```
