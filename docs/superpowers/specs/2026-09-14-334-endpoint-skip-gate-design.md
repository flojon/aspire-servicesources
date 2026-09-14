# Endpoint calls that update an existing endpoint bypass the skip-and-warn gate (#334)

**Date:** 2026-09-14
**Status:** Draft
**Resolves:** #334 (`WithHttpEndpoint()`/`WithHttpsEndpoint()` called with the default endpoint
name on an out-of-band `ServiceResource` hits Aspire's in-place-update branch instead of the add
branch, so the call never reaches `ServiceResourceBuilder.WithAnnotation` and is silently
unreported in the skip-and-warn tally).
**Relates to:** [`2026-09-10-313-service-resource-dualwrite-bridge-design.md`](2026-09-10-313-service-resource-dualwrite-bridge-design.md)
(#313/#326) — this bug lives entirely inside the mechanism that design introduced, and its own §3
"Residual risk" note on the update branch already named half of what this document resolves.

---

## 1. The mechanism, verified against the actual pinned floor

Everything below was checked directly against this repo's pinned Aspire version
(`Directory.Build.props:74`, `AspireVersion = 13.5.2`) by decompiling
`~/.nuget/packages/aspire.hosting/13.5.2/lib/net8.0/Aspire.Hosting.dll` with `ilspycmd 11.0.0.9375`
— not assumed from the issue's paraphrase of it.

`WithHttpsEndpoint<T>`/`WithHttpEndpoint<T>` (`ResourceBuilderExtensions`) both forward to the
generic `WithEndpoint<T>(builder, port, targetPort, scheme, name, env, isProxied, isExternal,
protocol)` overload. Its body, decompiled in full:

```csharp
public static IResourceBuilder<T> WithEndpoint<T>(this IResourceBuilder<T> builder, int? port = null,
    int? targetPort = null, string? scheme = null, [EndpointName] string? name = null,
    string? env = null, bool? isProxied = null, bool? isExternal = null, ProtocolType? protocol = null)
    where T : IResourceWithEndpoints
{
    string text = scheme ?? (protocol ?? ProtocolType.Tcp).ToString().ToLowerInvariant();
    string resolvedName = name ?? text;
    EndpointAnnotation endpointAnnotation = builder.Resource.Annotations.OfType<EndpointAnnotation>()
        .FirstOrDefault(sb => string.Equals(sb.Name, resolvedName, StringComparisons.EndpointAnnotationName));
    if (endpointAnnotation != null)
    {
        if (port.HasValue) endpointAnnotation.Port = port;
        if (targetPort.HasValue) endpointAnnotation.TargetPort = targetPort;
        if (isExternal.HasValue) endpointAnnotation.IsExternal = isExternal.Value;
        if (isProxied.HasValue) endpointAnnotation.IsExplicitlyProxied = isProxied;
        ConfigureEndpointEnvironmentVariable(builder, endpointAnnotation, env);
        return builder;                                    // <-- UPDATE BRANCH: no WithAnnotation call
    }
    EndpointAnnotation endpointAnnotation2 = new EndpointAnnotation(...);
    ConfigureEndpointEnvironmentVariable(builder, endpointAnnotation2, env);
    return builder.WithAnnotation(endpointAnnotation2);     // <-- ADD BRANCH: goes through WithAnnotation
}
```

The branch selector is a name lookup on `builder.Resource.Annotations` — under this repo's design
(`ServiceResource facade`), that is the **facade's own collection**, which `ResolvedService.Bridge`
(`Sources/ResolvedService.cs:26-29`) pre-populates by copying every annotation instance `real`
already carries, *before* the AppHost author's own call runs:

- `UrlSource.Resolve` (`Sources/UrlSource.cs:52-61`) builds an `EndpointAnnotation` named
  `uri.Scheme` (`"http"` or `"https"`) directly on the facade — `url` has no `real`, so
  `BridgeUnregistered` is what puts it there, but the effect on the branch check is identical.
- `KubernetesSource.Resolve` (`Sources/KubernetesSource.cs:37`) calls
  `.WithEndpoint(..., name: scheme, ...)` on the real `ServiceExecutableResource` builder, and
  `Bridge` copies that same `EndpointAnnotation` instance onto the facade.
- `ContainerSource.Resolve` (`Sources/ContainerSource.cs:25`) does the identical thing for
  `container`, which is **not** an out-of-band source (`Reachability.OutOfBandSources` names only
  `"url"` and `"kubernetes"`), so this branch selection happens there too but has no gating
  consequence — see §4 for why `container` is out of scope.

So `service.WithHttpsEndpoint()` with no explicit `name:` resolves `resolvedName = "https"`, finds
the annotation the source itself already registered under that exact name, and takes the update
branch — which never calls `builder.WithAnnotation(...)`, the one point
`ServiceResourceBuilder` (`Sources/ServiceResourceBuilder.cs:118-153`) overrides to run
`Reachability.IsUnreachable` and either warn-and-skip or dual-write. The call is invisible to that
override entirely, for both the add-branch's gate and the add-branch's dual-write.

**Root-cause claim from the issue, verified true:** "for a new annotation" is exactly the qualifier
that matters, and it is Aspire's own code drawing that line — not something this package's design
overlooked so much as a case its dispatch model (`WithAnnotation` as the sole interception point)
structurally cannot see, because the update branch is a different code path inside Aspire's
extension method, not a different call into ours.

## 2. Confirming this predates nothing — it was introduced by #326

Before #326 (`243c227`, merged today, still entirely inside `[Unreleased]`), `AddService` returned
`IResourceBuilder<IResourceWithServiceDiscovery>`, and the **only** way to reach `WithHttpsEndpoint`
was through `Configure<IResourceWithEndpoints>(r => r.WithHttpsEndpoint())`
(`ServiceConfigurationExtensions.cs`, decompiled from `243c227~1`). `Configure<T>`'s gate
(`IsUnreachable<T>(annotation.Source)`) ran **before** `configure(...)` was ever invoked — i.e.
before Aspire's `WithEndpoint<T>` had a chance to look at the annotation collection at all. Whether
that inner call would itself have taken the add or update branch was irrelevant: the whole lambda
was skipped or run as one unit. `IResourceBuilder<IResourceWithServiceDiscovery>` also could not
bind Aspire's own `WithHttpsEndpoint<T>` extension directly (`T` would have to satisfy
`IResourceWithEndpoints`, which `IResourceWithServiceDiscovery` does not implement), so no native
call bypassing `Configure<T>` was even possible to write.

