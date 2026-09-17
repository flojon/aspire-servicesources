# Do guest-language endpoint callbacks bypass the Reachability gate? — measured findings

**Status:** Finding, for [issue #353](https://github.com/flojon/aspire-servicesources/issues/353)
**Date:** 2026-09-17
**Base commit:** `43baada` (`origin/main` when measuring; `d9271ba` landed during and changes comments only)
**Measured against:** Aspire CLI **13.5.3** (the version `📘 typescript export surface` pins),
`Aspire.Hosting` at the repo floor **13.5.2**, `samples/DemoAppHostTypeScript`, `ilspycmd 11.0.0.9375`,
and throwaway xUnit probes run on `net10.0` (written, run, then deleted — nothing below depends on a
file that still exists in the tree).

## Verdict

**Yes — the gap is real, and it is worse than the C# one.** Three ungated capabilities are projected
onto the `ServiceResource` handle in every guest language, and no amount of C# shadowing can close
them.

But the issue's framing is **half wrong in this package's favour**, which matters for the fix: #335's
shadows *do* protect the guest-language path for `withEndpoint`/`withHttpEndpoint`/`withHttpsEndpoint`.
The generated TypeScript binds those three to **this package's** capabilities, not Aspire's. It is only
the three `*Callback` names — which this package declares no member for — that fall through.

| Generated TS member on `ServiceResource` | Capability it invokes | Gated? |
|---|---|---|
| `withEndpoint` | `Aspire.Hosting.ServiceSources/withEndpoint` | ✅ this package's shadow |
| `withHttpEndpoint` | `Aspire.Hosting.ServiceSources/withHttpEndpoint` | ✅ this package's shadow |
| `withHttpsEndpoint` | `Aspire.Hosting.ServiceSources/withHttpsEndpoint` | ✅ this package's shadow |
| `withEndpointCallback` | `Aspire.Hosting/withEndpointCallback` | ❌ **ungated, both branches** |
| `withHttpEndpointCallback` | `Aspire.Hosting/withHttpEndpointCallback` | ❌ **ungated update branch** |
| `withHttpsEndpointCallback` | `Aspire.Hosting/withHttpsEndpointCallback` | ❌ **ungated update branch** |

Of the three fix candidates, only post-hoc detection survives contact with the constraints — §4
prototypes it and finds it viable. It also turns up a fourth ungated call surface,
`WithExternalHttpEndpoints`, which is unreported **from C# today** (§4.5).

## 1. Q1 — how a guest-language AppHost reaches these methods

Through **Aspire's own generated bindings**, directly on the handle `addService()` returns. Not
hypothetically: generated with the pinned CLI and read out of the emitted file.

```
aspire restore --non-interactive --nologo   # in samples/DemoAppHostTypeScript, after rm -rf .aspire
```

`.aspire/modules/aspire.mts` (44,408 lines) declares
`type ServiceResourceHandle = Handle<'Aspire.Hosting.ServiceSources/Aspire.Hosting.ServiceSources.ServiceResource'>`,
and `ServiceResourceImpl` carries all three callback members. Verbatim from the emitted output:

```typescript
private async _withEndpointCallbackInternal(endpointName: string, callback: (obj: EndpointUpdateContext) => Promise<void>, createIfNotExists?: boolean): Promise<ServiceResource> {
    const callbackId = registerCallback(async (objData: unknown) => { /* … */ });
    const rpcArgs: Record<string, unknown> = { builder: this._handle, endpointName, callback: callbackId };
    if (createIfNotExists !== undefined) rpcArgs.createIfNotExists = createIfNotExists;
    const result = await this._client.invokeCapability<ServiceResourceHandle>(
        'Aspire.Hosting/withEndpointCallback',
        rpcArgs
    );
```

Against the same handle, the non-callback methods name a different assembly — this is the half of the
issue's premise that turns out not to hold:

```typescript
    const result = await this._client.invokeCapability<ServiceResourceHandle>(
        'Aspire.Hosting.ServiceSources/withHttpsEndpoint',
        rpcArgs
    );
```

So ATS collapses a member-name collision on the handle in favour of the capability whose receiver is
the concrete `ServiceResource` — the same principle C# overload resolution applies, which is why #335's
fix carries over to guest languages for free for those three. It carries over for **only** those three,
because member-name shadowing needs a member of that name to exist, and this package declares no
`WithHttpsEndpointCallback`.

That an `internal` method projects at all is by design: `[AspireExport]` is read off assembly metadata
inside `Aspire.Hosting.dll`, where accessibility is not a barrier. All three carry
`[AspireExport(RunSyncOnBackgroundThread = true)]`.

## 2. Q2 — what a call through them actually does

Yes: it bypasses `Reachability`/`ServiceResourceBuilder.WithAnnotation` entirely and mutates for real,
with **no warning at all**.

Measured with throwaway xUnit probes reflectively invoking Aspire's internal generic closed over
`ServiceResource` — which is exactly what ATS capability dispatch does — against the same real
`UrlSource`/`KubernetesSource` fixtures `EndpointSkipGapRepro` uses:

| Probe | Result |
|---|---|
| `url` + `withHttpsEndpointCallback` setting `Port`/`TargetHost` | `Port` 443 → **9999**, `TargetHost` `orders.example.com` → **`attacker.internal`**, **warnings=0** |
| `url` + `withEndpointCallback('probe', …)` (name that does not exist) | endpoint **`probe:4242` added to the facade**, **warnings=0** |
| `kubernetes` + `withHttpsEndpointCallback` setting `Port`/`TargetPort` | 54321 → **9999** on both, **warnings=0** |

The same three calls' C# equivalents *are* gated today, which is the asymmetry:

```
service.WithEndpoint("https", a => a.Port = 9999);   // C#
service.WithEndpoint("probe", a => a.Port = 4242);
→ endpoints=[https:54321]  warnings=1  ("skipped 2 calls (WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint ×2)")
```

### 2.1 The kubernetes mutation reaches the real port-forward

Not just the facade. Probed directly: the facade and the real `ServiceExecutableResource` (the
`kubectl port-forward`) hold the **same** `EndpointAnnotation` instance — `same=True` — because
`ServiceResourceBuilder.WithAnnotation` deliberately adds one instance to both collections. So a
guest-language callback repointing `Port`/`TargetPort` repoints the actual running port-forward's
endpoint tuple. That is precisely the wrong-process failure mode `Reachability` exists to prevent.

### 2.2 Why the gate never sees it

Decompiled from the pinned floor. `WithWellKnownEndpointCallback` (which both
`WithHttpEndpointCallback` and `WithHttpsEndpointCallback` funnel through) creates the endpoint only
when it is missing, then updates:

```csharp
private static IResourceBuilder<T> WithWellKnownEndpointCallback<T>(this IResourceBuilder<T> builder,
    Action<EndpointUpdateContext> callback, string endpointName, bool createIfNotExists,
    Action<IResourceBuilder<T>, string> createEndpoint) where T : IResourceWithEndpoints
{
    if (createIfNotExists && !builder.Resource.Annotations.OfType<EndpointAnnotation>().Any(/* name match */))
    {
        createEndpoint(builder, endpointName);          // → Aspire's WithHttpsEndpoint → WithAnnotation → GATED
    }
    return builder.WithEndpoint(endpointName, endpoint => callback(new EndpointUpdateContext(endpoint)),
                                createIfNotExists: false);   // → update branch → NOT gated
}
```

For `url` and `kubernetes` the endpoint the source registered is *already there* (`UrlSource` adds one
named for the URL's scheme; `KubernetesSource` one named for its configured scheme), so the create is
skipped and every call lands on the update branch — which hands the caller the live shared
`EndpointAnnotation` with no gate and no ceiling on what it changes.

`WithEndpointCallback` is worse still: its add branch is `builder.Resource.Annotations.Add(...)`
directly, never `WithAnnotation`, so a brand-new endpoint name is ungated too. That is the same
underlying defect #352 records for reachable sources, seen from the reachability side.

`EndpointUpdateContext` exposes settable `Protocol`, `Port`, `TargetPort`, `UriScheme`, `TargetHost`,
`Transport`, `IsExternal`, `IsProxied`, `ExcludeReferenceEndpoint`, `TlsEnabled`.

## 3. Q3 — what is actually fixable

**Not by shadowing.** `EndpointUpdateContext` is `internal sealed` to `Aspire.Hosting.dll`:

```csharp
[AspireExport(ExposeProperties = true)]
internal sealed class EndpointUpdateContext(EndpointAnnotation endpointAnnotation)
```

A shadow has to project the same TypeScript shape to win the member, and that shape is
`(callback: (obj: EndpointUpdateContext) => Promise<void>, …)`. This package cannot name that
parameter type at compile time, so the `withHttpsEndpoint` trick has no counterpart here. This is a
hard wall, not an effort question.

**Not by intercepting annotations either.** `Resource.Annotations` is `virtual`, but its type
`ResourceAnnotationCollection` is `public sealed` — there is no subclass to override `Add` on. And the
update branch mutates properties on an existing `EndpointAnnotation`, where there is nothing to
intercept at all.

That leaves three candidates:

1. **Detect after the fact** — snapshot at resolve, compare at `BeforeStartEvent`. Investigated in
   full in §4 below: **viable, prototyped, and it covers strictly more than the three callbacks.**
2. **Document the limitation** for guest-language AppHost authors — cheap, honest, closes nothing.
3. **Ask upstream.** Routing the callback path's update branch through `WithAnnotation`, or making
   `EndpointUpdateContext` public, would each make this closable here. Worth filing against
   `microsoft/aspire` regardless of which of the above ships.

## 4. Post-hoc detection, investigated

**Verdict: viable.** A working prototype caught both mutation shapes, reverted them, and reported
them in the package's existing skip-warning wording — order-independently. What follows was measured,
not designed on paper; the prototype was a throwaway xUnit probe, run on `net10.0`, then deleted.

### 4.1 The prototype and what it did

Fingerprint every `EndpointAnnotation` the source registers at resolve time (`Name`, `Port`,
`TargetPort`, `UriScheme`, `TargetHost`, `Transport`, `IsExternal`, `IsExplicitlyProxied`,
`ExcludeReferenceEndpoint`, `TlsEnabled`), subscribe `BeforeStartEvent`, and on any difference:
restore the recorded values, drop endpoints that were not there before, and record a skip.

Driven with the same reflective guest-language calls as §2, against a `kubernetes`-sourced service:

```
detected=[changed:https,added:probe]  port 54321->9999->54321  endpoints=[https]  LOGGED=1
  Service 'orders': skipped WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint because its source is
  'kubernetes' — it resolves to a 'kubectl port-forward' in front of an already-running service, …
```

Both shapes caught, the port restored, the smuggled `probe` endpoint removed, and the message that
reached the log is the one this package already emits for a gated C# call. **The observable outcome
matches gating at the call site**, which is the bar — revert is possible, not just detection, because
every property `EndpointUpdateContext` can reach is settable from here too.

### 4.2 Both seams it needs already exist

- **Timing.** `BeforeStartEvent` runs before DCP starts anything (`ExecuteBeforeStartHooksAsync` is
  awaited before `_host.StartAsync`), and — checked, because the opposite is easy to assume from
  `ServiceStartupFailureNotices`' remark about its *report loop* — it fires under
  `IsPublishMode || IsRunMode`, so publish mode is covered too. All guest-language capability calls
  land before it: the AppHost script runs to completion before `builder.build().run()`.
- **Reporting.** `ServiceSourcesWarnings` already supports entries recorded *during*
  `BeforeStartEvent` — `ReporterFor` exists precisely so a caller in that position does not lay the
  inert-subscription trap, and `Flush` is documented report-once for exactly this. Nothing new is
  needed to report a late skip.

### 4.3 False positives: none by construction

Two independent reasons, both checked rather than assumed:

- Nothing in this package mutates an endpoint tuple between resolve and `BeforeStartEvent`. The only
  post-construction endpoint write anywhere in `src/` is `UrlSource` setting `AllocatedEndpoint` at
  resolve time, before the snapshot is taken. `KubectlPortForward` allocates its port during
  `Resolve`, not later.
- More fundamentally: the detector is only installed for out-of-band sources, and for those
  `Reachability.IsUnreachable(typeof(EndpointAnnotation), source)` is unconditionally true. So *any*
  endpoint-tuple change after resolve is by definition one that should have been skipped. There is no
  legitimate change for it to confuse itself with.

### 4.4 The one real trap, and its remedy

Subscription order. `ServiceSourcesWarnings`' flush handler subscribes on the first
`ServiceSourcesWarnings.For(builder)` call — which an *earlier, unrelated* service's skip may already
have triggered. With the flush handler subscribed first, the prototype measured:

```
detected=[changed:https]  TargetHost=orders.example.com  LOGGED=0
```

The mutation is reverted and **nothing is logged** — strictly worse than doing nothing, because the
author's call vanishes with no explanation. Calling `Flush(@event.Services)` immediately after
`AddSkip` fixes it: re-measured in both subscription orders, `LOGGED=1` either way. Any design must
treat this as a requirement, not a detail.

### 4.5 Why this beats more shadows: it closes a bug shadowing never will

`WithExternalHttpEndpoints` is a **public** Aspire method that walks existing annotations and sets
`item.IsExternal = true` directly — no `WithAnnotation`, so no gate. Measured on current `main`
against a `kubernetes` service, **from C#, not a guest language**:

```
service.WithExternalHttpEndpoints();
→ IsExternal False -> True,  warnings=0
```

That is a live, unreported bug outside #334/#335's scope, in the language those fixes were written
for — filed as #369. It is not a one-off: the shadow strategy has to name every Aspire method that mutates an
existing `EndpointAnnotation` in place, in both the public and the ATS-projected surface, and re-name
each new one Aspire ships. Post-hoc detection is keyed on the *state*, so its coverage does not scale
with that list — it catches `withExternalHttpEndpoints`, the three callbacks, and whatever comes next,
uniformly.

(`WithEndpointProxySupport`, the other suspect on the handle, goes through `WithAnnotation` and is
correctly gated today.)

### 4.6 Open questions for the design pass

- **Revert, warn-only, or fail?** Reverting matches the gate's semantics, but it is undoing something
  the author wrote. A guest AppHost that called `withEndpointCallback('probe', …)` and then
  `getEndpoint('probe')` would have the endpoint removed underneath a reference that was already
  taken, failing later at environment-gathering rather than at `BeforeStartEvent`. Warn-without-revert
  avoids that at the cost of leaving the real port-forward repointed.
- **Where the snapshot lives.** `ResolvedService.BridgeUnregistered`/`Bridge` is the natural chokepoint
  — every out-of-band facade passes through it — but that is a placement decision, not a measured one.
- **Mutations during `BeforeStartEvent` itself**, by a handler subscribed after the detector, are out
  of reach. Nothing does this today; it is a residual, not a known gap.
- **Scope beyond endpoints.** The same "Aspire mutates existing state without `WithAnnotation`" shape
  may exist for other annotation types on the handle's other 60-odd `Aspire.Hosting/*` capabilities.
  Only the endpoint ones were enumerated here.

## 5. Reproducing this

```bash
cd samples/DemoAppHostTypeScript && rm -rf .aspire
aspire restore --non-interactive --nologo          # Aspire CLI 13.5.3
grep -oE "'Aspire[^']*'" .aspire/modules/aspire.mts # read the ServiceResourceImpl capability ids
```

The runtime probes were throwaway xUnit facts in
`test/Aspire.Hosting.ServiceSources.Tests/`, reusing `EndpointSkipGapRepro`'s `UrlSource`/
`KubernetesSource` fixtures and reaching Aspire's internal generics via
`typeof(DistributedApplication).Assembly.GetType("Aspire.Hosting.ResourceBuilderExtensions")`
+ `MakeGenericMethod(typeof(ServiceResource))`.

## 6. Unrelated defect noticed while reading

`ServiceEndpointExtensions.cs` told guest-language authors to call
`withServiceHttpEndpoint()`/`withServiceHttpsEndpoint()` in its "service exposes no endpoint"
exception. Those shims went away with #313, so the message named an API that no longer existed.
Fixed on `main` since this was measured — it now names `withHttpEndpoint()`/`withHttpsEndpoint()`.
