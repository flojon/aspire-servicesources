# Raw WithEndpoint overloads bypass the skip-and-warn gate, with real mutation (#335)

**Date:** 2026-09-15
**Status:** Draft
**Resolves:** #335 (an explicit argument to a call resolving to an already-registered endpoint
name mutates the shared `EndpointAnnotation` in place, with no `Reachability` gate and no warning
at all — the same root cause as #334, but for mutation rather than just reporting).
**Relates to:** [`2026-09-14-334-endpoint-skip-gate-design.md`](2026-09-14-334-endpoint-skip-gate-design.md)
(#334) — that design's §3.6/§4 named this exact remaining surface ("the raw `WithEndpoint`
overloads... at least as broken... not built, not filed [there], consistent with this document's
scope discipline") and its own shadow fix (PR #339) already closed #335's scenario for
`WithHttpEndpoint`/`WithHttpsEndpoint`'s primary (nullable-`isProxied`) overload as a side effect.
**Spins off:** #352 (raw `WithEndpoint` callback overload's add branch never dual-writes a new
named endpoint onto `real`, for *reachable* sources — a distinct, independently-fixable bug found
while decompiling for this design) and #353 (investigation, not yet a confirmed bug: whether
guest-language endpoint callbacks reach Aspire's internal `WithEndpointCallback`/
`WithHttpEndpointCallback`/`WithHttpsEndpointCallback` in a way C# shadowing cannot intercept).

---

## 1. What #334's fix already closed, and what it left open

PR #339 added two non-generic shadow overloads — `WithHttpEndpoint`/`WithHttpsEndpoint` on
`IResourceBuilder<ServiceResource>`, matching Aspire's nullable-`isProxied` public signature
exactly — each gating via `Reachability.IsUnreachable(typeof(EndpointAnnotation), source)` before
delegating to Aspire's own implementation by fully-qualified static call. Its own design doc §3.4
Probe 2 proved this closes #335's exact scenario (`WithHttpsEndpoint(port: 9999)` leaving
`preexisting.Port` unchanged) — but **only** for that one overload on those two methods.