This gap is new in #326: it is a direct consequence of `ServiceResourceBuilder.WithAnnotation`
replacing `Configure<T>`'s per-call gate with a per-`WithAnnotation`-invocation gate, which
(correctly, for every other native method) intercepts less than the whole call and (here,
incorrectly) intercepts *nothing* for the update branch.

**CHANGELOG consequence:** per `CHANGELOG.md`'s own stated rule ("A bug introduced and fixed within
the same `[Unreleased]` cycle never shipped... nothing for a consumer to have hit"), and since #326
has not been released, **no `### Fixed` entry is warranted regardless of what this ticket ships** —
the dual-write bridge feature simply ships with this gap closed (or documented) the first time,
with no separate changelog line calling out a "fix."

## 3. Fix shape: accept and document (issue's option 3)

### 3.1 Why option 1 (report at `BeforeStartEvent` by diffing) does not work

Option 1 needs some observable difference between "the source's own registration" and "state after
the AppHost's call" to detect that a call happened at all. But look again at the update branch: every
mutation is gated on the corresponding parameter's `.HasValue` — `if (port.HasValue) ... `. The
ticket's own primary repro is `service.WithHttpsEndpoint()` with **no arguments** — every one of
`port`, `targetPort`, `isExternal`, `isProxied` is `null`, so **the update branch mutates nothing**.
The `EndpointAnnotation` after the call is reference- and value-identical to the one the source
registered. There is no diff to find, at `BeforeStartEvent` or anywhere else that only inspects
end state: the call and its absence are indistinguishable from outside Aspire's own code, because
Aspire's own code is the only thing that ever saw the call happen. This rules the option out for
the exact case the ticket exists to fix, not just makes it awkward — it is verified infeasible, not
merely costly.

(A version of option 1 restricted to "differs after a call that *did* pass arguments" would catch
part of §4's mutation-with-args case, but not the no-arg case the ticket's acceptance criterion 1
requires, so it does not clear the bar on its own either.)

### 3.2 Why option 2 (sources don't pre-register the default name) is out of scope for a bug fix

The `"https"`/`"http"` pre-registration on `url`/`kubernetes` (and `container`, which shares the
mechanism though it is not gated) is exactly what lets a consumer read
`service.GetEndpoint("https")` or `WithReference(service)` **without the AppHost author calling
`WithHttpsEndpoint()` at all** — the ordinary case for these sources, where the endpoint is data the
source already knows (the configured URL; the port-forward's local port), not something an AppHost
author configures. Moving that registration off the name Aspire's own helpers default to would mean
every such consumer breaks unless the AppHost author now explicitly calls `WithHttpsEndpoint()`
first — a behavior change to the wire contract between a source and its consumers, felt by every
existing AppHost using `url`/`kubernetes`/`container`, not a targeted fix to a reporting gap. It
would also very likely trip this repo's own size-gate predicates (config/wire-shape change,
multiple sources touched) were it scoped as its own ticket — which is the right way to scope it, if
it is ever pursued, not folded into #334.

### 3.3 Decision

**Accept the gap and document it** — issue option 3, explicitly offered by the issue author as
acceptable ("Happy to leave this open as documentation-only... the functional behaviour is correct
in every case I could construct" — see §4 for where that last claim needs a caveat).

Concretely:

1. **README.md**, in the native-vocabulary skip section (`README.md:1761-1792`): add a paragraph
   immediately after the existing "Native calls are skipped..." paragraph, stating that a call
   naming an endpoint the source has *already* declared under that name (the default `"http"`/
   `"https"` an AppHost author gets by omitting `name:`) is not reported, because Aspire's own
   `WithHttpEndpoint`/`WithHttpsEndpoint` mutate the existing annotation in place rather than
   calling back into this package's interception point — so the skip tally is not exhaustive for
   this one case. Name the workaround: passing an explicit, not-already-used `name:` *is* reported
   correctly (`NewEndpoint_OnUrlSource_IsSkippedAndReported`), so an AppHost author who needs the
   call to be visibly gated can use a distinct name.
2. **`ResolvedService.Bridge`'s existing comment** (`Sources/ResolvedService.cs:20-25`): it already
   explains why instance-sharing is what keeps the update branch's mutation visible on both
   collections. Extend it with the reachability-gate implication this ticket surfaces: the same
   sharing that makes the mutation *correct* for a reachable source is what makes it invisible to
   `Reachability.IsUnreachable` for an out-of-band one — the update branch bypasses
   `ServiceResourceBuilder.WithAnnotation` entirely, so it is neither gated nor reported. Cross-
   reference this spec and the README section by name so a future reader lands on the reasoning,
   not just the fact.
3. **No code changes to `Reachability`, `ServiceResourceBuilder`, or any source.** Nothing here
   changes behavior; it only closes acceptance item 5 by putting the implication of the existing
   comment into words, and closes the ticket's documentation half.

### 3.4 What "accept" does *not* mean for the repro test

The repro test in the ticket asserts `DefaultNamedEndpoint_OnUrlSource_IsSkippedAndReported` should
pass (i.e. the call *is* reported) — that assertion is the reporting gap this document is choosing
not to close. If the Plan phase commits `EndpointSkipGapRepro.cs` as written in the issue, that one
case must be **inverted**, not left red: assert and pin the *actual, now-documented* behavior — no
warning is recorded, and the pre-existing endpoint annotation is mutated in place exactly as
Aspire's own update branch already does (a no-op for the no-arg repro call, since every mutation is
gated on the corresponding argument being non-null) — with a comment
pointing at the README section and this spec, so a future change to Aspire's own `WithEndpoint`
branch logic — which would silently start reporting, or silently stop skip-gating the property
mutations — is caught by a test failure rather than discovered again the way this ticket was.
`NewEndpoint_OnUrlSource_IsSkippedAndReported` and
`DefaultNamedEndpoint_OnKubernetesSource_DoesNotChangeThePortForwardsEndpoint` already pass and stay
as regression coverage of what continues to work correctly. See [Open Questions](#open-questions)
for the exact shape this takes in the Plan phase.

## 4. Widened finding, out of this ticket's scope: the update branch also mutates properties in place

Re-reading the decompiled body in §1 with an eye on the issue's own severity claim ("The resolved
URL is not corrupted... I went looking for a worse consequence and did not find one"): that claim
was checked only against the no-argument repro (`service.WithHttpsEndpoint()`). The update branch's
four `if (x.HasValue) endpointAnnotation.X = x` lines are **not** gated by source or reachability at
all — they run whenever the AppHost author passes an explicit `port`, `targetPort`, `isExternal`, or
`isProxied` argument to a call that resolves to an already-registered name, regardless of source.

For `kubernetes`, that annotation instance is the **same object** `KubernetesSource.Resolve`
registered on the real, DCP-registered `ServiceExecutableResource` (shared by `ResolvedService.Bridge`,
§2's comment). So `service.WithHttpsEndpoint(port: 9999)` on a `kubernetes`-sourced service whose
declared name matches (the default, unnamed case) does not just go unreported — it silently repoints
the real `kubectl port-forward` process's configured port, in place, with no gate and no warning.
This is the exact "configuration reaching the wrong process" failure mode
`Reachability`/`ServiceConfigurationExtensions.IsUnreachable` exists to prevent (see
`ServiceResourceBuilder.cs:5-11`'s own framing), happening through a path neither this design nor
the original `Configure<T>` gate ever covered, because — like the reporting gap — it is entirely
inside Aspire's own update branch.

**This is a distinct, more severe defect from #334's reporting gap**, not a rephrasing of it: #334
is "a developer isn't told their call was ignored"; this is "a developer's call was not ignored, and
landed somewhere unintended, still with no warning." It shares the exact same root cause and would
very likely be closed by the same investigation, but fixing it is not what accepting §3.3's
documentation-only shape does, and it is outside the acceptance checklist this spec was scoped
against (which frames the bug as reporting-only, following the issue). Flagging it here rather than
building a fix for it, per this skill's scope discipline. See
[Open Questions](#open-questions) for the recommended disposition.

`container` is excluded from this finding's scope even though `ContainerSource` shares the same
default-name pre-registration mechanism: `container` is not in `Reachability.OutOfBandSources`, so
native calls against it are never gated or skipped in the first place — a mutation reaching its real
resource is the intended, reachable behavior, not a bypass of anything.

## 5. Test and acceptance-criteria disposition

| Acceptance item | Disposition |
|---|---|
| 1. Default-named endpoint call on `url` reported in tally | **Not implemented** — accepted and documented per §3.3, not fixed. The gap itself is the "documentation-only" resolution the issue explicitly allows. |
| 2. No functional regression (resolved URL/env correct; kubernetes tuples unmutated) | Unaffected — this document proposes no code change, so nothing regresses. §4's widened finding is a **pre-existing, undocumented-until-now** gap in this guarantee for the with-arguments case, not something this spec's own change introduces or worsens. |
| 3. `EndpointSkipGapRepro.cs` committed | Recommended for the Plan phase, with the first case's assertion inverted per §3.4 — see Open Questions. |
| 4. Pick one of three fix shapes | **Option 3 (accept + document)**, per §3.3, with options 1 and 2 ruled out on verified technical grounds (§3.1, §3.2) rather than by preference. |
| 5. Revisit `ResolvedService.Bridge`'s comment | Done in this spec's plan-facing instructions (§3.3 item 2) — the Plan phase writes the actual comment edit. |

## Attack surface

None added or changed. This document proposes documentation edits only (a README paragraph, a code
comment) — no new code path, no new input parsed, no new trust boundary. The one behavior this
ticket concerns itself with (an endpoint call silently reaching, or not reaching, an already-running
out-of-band process) is pre-existing in Aspire's own `WithEndpoint` update branch and is not created,
widened, or narrowed by anything this spec proposes; §4 names a widened understanding of that
pre-existing behavior's severity, not a new one.

## Open Questions

1. **Confirm doc-only is the accepted resolution.** The issue author offered it explicitly as
   acceptable, and §3.1/§3.2 rule the two functional alternatives out on verified technical grounds
   rather than mere preference — but shipping a known, permanent reporting gap (rather than a
   temporary one pending a fix) is still a product call, not purely a technical one, and belongs
   with a human before the Plan phase proceeds.
2. **§4's widened finding — file as a new issue, or fold into this ticket's plan?** Recommended:
   file as a **new, separate issue** once a human confirms severity and priority. It is a distinct
   defect (silent mutation of a real, running process's configuration, not merely an unreported
   skip) discovered while investigating #334, sharing #334's root cause but not its acceptance
   criteria. Building a fix for it was not attempted here, in keeping with this skill's
   out-of-scope-finding handling — reported, not built.
3. **Exact shape of the repro-test disposition (§3.4).** Recommend the Plan phase commit
   `EndpointSkipGapRepro.cs` with `DefaultNamedEndpoint_OnUrlSource_IsSkippedAndReported` rewritten
   to assert the current (accepted, documented) behavior — no warning, annotation state unchanged by
   a no-arg call — rather than the issue's original "should be reported" assertion, and with a
   fourth case added covering §4's with-arguments mutation for `kubernetes` (asserting it happens,
   as a pinned-but-flagged regression test, with a comment pointing at Open Question 2) so that
   whoever eventually fixes the widened finding has a failing test ready to turn green. Whether to
   add that fourth case now or leave it for the new issue in Open Question 2 to introduce is a Plan-
   phase judgment call, not resolved here.