Decompiling `Aspire.Hosting.dll` 13.5.2's `ResourceBuilderExtensions` in full (`ilspycmd
11.0.0.9375`, same pinned floor `Directory.Build.props:74` names) turns up six more call surfaces
sharing the identical root cause — none shadowed today, so a call through any of them still hits
Aspire's ungated update branch (or, for the callback overload, an update branch with no gate on
*either* branch):

| # | Signature | Attribute (Aspire's own) | Forwards to |
|---|---|---|---|
| 1 | `WithEndpoint<T>(port=null, targetPort=null, scheme=null, name=null, env=null, isProxied=null, isExternal=null, protocol=null)` | `[AspireExport]` | primary — the overload `WithHttpEndpoint`/`WithHttpsEndpoint` themselves forward to |
| 2 | `WithEndpoint<T>(port, targetPort, scheme, name, env, bool isProxied, isExternal, protocol)` | `[AspireExportIgnore("Binary compatibility shim for the nullable isProxied overload.")]` | #1, via `(bool?)isProxied` cast |
| 3 | `WithEndpoint<T>(port, targetPort, scheme, name, env, isProxied?, isExternal)` (no `protocol`) | `[AspireExportIgnore("Subset of the full WithEndpoint overload which is already exported.")]` | #1, with `protocol: null` |
| 4 | `WithEndpoint<T>(port, targetPort, scheme, name, env, bool isProxied, isExternal)` (no `protocol`) | `[AspireExportIgnore("Binary compatibility shim for the nullable isProxied overload.")]` | #1 directly, via `(bool?)isProxied` cast and `protocol: null` — not via #3, despite the identical parameter list; verified from the decompiled body, not assumed from the signature |
| 5 | `WithEndpoint<T>(endpointName, Action<EndpointAnnotation> callback, createIfNotExists=true)` | `[AspireExportIgnore("Polyglot app hosts use the internal withEndpointCallback export...")]` | nothing — self-contained, see §2 |
| 6 | `WithHttpEndpoint<T>(port, targetPort, name, env, bool isProxied)` | `[AspireExportIgnore("Binary compatibility shim for the nullable isProxied overload.")]` | `WithHttpEndpoint` #1-equivalent, via `(bool?)isProxied` cast |
| 7 | `WithHttpsEndpoint<T>(port, targetPort, name, env, bool isProxied)` | `[AspireExportIgnore("Binary compatibility shim for the nullable isProxied overload.")]` | `WithHttpsEndpoint` #1-equivalent, via `(bool?)isProxied` cast |

#1-#4 and #6-#7's bodies are structurally identical to what #334 already fixed for
`WithHttpEndpoint`/`WithHttpsEndpoint`'s primary overload: an update branch gated per-parameter on
`.HasValue` that returns without ever calling `WithAnnotation`, and an add branch that does call
`WithAnnotation` (so already dual-writes and is already gated correctly today for a genuinely new
name). #5 is qualitatively different — see §2.

All seven are confirmed reachable from a `ServiceResourceBuilder`-typed receiver today: `ServiceResource`
implements `IResourceWithEndpoints` directly (`ServiceResource.cs:31-36`), which is the only
constraint `WithEndpoint<T>`/`WithHttpEndpoint<T>`/`WithHttpsEndpoint<T>` require, and `AddService`
returns the concrete `IResourceBuilder<ServiceResource>` (per #334's design §3.3) — the same
condition that let #339's shadows bind in the first place applies unchanged to all seven of these.

## 2. The callback overload is a different shape of the same bug, and worse

```csharp
[AspireExportIgnore(Reason = "Polyglot app hosts use the internal withEndpointCallback export, which exposes EndpointUpdateContext instead of EndpointAnnotation.")]
public static IResourceBuilder<T> WithEndpoint<T>(this IResourceBuilder<T> builder,
    [EndpointName] string endpointName, Action<EndpointAnnotation> callback, bool createIfNotExists = true)
    where T : IResourceWithEndpoints
{
    EndpointAnnotation endpointAnnotation = builder.Resource.Annotations.OfType<EndpointAnnotation>()
        .SingleOrDefault(ea => string.Equals(ea.Name, endpointName, StringComparisons.EndpointAnnotationName));
    if (endpointAnnotation != null)
    {
        callback(endpointAnnotation);                          // <-- UPDATE BRANCH: no gate, arbitrary mutation
    }
    if (endpointAnnotation == null && createIfNotExists)
    {
        endpointAnnotation = new EndpointAnnotation(...);
        callback(endpointAnnotation);
        builder.Resource.Annotations.Add(endpointAnnotation);   // <-- ADD BRANCH: no WithAnnotation call either
    }
    else if (endpointAnnotation == null)
    {
        return builder;
    }
    return builder;
}
```

For an existing name (the #335 scenario), the callback runs directly against the shared instance
with **no gate at all** and **no constraint on what it mutates** — every other overload in this
document only ever touches `Port`/`TargetPort`/`IsExternal`/`IsExplicitlyProxied`/the env-var
callback; this one hands the caller the live `EndpointAnnotation` object itself. For a
`kubernetes`-sourced service, that is Aspire's own framing of exactly the failure mode
`Reachability` exists to prevent, with no ceiling on what changes.

The add branch's own defect (never dual-writing a brand-new name onto `real` even when reachable)
is a **separate** bug from the reachability gate this design closes — filed as #352, out of this
document's scope. What #335 needs from this overload is the **update-branch gate only**: skip the
callback entirely (never invoke it) and warn when unreachable; delegate to Aspire's implementation,
unmodified, when reachable. #352's dual-write gap already exists on the reachable path regardless
of whether this design ships, so choosing to delegate unmodified doesn't introduce or worsen it.

## 3. Fix shape: seven more shadows, same mechanism as #339, zero changes to `GateEndpointCall`

Add seven non-generic shadow overloads to `ServiceSourcesBuilderExtensions`, each mirroring its
Aspire counterpart's signature and attribute exactly (so no AppHost-visible signature change, and
no change to guest-language projection for the ones Aspire already excludes from it), each calling
the existing private `GateEndpointCall(builder)` helper unchanged, each delegating to Aspire's
matching overload by fully-qualified static call (`Aspire.Hosting.ResourceBuilderExtensions.XXX`)
when reachable — never through extension-method syntax on `builder`, which would recurse into the
shadow itself, exactly as #339 already established.

`GateEndpointCall` needs **no changes**: it already keys purely on `typeof(EndpointAnnotation)` via
`Reachability.IsUnreachable`/`CapabilityLabel`, with no dependency on which method name called it.
The seven new shadows are seven new call sites for the same helper, not a new gating mechanism.

**For the callback overload specifically** (#5 above), the shadow's body is:

```csharp
[AspireExportIgnore(Reason = "Polyglot app hosts use the internal withEndpointCallback export, which exposes EndpointUpdateContext instead of EndpointAnnotation.")]
public static IResourceBuilder<ServiceResource> WithEndpoint(
    this IResourceBuilder<ServiceResource> builder,
    [EndpointName] string endpointName, Action<EndpointAnnotation> callback, bool createIfNotExists = true)
{
    if (GateEndpointCall(builder))
    {
        return builder;
    }

    return Aspire.Hosting.ResourceBuilderExtensions.WithEndpoint(builder, endpointName, callback, createIfNotExists);
}
```

When unreachable, the callback is never invoked at all — there is no partial-mutation state to
reason about, unlike the numeric overloads where individual `.HasValue` properties are naturally
all-or-nothing per call already. When reachable, this delegates to Aspire's implementation exactly
as it stands today, inheriting #352's separate add-branch dual-write gap unchanged (not this
design's concern to fix).

### 3.1 `Reachability.CapabilityLabel` needs to widen, not restructure

Today: `nameof(EndpointAnnotation) => "WithHttpEndpoint/WithHttpsEndpoint"`. A skip triggered by a
raw `WithEndpoint` call would report that label verbatim — accurate that *an* endpoint call was
skipped, inaccurate about *which* method the AppHost author actually wrote. Change the string to
`"WithEndpoint/WithHttpEndpoint/WithHttpsEndpoint"`. This is additive to the label text only — no
change to `IsUnreachable`, no new dictionary key, no per-method dispatch — because the underlying
design decision (every method that resolves to a pre-existing `EndpointAnnotation`-named lookup is
the same capability, gated the same way, keyed by annotation type not call site) is exactly right
and #334 already established it; the label was just written before a second and third method existed
to report through it. `EndpointSkipGapRepro.cs`'s existing assertions use `.Contains(...)`, not
`.Equals(...)`, so this is backward compatible with every test written against #334's fix.

### 3.2 Compat shims: verified real, not hypothetical

All three `WithEndpoint<T>` compat shims (#2-#4) and both `WithHttpEndpoint`/`WithHttpsEndpoint`
`bool`-`isProxied` shims (#6-#7) are real, currently-shipping public API in Aspire 13.5.2 — not
speculative binary-compat scaffolding that might not exist. An AppHost author who has any code
compiled against an older Aspire minor version, or who explicitly writes a literal
`isProxied: true`/`isProxied: false` (binding to the `bool` overload rather than the `bool?` one),
reaches one of these today. Per this design's approval, all seven are shadowed — no overload in
this family is left deliberately open the way #339 left #6/#7 open pending this ticket.

### 3.3 What stays out of scope

- **#352** (callback overload's add-branch dual-write gap for reachable sources) — a distinct bug,
  not a gating gap, filed separately as its own fix.
- **#353** (guest-language endpoint callbacks potentially bypassing `Reachability` through Aspire's
  own internal `WithEndpointCallback`/`WithHttpEndpointCallback`/`WithHttpsEndpointCallback`) — not
  yet confirmed to be a real gap, and not closable by more C# shadowing even if confirmed, since
  those three methods are `internal` to `Aspire.Hosting.dll` and never resolved via extension-method
  overload resolution at any call site this package's shadows can compete for. Tracked as its own
  investigation.
- **`container`** — excluded from `Reachability.OutOfBandSources` exactly as #334 excluded it: a
  mutation reaching `container`'s real resource is intended, reachable behavior, not a bypass.

## 4. Tests

Extend `EndpointSkipGapRepro.cs` (same file #334's repro lives in — this is the direct continuation
of that repro's own scope note: "the *with*-arguments mutation risk is #335, out of this file's
scope," which stops being true once this fix lands) with:

- One url-source case per shadowed overload proving the raw call is skipped and reported with the
  widened label — at minimum the primary `WithEndpoint` overload and the callback overload (the two
  an AppHost author would plausibly write by hand); the four remaining compat shims get at least one
  direct-call test each so a future accidental de-shadowing regresses visibly, even though their
  production likelihood is low.
- A kubernetes-source case mirroring
  `DefaultNamedEndpoint_OnKubernetesSource_WithArguments_DoesNotChangeThePortForwardsEndpoint` but
  through the raw `WithEndpoint(port: 9999)` call instead of `WithHttpsEndpoint(port: 9999)` —
  proving the shared, real port-forward annotation instance stays unmutated through this path too.
- A kubernetes-source case for the callback overload: `service.WithEndpoint("https", e => e.Port =
  9999)` must leave the real annotation's `Port` unchanged and record exactly one warning, with the
  callback provably never invoked (e.g. a closure flag asserted `false`) — distinguishing "gated
  before the callback ran" from "callback ran but its effect was reverted," which matters more here
  than elsewhere because this callback can mutate fields no other overload exposes.
- A reachable-source (`local` or `container`) sanity check for at least the primary `WithEndpoint`
  and the callback overload, confirming the shadow still delegates through correctly (no behavior
  change on the happy path) — mirroring the existing add-branch coverage pattern already in the
  test suite for `WithHttpEndpoint`/`WithHttpsEndpoint`.

No inversion of any existing #334 assertion; this is purely additive coverage.

## 5. Documentation touch-ups

- `ResolvedService.cs`'s bridge comment (already extended once by #339 to explain the second
  interception point) gets a further short addendum: the shadow overloads now number nine across
  three methods (`WithEndpoint`, `WithHttpEndpoint`, `WithHttpsEndpoint`) rather than two, still all
  funneling through the same `GateEndpointCall`/`Reachability` table — not a structural change to
  the comment's claim, just its count.
- `Reachability.CapabilityLabel`'s remarks already document why `EndpointAnnotation`'s label is a
  literal method name rather than the type name; update the literal itself per §3.1 and note it now
  serves three call-site families, not two.
- No `README.md` change needed beyond what's already true: the skip-warning section describes
  behavior generically ("native calls skipped and logged"), which becomes accurate for these seven
  overloads too without new prose.

## 6. CHANGELOG disposition

No `### Fixed` entry. Per `CHANGELOG.md`'s own stated rule, and identically to #339's own
disposition: the gap predates no release (introduced by #326, still `[Unreleased]`; #334/#339's own
fix is also still `[Unreleased]`), so there is nothing a consumer could have hit. The dual-write
bridge feature ships with this gap closed the first time it's ever released, with no line calling
out a "fix" for behavior that never shipped broken.

## Attack surface

None added, for the same reasons #339's own design gave: no new input parsing, no new shell/URL/
path/query construction, no new trust or process boundary. Seven more call sites now consult the
same `Reachability`/`GateEndpointCall` table `ServiceResourceBuilder.WithAnnotation` and #339's two
shadows already consult — reused, not duplicated, logic — so none of the nine total interception
points can silently drift from what the others consider unreachable.

## Open Questions

None outstanding — scope was settled through direct questions during brainstorming: cover all seven
overloads including every compat shim (not just the two common ones), shadow the callback overload
too (skip-and-warn, never partially invoke it), and close the `WithHttpEndpoint`/`WithHttpsEndpoint`
`bool`-`isProxied` shims #339 had left open, all in this same fix. The two genuinely separate findings
surfaced while decompiling (#352, #353) are filed and out of this document's scope by design, not by
oversight.
